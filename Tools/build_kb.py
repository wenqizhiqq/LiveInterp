"""Build the offline translation knowledge base.

Sources:
  * OPUS Tatoeba cmn-en (CC-BY 2.0 FR)  https://opus.nlpl.eu/Tatoeba/
  * CC-CEDICT (CC-BY-SA 3.0)            https://www.mdbg.net/chinese/dictionary?page=cedict

Run with a Python that has `opencc-python-reimplemented` installed:
  python Tools/build_kb.py
"""
from __future__ import annotations

import gzip
import os
import re
import sqlite3
import unicodedata
import zipfile
from datetime import datetime, timezone

from opencc import OpenCC

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KB_DIR = os.path.join(ROOT, "Data", "knowledge")
SOURCE_DIR = os.path.join(ROOT, "Tools", "kb_source")
TATOEBA_ZIP = os.path.join(SOURCE_DIR, "cmn-en.txt.zip")
CEDICT_GZ = os.path.join(SOURCE_DIR, "cedict.txt.gz")
DB_PATH = os.path.join(KB_DIR, "liveinterp-kb.db")

cc = OpenCC("t2s")
PUNCT_RE = re.compile(r"[\s\W_]+", re.UNICODE)


def norm_zh(text: str) -> str:
    text = cc.convert(text)
    text = unicodedata.normalize("NFKC", text)
    return PUNCT_RE.sub("", text).lower()


def norm_en(text: str) -> str:
    text = unicodedata.normalize("NFKC", text)
    return PUNCT_RE.sub("", text).lower()


def clean_en(text: str) -> str:
    text = unicodedata.normalize("NFKC", text).strip()
    text = re.sub(r"\s+", " ", text)
    return text


def english_score(en: str) -> int:
    """Lower is better. Prefer clean, neutral, medium-length sentences."""
    score = 0
    if not en.endswith((".", "?", "!")):
        score += 3
    if any(ch in en for ch in '()[]{}"“”'):
        score += 4
    if "..." in en or "…" in en:
        score += 3
    if len(en) < 6:
        score += 4
    if len(en) > 120:
        score += 3
    return score


def load_tatoeba() -> dict[str, tuple[str, str]]:
    with zipfile.ZipFile(TATOEBA_ZIP) as zf:
        names = zf.namelist()
        cmn_name = next(n for n in names if n.endswith(".cmn"))
        en_name = next(n for n in names if n.endswith(".en"))
        with zf.open(cmn_name) as zh_file, zf.open(en_name) as en_file:
            zh_lines = zh_file.read().decode("utf-8").splitlines()
            en_lines = en_file.read().decode("utf-8").splitlines()

    if len(zh_lines) != len(en_lines):
        raise RuntimeError(f"Tatoeba pair mismatch: {len(zh_lines)} vs {len(en_lines)}")

    best: dict[str, tuple[str, str]] = {}
    for zh_raw, en_raw in zip(zh_lines, en_lines):
        zh_simplified = cc.convert(zh_raw.strip())
        en = clean_en(en_raw)
        key = norm_zh(zh_simplified)
        if not (3 <= len(key) <= 80):
            continue
        if not re.search(r"[\u4e00-\u9fff]", key):
            continue
        if not en or not re.search(r"[A-Za-z]", en) or re.search(r"[\u4e00-\u9fff]", en):
            continue
        if any(x in en.lower() for x in ("http://", "https://", "www.", "todo")):
            continue
        if len(en) > 200 or en == zh_simplified:
            continue
        current = best.get(key)
        if current is None or english_score(en) < english_score(current[1]):
            best[key] = (zh_simplified, en)
    return best


CEDICT_RE = re.compile(r"^(\S+)\s+(\S+)\s+\[([^\]]+)\]\s+/(.+)/$")
SKIP_DEF_PREFIXES = (
    "variant of ", "old variant of ", "see ", "surname ", "used in ", "abbr. for ",
    "classifier for ", "taiwan pr. ", "also written ", "erhua variant of ", "cl:",
)


def load_cedict() -> dict[str, tuple[str, str]]:
    terms: dict[str, tuple[str, str]] = {}
    with gzip.open(CEDICT_GZ, "rt", encoding="utf-8") as fh:
        for line in fh:
            if line.startswith("#"):
                continue
            match = CEDICT_RE.match(line.strip())
            if not match:
                continue
            _trad, simplified, _pinyin, definitions = match.groups()
            simp = cc.convert(simplified)
            key = norm_zh(simp)
            if not (2 <= len(key) <= 12):
                continue
            glosses = []
            for definition in definitions.split("/"):
                definition = definition.strip()
                if not definition or definition.lower().startswith(SKIP_DEF_PREFIXES):
                    continue
                if len(definition) > 90:
                    continue
                glosses.append(definition)
            if not glosses:
                continue
            gloss = clean_en(glosses[0])
            if not re.search(r"[A-Za-z]", gloss) or re.search(r"[\u4e00-\u9fff]", gloss):
                continue
            if gloss.lower() == simp.lower():
                continue
            current = terms.get(key)
            if current is None or len(gloss) < len(current[1]):
                terms[key] = (simp, gloss)
    return terms


def build(pairs: dict[str, tuple[str, str]], terms: dict[str, tuple[str, str]]):
    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
    db = sqlite3.connect(DB_PATH)
    try:
        db.executescript(
            """
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE pairs(id INTEGER PRIMARY KEY, zh TEXT NOT NULL, en TEXT NOT NULL,
                               zh_norm TEXT NOT NULL, en_norm TEXT NOT NULL, source TEXT NOT NULL);
            CREATE TABLE terms(id INTEGER PRIMARY KEY, zh TEXT NOT NULL, en TEXT NOT NULL,
                               zh_norm TEXT NOT NULL, source TEXT NOT NULL);
            """
        )
        db.executemany(
            "INSERT INTO pairs(zh, en, zh_norm, en_norm, source) VALUES (?,?,?,?,?)",
            [(zh, en, key, norm_en(en), "Tatoeba") for key, (zh, en) in pairs.items()],
        )
        db.executemany(
            "INSERT INTO terms(zh, en, zh_norm, source) VALUES (?,?,?,?)",
            [(zh, en, key, "CC-CEDICT") for key, (zh, en) in terms.items()],
        )
        db.executemany(
            "INSERT INTO meta(key, value) VALUES (?,?)",
            [
                ("pairs", str(len(pairs))),
                ("terms", str(len(terms))),
                ("tatoeba_version", "v2026-07-08"),
                ("pairs_license", "CC-BY 2.0 FR (Tatoeba)"),
                ("terms_license", "CC-BY-SA 3.0 (CC-CEDICT)"),
                ("built_utc", datetime.now(timezone.utc).isoformat(timespec="seconds")),
            ],
        )
        db.execute("CREATE INDEX idx_pairs_norm ON pairs(zh_norm)")
        db.execute("CREATE INDEX idx_pairs_en_norm ON pairs(en_norm)")
        db.execute("CREATE INDEX idx_terms_norm ON terms(zh_norm)")
        db.commit()
        db.execute("VACUUM")
    finally:
        db.close()


def main():
    print("loading Tatoeba...")
    pairs = load_tatoeba()
    print("pairs:", len(pairs))
    print("loading CC-CEDICT...")
    terms = load_cedict()
    print("terms:", len(terms))
    build(pairs, terms)
    print("db:", DB_PATH, os.path.getsize(DB_PATH), "bytes")
    samples = [
        "大家好，欢迎参加今天的会议。",
        "今天天气很好。",
        "我们下周继续讨论这个问题。",
        "请你再说一遍。",
        "你能听到我吗？",
    ]
    db = sqlite3.connect(DB_PATH)
    for sample in samples:
        key = norm_zh(sample)
        row = db.execute("SELECT en FROM pairs WHERE zh_norm=?", (key,)).fetchone()
        term = db.execute("SELECT en FROM terms WHERE zh_norm=?", (key,)).fetchone()
        print(sample, "=>", row[0] if row else (term[0] if term else "-"))
    db.close()


if __name__ == "__main__":
    main()

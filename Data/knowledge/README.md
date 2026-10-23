# 离线知识库

`liveinterp-kb.db` 由 `Tools/build_kb.py` 生成，包含：

- **46,121 条中英句对**（Tatoeba，去重后），用于整句/近似句检索。
- **97,069 条中英术语**（CC-CEDICT），用于短句术语兜底。

## 来源与许可

- Tatoeba 中英句对：https://tatoeba.org/ ，CC-BY 2.0 FR。
- CC-CEDICT：https://www.mdbg.net/chinese/dictionary?page=cedict ，CC-BY-SA 3.0。
- 离线翻译模型：Helsinki-NLP/opus-mt-zh-en，CC-BY 4.0；ONNX 量化权重来自 Xenova/opus-mt-zh-en。

本目录的数据库是上述数据的衍生整理，分发时请保留本说明与许可信息。

## 重建

```bash
python -m pip install opencc-python-reimplemented
python Tools/build_kb.py
```

原始语料放在 `Tools/kb_source/`，运行时会生成 `Data/knowledge/liveinterp-kb.db`。

import os, time, json
import numpy as np
import onnxruntime as ort
import sentencepiece as spm

root = r"D:\wqz\code\agi\LiveInterp\Models\opus-mt-zh-en"
src_sp = spm.SentencePieceProcessor(model_file=os.path.join(root, "source.spm"))
tgt_sp = spm.SentencePieceProcessor(model_file=os.path.join(root, "target.spm"))
vocab = json.load(open(os.path.join(root, "vocab.json"), encoding="utf-8"))
id2piece = {v: k for k, v in vocab.items()}

print("source spm pieces:", src_sp.get_piece_size(), "target:", tgt_sp.get_piece_size())
print("source special pad/eos/unk:", src_sp.pad_id(), src_sp.eos_id(), src_sp.unk_id())
print("target special pad/eos/unk:", tgt_sp.pad_id(), tgt_sp.eos_id(), tgt_sp.unk_id())
print("vocab special <pad>/</s>/<unk>:", vocab["<pad>"], vocab["</s>"], vocab["<unk>"])

# piece/id alignment checks
for i in [0, 1, 2, 3, 100, 3833, 65000]:
    if i < src_sp.get_piece_size():
        p = src_sp.id_to_piece(i)
        print(i, repr(p), "vocab_id=", vocab.get(p))

sess_enc = ort.InferenceSession(os.path.join(root, "encoder_model_quantized.onnx"), providers=["CPUExecutionProvider"])
sess_dec = ort.InferenceSession(os.path.join(root, "decoder_model_quantized.onnx"), providers=["CPUExecutionProvider"])
print("encoder inputs:", [(i.name, i.shape) for i in sess_enc.get_inputs()])
print("decoder inputs:", [(i.name, i.shape) for i in sess_dec.get_inputs()])
print("decoder outputs:", [(o.name, o.shape) for o in sess_dec.get_outputs()])
logits_name = next(o.name for o in sess_dec.get_outputs() if o.name == "logits")

decoder_start = 65000
eos = 0
max_new = 80

def encode_source(text):
    sp_ids = src_sp.encode(text, out_type=int)
    ids = []
    for i in sp_ids:
        piece = src_sp.id_to_piece(i)
        ids.append(vocab.get(piece, vocab["<unk>"]))
    ids.append(eos)
    return ids

def decode_ids(ids):
    parts = []
    for i in ids:
        piece = id2piece.get(i, "<unk>")
        if piece.startswith("▁"):
            if parts:
                parts.append(" ")
            parts.append(piece[1:])
        elif piece not in ("</s>", "<pad>", "<unk>"):
            parts.append(piece)
    return "".join(parts).strip()

def translate(text):
    t0 = time.perf_counter()
    src_ids = encode_source(text)
    enc = sess_enc.run(None, {
        "input_ids": np.array([src_ids], dtype=np.int64),
        "attention_mask": np.ones((1, len(src_ids)), dtype=np.int64),
    })[0]
    dec_ids = [decoder_start]
    for _ in range(max_new):
        outs = sess_dec.run(None, {
            "input_ids": np.array([dec_ids], dtype=np.int64),
            "encoder_attention_mask": np.ones((1, len(src_ids)), dtype=np.int64),
            "encoder_hidden_states": enc,
        })
        logits = outs[[o.name for o in sess_dec.get_outputs()].index(logits_name)]
        nxt = int(np.argmax(logits[0, -1]))
        if nxt == eos or nxt == decoder_start:
            break
        dec_ids.append(nxt)
    elapsed = (time.perf_counter() - t0) * 1000
    return decode_ids(dec_ids[1:]), len(dec_ids) - 1, elapsed

tests = [
    "大家好，欢迎参加今天的会议。",
    "今天讨论三个议题。",
    "目前进展顺利，但预算需要重新评估。",
    "我们下周继续讨论这个问题。",
    "请注意听，这是一个测试。",
    "客户的反馈很好，产品下个月上线。",
]
for s in tests:
    out, n, ms = translate(s)
    print(f"[{ms:7.0f} ms, {n:2d} tok] {s}\n    => {out}")

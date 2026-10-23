# 离线翻译模型

- 基础模型：Helsinki-NLP/opus-mt-zh-en（CC-BY 4.0）。
- ONNX 量化权重：Xenova/opus-mt-zh-en。
- 文件：`encoder_model_quantized.onnx`、`decoder_model_quantized.onnx`、`source.spm`、`vocab.json`。

使用 ONNX Runtime CPU 贪心解码，运行时不需要联网。

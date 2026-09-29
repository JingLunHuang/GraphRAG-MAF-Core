# Local model contracts

`scripts/download-embedding.ps1` downloads a pinned Apache-2.0 `all-MiniLM-L6-v2` INT8 ONNX model and verifies both SHA-256 hashes. Weights are excluded from Git.

The embedding adapter supports uncased BERT WordPiece, `input_ids`, `attention_mask`, optional `token_type_ids`, and a rank-three `last_hidden_state` tensor. It performs attention-mask mean pooling and L2 normalization. Defaults: 256 tokens, 384 dimensions. This test model primarily covers English; validate language suitability before changing a corpus.

For `AI_PROVIDER=onnx`, put a compatible **ONNX Runtime GenAI** model in `models/chat/`, including `genai_config.json`, tokenizer files and every ONNX/external-data file. Set `ONNX_CHAT_TEMPLATE=llama3|phi3|tokenizer` to match the model. A generic ONNX file is insufficient for autoregressive GenAI inference. Use the [official model builder](https://onnxruntime.ai/docs/genai/howto/build-model.html) and the model's own license.

GGUF models run with `AI_PROVIDER=ollama`, including Llama 3.x. ONNX Runtime does not load GGUF. [ORT GenAI C# API](https://onnxruntime.ai/docs/genai/api/csharp.html), [Ollama model library](https://ollama.com/library).

Quantized embeddings have been run locally. The ORT GenAI chat adapter requires a compatible chat checkpoint; that specific backend is a separate validation item. The live chat pilot uses local Ollama GGUF inference.

# 執行與模型設定

設定使用 process environment；Compose 讀取 `.env`。`scripts/setup.ps1` 產生並保留已有的 `.env`，不覆寫使用者設定。服務不會輸出 `APP_API_KEY`、Neo4j 密碼或供應商憑證。預設 prompt 內容不匯出；需自行評估資料政策後才設定 `TELEMETRY_CAPTURE_CONTENT=true`。

| 變數 | 預設 | 說明 |
|---|---|---|
| `AI_PROVIDER` | `fixture`（Compose: `ollama`） | `fixture`, `ollama`, `azure`, `onnx`, `openai-compatible` |
| `EMBEDDING_PROVIDER` | 同 `AI_PROVIDER` | 可獨立切換；ONNX INT8 需 model+vocab |
| `GRAPH_STORE` | fixture: `memory`; 其他: `neo4j` | 非 fixture 聊天不得使用記憶體圖 |
| `NEO4J_HTTP` | `http://localhost:7474/` | Neo4j transactional HTTP endpoint |
| `NEO4J_DATABASE` / `NEO4J_USER` / `NEO4J_PASSWORD` | `neo4j` / `neo4j` / 必填 | 正式圖的認證 |
| `OLLAMA_ENDPOINT` | `http://localhost:11434/` | Ollama REST |
| `CHAT_MODEL` / `EMBEDDING_MODEL` | `llama3.2:3b` / `nomic-embed-text` | 模型或 Azure deployment 名稱 |
| `ONNX_CHAT_DIRECTORY` | `models/chat` | GenAI 模型，含 `genai_config.json` |
| `ONNX_CHAT_TEMPLATE` | `llama3` | `llama3`, `phi3`, `tokenizer`，配合 checkpoint |
| `ONNX_EMBEDDING_PATH` / `ONNX_VOCAB_PATH` | `models/embedding/model.onnx` / `vocab.txt` | 量化模型與 WordPiece vocabulary |
| `ONNX_EMBEDDING_OUTPUT` | `last_hidden_state` | 模型輸出 tensor 名稱 |
| `ONNX_MAX_EMBEDDING_TOKENS` | `256` | Token 上限；截斷需納入模型驗收 |
| `EMBEDDING_SPACE` | 供應商與模型名稱 | 同一資料庫不可混用不同向量空間；同名權重變動時應自行升版此 ID |
| `AZURE_OPENAI_ENDPOINT` / `AZURE_OPENAI_API_KEY` | 空 | Azure provider 必需；`CHAT_MODEL` 和 `EMBEDDING_MODEL` 是 deployment IDs |
| `AZURE_OPENAI_API_VERSION` | `2024-10-21` | API 版本，部署前確認資源支援 |
| `AI_ENDPOINT` / `AI_API_KEY` | localhost/空 | OpenAI-compatible server URL 與可選 key |
| `JUDGE_PROVIDER` / `JUDGE_CHAT_MODEL` | 與生成模型相同 | 建議品質評估改用獨立模型並保留偏差說明 |
| `MCP_ENDPOINT` / `MCP_SEARCH_TOOL` | 空 | CRAG 工具服務與可選明確工具名稱 |
| `RETRIEVAL_THRESHOLD` | `0.45` | 低分觸發；是 cosine heuristic，不是機率 |
| `MAX_CORRECTIONS` | `2` | 不通過 citation/critic 時的最大生成回合 |
| `CHUNK_SIZE` / `CHUNK_OVERLAP` | `1200` / `150` | 字元數；保留 Unicode surrogate 邊界 |
| `INGESTION_CAPACITY` / `STREAM_CAPACITY` | `16` / `8` | 有界 channel 與背壓 |
| `MAX_EVIDENCE_CHARACTERS` | `24000` | 每次 answer/reduce context 預算 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | 空 | 例如 Compose 內 `http://otel-collector:4317` |
| `TELEMETRY_CAPTURE_CONTENT` | `false` | Prompt/response 採集開關 |

非 Docker 的實測組合：Windows 上的量化 Ollama `qwen2.5-coder:7b`、INT8 ONNX `all-MiniLM-L6-v2`，透過本地 Neo4j GDS。它只是實測配置，並非預設推薦企業模型；評估仍需獨立樣本與正式資訊治理。

更換 embedding 模型或 tokenizer 時應建立新 Neo4j database 或重新索引。`RagMetadata` 存放 embedding-space 識別，啟動時會拒絕錯誤配對。外部文件應先確認來源、授權、語言與欄位，不可默默混入示範資料集。Google/Azure/企業系統的 MCP endpoint 是可選整合；本專案不收集其它專案的密鑰。

Native AOT 在 Linux 上需要 C/C++ linker（CI 安裝 clang 與 zlib），Windows native publish 需 Visual Studio Desktop C++ workload。編譯後應以實際 Linux 檔案系統測量啟動；WSL 掛載 Windows 磁碟可能主導 I/O 延遲。[量測 JSON](../artifacts/startup-benchmark-linux-fs.json)。

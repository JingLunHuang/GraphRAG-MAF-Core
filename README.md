# GraphRAG-MAF-Core

以 C#/.NET 10 實作供應商可切換的 GraphRAG：Semantic Kernel 文件提取、Neo4j GDS Leiden、多智能體審查、MCP 回退、Native AOT API，以及可以重新執行的評估與效能量測。

提供 REST、SSE、CLI、stdio MCP 和 Streamable HTTP MCP。核心與供應商、資料庫、傳輸層分離；`IChatClient` 與 `IEmbeddingGenerator<string, Embedding<float>>` 可透過 DI 切換 Azure OpenAI、Ollama、ONNX 或 OpenAI-compatible 服務。

## 執行

需要 .NET SDK **10.0.401**、Node.js 22+。目前工作目錄已有局部 SDK 時，驗證腳本會自動使用它。

```powershell
.\scripts\verify.ps1
```

這會執行 Release 測試、三種 RAG 模式、SIMD benchmark 與 MCP 協定驗證。離線 fixture 使用確定性模型與記憶體圖，不代表 LLM 品質或 Leiden；輸出會明確標記。

### 一鍵部署

安裝並啟動 Docker Desktop/Linux Docker Engine，然後執行：

```powershell
.\scripts\setup.ps1 -Start
```

腳本產生不納入 Git 的 `.env` 與隨機密鑰。Compose 包含 Neo4j **5.26.31 + GDS 2.13.12**、Ollama、模型初始化、Jaeger **2.21.0**、OTel Collector、Prometheus 與編譯成 Native AOT 的 API。初次部署需要下載容器和模型。預設模型是 `llama3.2:3b` 與 `nomic-embed-text`。

若本機已有 Ollama 模型，可用唯讀掛載避免重新下載；以下範例搭配專案下載的 ONNX INT8 embedding 模型：

```powershell
$env:OLLAMA_HOST_MODELS = ($env:USERPROFILE -replace '\\', '/') + '/.ollama/models'
$env:AI_PROVIDER = 'ollama'
$env:CHAT_MODEL = 'qwen2.5-coder:7b' # 改成 ollama list 中實際存在的模型
$env:EMBEDDING_PROVIDER = 'onnx'
docker compose -f docker-compose.yml -f deploy/compose.cached-models.yml --env-file .env up --build -d --wait
$env:APP_API_KEY = ((Get-Content .env | Where-Object { $_ -match '^APP_API_KEY=' }) -split '=', 2)[1]
node scripts/smoke-compose.mjs
```

`scripts/smoke-compose.mjs` 會在真實容器中匯入合成文件、執行 Leiden 社群、產生並審查全域回答，並檢查 HTTP MCP 工具發現；CPU 模型第一次載入與產生摘要可能花數分鐘。驗證結果寫入 `artifacts/docker-live-smoke.json`，過程中寫入 `.partial.json`；模型權重與密鑰不會納入 Git。

| 入口 | 位址 |
|---|---|
| API / OpenAPI | http://localhost:8080/openapi.json |
| Readiness | http://localhost:8080/health/ready |
| MCP | http://localhost:8080/mcp |
| Neo4j Browser | http://localhost:7474 |
| Jaeger | http://localhost:16686 |
| Prometheus | http://localhost:9090 |

API 使用 `.env` 中的 `APP_API_KEY`，透過 `X-API-Key` 傳入；health endpoints 不需要密鑰。範例請見 [API 與 MCP 操作](docs/usage.md)。

### 切換模型

透過 process environment 或 `.env` 設定，不需要修改業務程式：

| 模式 | `AI_PROVIDER` | `EMBEDDING_PROVIDER` | 必要設定 |
|---|---|---|---|
| Azure OpenAI | `azure` | `azure` | endpoint、API key、聊天與 embedding deployment 名稱 |
| Ollama / GGUF | `ollama` | `ollama` | endpoint、模型名稱 |
| ONNX GenAI + ONNX embeddings | `onnx` | `onnx` | GenAI 模型目錄、ONNX embedding 與 BERT vocabulary |
| Ollama + ONNX embeddings | `ollama` | `onnx` | 可分別設定兩種供應商 |
| 協定服務 | `openai-compatible` | `openai-compatible` | `AI_ENDPOINT`、可選 key、模型名稱 |

ONNX INT8 embedding 範例可以用 `.\scripts\download-embedding.ps1` 取得，下載後核對固定 revision 與 SHA-256。更換 embedding 模型必須使用新資料庫或重新索引；系統拒絕混用 embedding space。[模型契約](models/README.md)、[設定](docs/configuration.md)。

## 架構

```mermaid
flowchart LR
    REST[REST / SSE] --> CORE[GraphRag.Core]
    CLI[CLI / MCP server] --> CORE
    DOC[Documents] --> QUEUE[Bounded Channel / Wait]
    QUEUE --> SK[Semantic Kernel extraction]
    SK --> NEO[Neo4j entities / edges]
    NEO --> GDS[Leiden hierarchy / reports]
    CORE --> VEC[TensorPrimitives local vectors]
    CORE --> GDS
    CORE --> MAF[MAF Researcher → Generator → Critic]
    MAF --> FIX[Bounded correction / abstention]
    CORE --> MCP[MCP external search / CRAG]
    AI[IChatClient / IEmbeddingGenerator] --> SK
    AI --> MAF
    CORE --> OTEL[OpenInference / ActivitySource / Meter]
    OTEL --> OBS[Jaeger / Prometheus]
```

`local` 取向量候選並擴展 0–3 跳圖鄰居。`global` 處理最粗層的全部社群報告，以 map/reduce 組織答案，並提供原始 chunks 給 critic 查核。`naive` 使用向量檢索及單一生成智能體，作為相同資料集的基準。

低 cosine heuristic 觸發 MCP 工具發現與搜尋，補充來源必須保留 citation。這個分數不是校準過的機率。MCP 逾時或失敗時保留本地證據；無支持的回答會在固定重試上限後拒答。串流中的草稿增量尚未通過 critic，使用者應以最後的 `result` 為準。

## 實測數據

2026-09-29，Release、.NET 10.0.12。所有數值都有可查核的 JSON，不能推廣成任意硬體與工作負載的保證。

| 向量維度 | Scalar ns/op | TensorPrimitives ns/op | 加速 | Kernel managed allocations |
|---:|---:|---:|---:|---:|
| 128 | 54.61 | 10.38 | 5.26× | 0 bytes |
| 384 | 165.80 | 29.42 | 5.64× | 0 bytes |
| 768 | 329.70 | 65.67 | 5.02× | 0 bytes |
| 1536 | 662.81 | 138.66 | 4.78× | 0 bytes |

[原始 SIMD 量測](artifacts/vector-benchmark.json)：Windows 10.0.26200、28 logical processors，200,000 operations × 5 alternating rounds，採中位數；warm-up 10,000 次，數值誤差 < 6e-8。零配置適用於 cosine kernel；top-k 結果與整個 query 仍會配置記憶體。

| fresh process → HTTP ready，5 次中位數 | Native AOT | JIT |
|---|---:|---:|
| WSL2 Linux filesystem | 30.51 ms | 221.03 ms |
| readiness 時 RSS | 51.56 MiB | 76.71 MiB |
| Windows 掛載磁碟 | 5,510 ms | 7,566 ms |

[Linux filesystem 量測](artifacts/startup-benchmark-linux-fs.json)、[Windows mount 量測](artifacts/startup-benchmark.json)。兩者都是 warm filesystem cache、fixture providers、沒有載入模型的 fresh-process 啟動；不含容器建置、Neo4j 啟動或 LLM 載入。

已執行真正的 ONNX INT8 推論，384 維，相關句 cosine **0.8990**、不同主題 **0.0076**。[模型推論證據](artifacts/onnx-inference-smoke.json)。真正的 Neo4j GDS Leiden 在 4 份 fixture 提取文件上產生 **2 層、5 個社群**。[GDS 查核](artifacts/neo4j-gds-validation.json)。

## 量化評估

`evaluate` 會在相同文件及問題上配對執行 Naive/GraphRAG，用獨立可設定的 LLM judge 輸出原子事實判斷，再計算 Faithfulness、Answer Relevancy、Context Precision、Context Recall。報告保存原始答案、上下文、judge 判斷、資料集雜湊、模型名稱與公式版本。

```powershell
dotnet run --project src/GraphRag.Cli -c Release -- evaluate `
  --corpus data/demo-corpus.json --dataset data/evaluation.json `
  --output artifacts/evaluation.json
```

fixture judge 或 fixture embeddings 不能產生品質分數。目前示範集是 4 份合成文件、8 題，不能據此證明一般性的「GraphRAG 100% vs Naive RAG 0%」，也不能保證比 Naive RAG 準確。[評估方法與限制](docs/evaluation.md)。

## 五階段交付與技術對照

[30 天規劃對照](docs/phase-delivery.md) 列出每個階段的程式、驗證方式和實際驗證邊界。[驗證總表](artifacts/verification.json) 連結單項量測與目前尚未通過的項目。

目前 SDK 的 MAF 使用 `ChatClientAgent` 搭配 `AgentWorkflowBuilder`；`ChatCompletionAgent` 屬於 Semantic Kernel。GGUF 由 Ollama 執行，ORT GenAI 載入 ONNX GenAI 模型目錄。Self-RAG 反思是 prompted critic，未訓練原論文的 reflection-token checkpoint。C# 評估重現四項 RAGAS 公式與判斷流程，未宣稱等同官方 Python Ragas 的每個版本。[Microsoft MAF](https://learn.microsoft.com/en-us/agent-framework/workflows/workflows)、[ORT C#](https://onnxruntime.ai/docs/genai/api/csharp.html)、[Ragas](https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/)。

## GitHub

`ci.yml` 執行測試、CLI、MCP、SIMD、Linux Native AOT 編譯及 HTTP/SSE smoke，保留驗證 artifacts；[目前提交的 CI 已通過](https://github.com/JingLunHuang/GraphRAG-MAF-Core/actions/runs/37102831794)。`release.yml` 在 `v*` tag 或手動執行時發布 Native AOT container 至 GHCR，該發布流程尚未實測。

Docker 實測的 local 問題已透過 Ollama、ONNX embedding、Neo4j 與多智能體 critic，產生 1 筆來源引用及 `[Fully supported]` 判斷；Jaeger 可依 trace ID 查得追蹤。[實際結果](artifacts/docker-local-query.json)。同一合成語料的 global smoke 在目前容器模型上遭 critic 拒絕，不能宣稱整套問題品質都已通過；詳見[驗證總表](artifacts/verification.json)。

原始碼 MIT 授權；模型、GDS 與容器依各自授權。大型模型、SDK、密鑰、執行檔、暫存資料與資料庫不納入 Git。

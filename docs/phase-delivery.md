# 第 1–30 天交付對照

依照原定五階段建立完整工程；日期區間是規劃的工作分組，不代表已經過 30 個日曆日。以下把「已實作」、「已驗證」和「尚需指定模型／外部環境」分開記錄。

| 階段 | 已實作的程式 | 已取得驗證 | 驗證邊界 |
|---|---|---|---|
| 1：第 1–5 天 | `IChatClient`、`IEmbeddingGenerator` DI；Azure/Ollama/ONNX adapters；Native AOT API | 真正 Linux AOT 編譯与 HTTP；真正 INT8 ONNX embeddings；REST routing/usage/JSON 契約 | Azure 需有效 endpoint/部署/密鑰；ONNX GenAI chat 需完整相容 checkpoint |
| 2：第 6–12 天 | SK entities/relations；Neo4j HTTP transactions；GDS Leiden gamma/theta/seed；多層摘要；TensorPrimitives | 真正 GDS 2.13.12 產生 2 層 5 社群；Release SIMD 4.78–5.64×，kernel 0 managed bytes | fixture 圖只用於離線測試，明確稱為 connected-components，不冒稱 Leiden |
| 3：第 13–20 天 | 真正 MAF workflow；Researcher/Generator/Critic；反思標記；固定重試與拒答；MCP 發現/search | fixture 模型驅動真實 MAF；citation/retry/cancel；官方 SDK HTTP external-search 整合測試 | 反思是提示式判斷，沒有訓練 Self-RAG checkpoint；live MCP search 需外部 MCP endpoint |
| 4：第 21–28 天 | bounded Channel.Wait 攝取/串流；OpenInference.NET；Activity/Meter；OTLP；四項 C# 評估 | 30 項測試覆蓋背壓、取消、token usage、不記錄 prompt；Native HTTP/SSE/16 並發；真實 LLM pilot 可重跑 | 16 並發是工程 smoke，不是大量實際 LLM 請求的吞吐基準；合成小樣本不能證明普遍優越 |
| 5：第 29–30 天 | Dockerfiles；完整 Compose；README measured tables；GitHub CI/GHCR release workflow | Docker 六個長駐服務已實際啟動；AOT API + Ollama + ONNX + Neo4j 的 local 回答有引用並通過 critic；Jaeger 查得 5 個 spans；GitHub CI 通過 | Docker global smoke 的答案未通過 critic；GHCR 發布流程尚未驗證 |

## API 與論文對應

1. MAF 1.x 的實際 NuGet API 是 `ChatClientAgent` 和 `AgentWorkflowBuilder.CreateSequentialBuilderWith`。採用正式套件 `Microsoft.Agents.AI.Workflows 1.23.0`。[官方 workflow API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.agentworkflowbuilder?view=agent-framework-dotnet-latest)。
2. ONNX Runtime 不讀取 GGUF；GGUF 經 Ollama，ONNX 經 ORT/ORT GenAI。後者需要 `genai_config.json`、tokenizer 與完整 external-data。這保留本地資料落地與 DI 模型切換的目標。[ORT GenAI](https://onnxruntime.ai/docs/genai/api/csharp.html)。
3. GraphRAG 依照 entity graph、Leiden hierarchy、community reports、global map/reduce 實作；不是 Microsoft Python GraphRAG 所有 indexing/query 特性的逐行移植。[GraphRAG global search](https://microsoft.github.io/graphrag/query/global_search/)。
4. Neo4j 5.26 LTS 的正式相容線是 GDS 2.13。設定採 deterministic seed、單執行緒與 intermediate communities；不同 graph/gamma/theta 可以產生不同層數。[版本相容](https://neo4j.com/docs/graph-data-science/current/installation/supported-neo4j-versions/)、[Leiden](https://neo4j.com/docs/graph-data-science/current/algorithms/leiden/)。
5. Self-RAG 使用反思標記 `[Retrieval]`、`[Relevant]`、`[Fully supported]` 等，由 prompted critic 預測並以引用檢查補強；此工程沒有論文 checkpoint 的訓練程序。[Self-RAG paper](https://arxiv.org/abs/2310.11511)。
6. CRAG 觸發依檢索 cosine heuristic，低分呼叫 MCP search；這不是 CRAG 論文經訓練的 retrieval evaluator。啟用外部來源須有可供工具發現的 endpoint。[CRAG paper](https://arxiv.org/abs/2401.15884)。
7. `OpenInference.NET` 是 community .NET package；整合原生 OpenTelemetry 與 OpenInference 語意，不宣稱為 Arize 官方 .NET SDK。[套件](https://www.nuget.org/packages/OpenInference.NET)、[語意標準](https://github.com/Arize-ai/openinference)。提示內容預設不採集；開啟 capture 時由套件敏感資訊清理機制處理。
8. RAGAS 四指標以 C# judge 原子判斷與公式重現；另列 judge answer accuracy，避免把任何一項指標稱為「準確率」。[RAGAS paper](https://arxiv.org/abs/2309.15217)。

## 尚待外部條件的驗收

- Azure OpenAI：提供已部署的 chat/embedding 名稱與 endpoint，在使用者配置後重跑，不會從其他專案取得密鑰。
- ORT GenAI chat：提供與模板相符、授權允許使用的完整量化模型目录；目前已實測的是 ONNX embeddings 與 Ollama GGUF chat。
- 品質優勢：提供較大、包含 multi-hop/global 問題的獨立標註測試集；保持相同資料、初始檢索 k 與明列 context budget，報告成本、延遲與不確定性。
- Docker 全域回答：目前合成語料的 local 題已通過，但 global 題在容器模型上被 critic 拒絕；需要檢查中間摘要與草稿，而非把服務健康狀態當作答案品質通過。[目前驗證紀錄](../artifacts/verification.json)。
- GitHub GHCR：原始碼已推送且 [CI 通過](https://github.com/JingLunHuang/GraphRAG-MAF-Core/actions/runs/37102831794)；`release.yml` 的映像發布仍需獨立驗證。

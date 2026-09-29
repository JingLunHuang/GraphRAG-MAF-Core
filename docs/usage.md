# API、SSE 與 MCP 操作

以下假定已執行 `scripts/setup.ps1 -Start`，API 在 `http://localhost:8080`。`.env` 僅留在本機；不要把密鑰貼到 GitHub issue 或 commit。

```powershell
$apiKey = ((Get-Content .env | Where-Object { $_ -match '^APP_API_KEY=' }) -split '=', 2)[1]
$headers = @{ 'X-API-Key' = $apiKey }
Invoke-RestMethod http://localhost:8080/health/ready
```

健康檢查在初始化成功後回 `status=ready`。文件攝取走容量為 16 的 `Channel`，佇列滿時寫入等待，而不是無限增長。文件 ID 由 source + text 決定，重複匯入保持冪等。

```powershell
$document = @{ source = 'demo://northwind'; text = 'Northwind sells Orion. Orion depends on Contoso for compliance reviews.' } | ConvertTo-Json
Invoke-RestMethod http://localhost:8080/api/v1/documents -Method Post -Headers $headers -ContentType 'application/json' -Body $document
$leiden = @{ gamma = 1.0; theta = 0.01; maxLevels = 10; randomSeed = 19 } | ConvertTo-Json
Invoke-RestMethod http://localhost:8080/api/v1/communities -Method Post -Headers $headers -ContentType 'application/json' -Body $leiden
$question = @{ question = 'How does Contoso affect Orion?'; mode = 'local'; topK = 5; hops = 2 } | ConvertTo-Json
Invoke-RestMethod http://localhost:8080/api/v1/query -Method Post -Headers $headers -ContentType 'application/json' -Body $question
```

`mode` 可選 `local`, `global`, `naive`。`global` 須在資料匯入後建立目前版本的社群，增量匯入會使摘要失效。輸出含 `citations`, `evidence`, `reflection`, `retrievalConfidence`, `usedFallback`, `warnings`；`retrievalConfidence` 是 cosine heuristic，不能當作校準機率。實際 API schema 在 [`openapi.json`](openapi.json)。

`POST /api/v1/query/stream` 接收相同 JSON，回傳 SSE `retrieval`, `agent_delta`, `agent_complete`, `correction`, `result` 等事件。`agent_delta` 是尚未通過最終 critic 的草稿；必須根據最後 `result` 或 `error` 判斷完成狀態。客戶端中止時，服務取消 producer；有界串流容量預設 8，使用 `BoundedChannelFullMode.Wait`。

HTTP MCP 位於 `POST /mcp`，使用 Streamable HTTP；stdio MCP 用 CLI：

```powershell
dotnet run --project src/GraphRag.Cli -c Release -- mcp
```

正式外部 MCP 回退設定 `MCP_ENDPOINT`，可選 `MCP_SEARCH_TOOL`。SDK 會列出工具，選擇名稱含 `search`、且 JSON schema 有 `query` 或 `q` 字串欄位的工具；呼叫結果可為 `{"results":[{"id":"...","source":"...","text":"...","score":0.8}]}` 或 text block。引用記錄會標成 `external`。未設定 MCP 時，低分檢索僅回傳警示與本地證據；MCP 逾時或服務失效不會直接廢棄本地可支持的答案。

檢查協定端到端：

```powershell
$env:DOTNET_EXE = '.runtime/dotnet/dotnet.exe'  # 若 SDK 已在 PATH，可改成 dotnet
$env:CLI_DLL = 'src/GraphRag.Cli/bin/Release/net10.0/GraphRag.Cli.dll'
node scripts/smoke-mcp-stdio.mjs
```

CLI `help` 可顯示 `smoke`, `benchmark`, `onnx-check`, `ingest`, `communities`, `query`, `evaluate`, `mcp`。fixture 可用 `--fixture`；`smoke` 自動採 fixture 並清楚標註不是模型品質驗證。

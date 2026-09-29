# 評估流程與判讀

資料格式：`id`, `question`, `reference`, `graphMode`（`local` 或 `global`）。`data/evaluation.json` 提供 8 題，涵蓋直接事實、多跳風險、全域策略、預算以及未提供資料時的拒答。

同一題先跑 Naive，再跑指定 GraphRAG 模式。兩者使用同一 corpus、embedding/generator、初始 top-k=5；GraphRAG 有圖擴展或全域摘要及 critic。context budget 預設 24,000 characters，來源與實際上下文逐題保存。Naive 不啟用 critic 或 CRAG，因此比較的是完整方法，而不是單一圖演算法效果。

| 指標 | C# 計算 |
|---|---|
| Faithfulness | 生成答案的原子事實中，judge 判為可由 contexts 支持的比例 |
| Answer Relevancy | 原問題與 3 個從生成答案重建之問題的 embedding cosine 平均；noncommittal 時為 0 |
| Context Precision | 原始順序相關 contexts 的 average precision：Σ(precision@rank × relevant) / relevant count |
| Context Recall | reference 原子事實中，可由 contexts 支持的比例 |
| Judge Answer Accuracy | 完整回答是否符合 reference 的布林比例，獨立於以上四指標 |

judge 必須輸出符合 schema 的逐項陣列，不接受自填任意 aggregate 分數。`JUDGE_PROVIDER` 與 `JUDGE_CHAT_MODEL` 可切換成獨立模型。報告保存模型名稱、dataset SHA-256、UTC 時間、`ragas-formulas-csharp-v1` 版本、原始判斷和結果。

小型合成案例上的 LLM-as-a-judge 分数不代表人工標註準確率、臨床效果或外部資料表現。同模型擔任 generator/judge 可能產生偏差。Reference segmentation 及 prompt 不等同所有 Python Ragas 版本；本工程明列公式和版本，避免混稱官方工具的 certified results。

`fixture` judge / embedding 會直接拒絕評估，工程 smoke 的成功不會轉成虛構的 RAGAS 分數。空 claim 集合計 0；模型拒答可能降低 relevancy/answer accuracy，應另看 reference 是否要求拒答及 judge 的 explanation。

增加嚴格評估時，採用獨立的 domain corpus 和保留題，至少分列 direct/multi-hop/global/unanswerable，記錄 judge 校準與人工覆核。相同資料、context/token 預算和初始 k 之外，報告 GraphRAG 多次模型呼叫的成本與延遲；以 paired results 和 bootstrap confidence intervals 判斷是否提升，而非預設 100% vs 0%。

[Ragas Answer Relevancy](https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/answer_relevance/)、[Context Precision](https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/context_precision/)、[RAGAS 論文](https://arxiv.org/abs/2309.15217)。

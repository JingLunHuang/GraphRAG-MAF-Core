using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace GraphRag.Core;

public sealed class GraphRagService(IGraphStore store, IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IGraphExtractor extractor, IAgentTeam team, IExternalKnowledgeSource external, VectorIndex index, RagOptions options)
{
    public static readonly ActivitySource Activities = new("GraphRag.Core");
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private CommunitySummary[] _communities = [];
    private int _ready;
    public bool Ready => Volatile.Read(ref _ready) == 1;
    public int ChunkCount => index.Count;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        await store.InitializeAsync(cancellationToken);
        GraphSnapshot snapshot = await store.LoadAsync(cancellationToken);
        if (snapshot.EmbeddingSpace is not null && snapshot.EmbeddingSpace != options.EmbeddingSpace)
            throw new InvalidOperationException("Stored embedding space differs from the configured provider/model; use a separate database or reindex.");
        index.Replace(snapshot.Chunks);
        Volatile.Write(ref _communities, snapshot.Communities);
        Volatile.Write(ref _ready, 1);
    }

    public async Task<IngestResult> IngestAsync(DocumentInput input, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        if (input.Text.Length > 1_000_000 || input.Source.Length > 2048) throw new ArgumentException("Document exceeds ingestion limits.");
        using Activity? activity = Activities.StartActivity("ingest");
        activity?.SetTag("openinference.span.kind", "CHAIN");
        string documentId = "doc-" + TextProcessing.Id(input.Source + "\n" + input.Text);
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            TextChunk[] existing = index.Snapshot.Where(c => c.DocumentId == documentId).ToArray();
            if (existing.Length > 0) return new IngestResult(documentId, existing.Length, 0, 0);
            List<TextChunk> chunks = [];
            Dictionary<string, GraphEntity> entities = new(StringComparer.Ordinal);
            List<GraphRelation> relations = [];
            foreach (string text in TextProcessing.Chunk(input.Text, options.ChunkSize, options.ChunkOverlap))
            {
                string chunkId = documentId + "-" + chunks.Count;
                GeneratedEmbeddings<Embedding<float>> generated = await embeddings.GenerateAsync([text], cancellationToken: cancellationToken);
                if (generated.Count != 1) throw new InvalidOperationException("Embedding provider returned an unexpected batch length.");
                float[] vector = generated[0].Vector.ToArray();
                VectorIndex.Validate(vector);
                chunks.Add(new TextChunk(chunkId, documentId, input.Source, text, vector));
                Extraction extracted = await extractor.ExtractAsync(text, cancellationToken);
                foreach (ExtractedEntity entity in extracted.Entities)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(entity.Name);
                    string id = TextProcessing.EntityId(entity.Name);
                    string[] mentions = entities.TryGetValue(id, out GraphEntity? old) ? [.. old.ChunkIds, chunkId] : [chunkId];
                    entities[id] = new GraphEntity(id, entity.Name.Trim(), entity.Type, entity.Description, mentions.Distinct().ToArray());
                }
                foreach (ExtractedRelation relation in extracted.Relations)
                {
                    string from = TextProcessing.EntityId(relation.Source), to = TextProcessing.EntityId(relation.Target);
                    if (!entities.ContainsKey(from) || !entities.ContainsKey(to))
                        throw new InvalidOperationException("Extraction contains a relationship whose endpoint was not extracted.");
                    relations.Add(new GraphRelation(from, to, relation.Description, chunkId));
                }
            }
            if (index.Count > 0 && chunks.Any(c => c.Vector.Length != index.Snapshot[0].Vector.Length))
                throw new InvalidOperationException("Embedding dimensions changed during ingestion.");
            GraphDocument document = new(documentId, input.Source, options.EmbeddingSpace, chunks.ToArray(), entities.Values.ToArray(), relations.ToArray());
            await store.SaveDocumentAsync(document, cancellationToken);
            index.Add(document.Chunks);
            // Community summaries are invalidated when the corpus changes.
            Volatile.Write(ref _communities, []);
            return new IngestResult(documentId, chunks.Count, entities.Count, relations.Count);
        }
        finally { _mutation.Release(); }
    }

    public async Task<CommunityBuildResult> BuildCommunitiesAsync(LeidenOptions settings, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (!double.IsFinite(settings.Gamma) || settings.Gamma <= 0 || !double.IsFinite(settings.Theta) ||
            settings.Theta <= 0 || settings.MaxLevels is < 1 or > 30)
            throw new ArgumentException("Leiden gamma and theta must be finite positive numbers, and maxLevels must be 1..30.");
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            CommunityPartition[] partitions = await store.PartitionAsync(settings, cancellationToken);
            Dictionary<string, TextChunk> chunks = index.Snapshot.ToDictionary(c => c.Id);
            List<CommunitySummary> summaries = [];
            foreach (CommunityPartition partition in partitions)
            {
                Evidence[] evidence = partition.ChunkIds.Where(chunks.ContainsKey)
                    .Select(id => new Evidence(id, chunks[id].Source, chunks[id].Text, 1)).ToArray();
                if (evidence.Length == 0) continue;
                // Summarize bounded batches, then reduce; preserve all underlying chunk provenance.
                List<string> reports = [];
                List<Evidence> batch = [];
                int length = 0;
                foreach (Evidence item in evidence)
                {
                    if (length + item.Text.Length > options.MaxEvidenceCharacters && batch.Count > 0)
                    {
                        reports.Add(await extractor.SummarizeAsync(batch.ToArray(), cancellationToken));
                        batch.Clear(); length = 0;
                    }
                    batch.Add(item); length += item.Text.Length;
                }
                if (batch.Count > 0) reports.Add(await extractor.SummarizeAsync(batch.ToArray(), cancellationToken));
                string report = reports.Count == 1 ? reports[0] : await extractor.SummarizeAsync(
                    reports.Select((text, i) => new Evidence($"partial-{i}", "community-partial", text, 1, "community")).ToArray(), cancellationToken);
                summaries.Add(new CommunitySummary(partition.Id, partition.Level, report, partition.ChunkIds));
            }
            await store.SaveCommunitiesAsync(summaries.ToArray(), cancellationToken);
            Volatile.Write(ref _communities, summaries.ToArray());
            return new CommunityBuildResult(summaries.Count, summaries.Select(s => s.Level).Distinct().Count(), settings.Gamma, settings.Theta, store.Algorithm);
        }
        finally { _mutation.Release(); }
    }

    public Task<AnswerResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        QueryCoreAsync(request, null, cancellationToken);

    public async IAsyncEnumerable<StreamEvent> StreamAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<StreamEvent> channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(options.StreamCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        Task producer = ProduceAsync();
        try { await foreach (StreamEvent item in channel.Reader.ReadAllAsync(linked.Token)) yield return item; }
        finally
        {
            await linked.CancelAsync();
            try { await producer; } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
        async Task ProduceAsync()
        {
            try
            {
                AnswerResult result = await QueryCoreAsync(request, item => channel.Writer.WriteAsync(item, linked.Token), linked.Token);
                await channel.Writer.WriteAsync(new StreamEvent("result", "", result), linked.Token);
                channel.Writer.TryComplete();
            }
            catch (Exception error) { channel.Writer.TryComplete(error); }
        }
    }

    private async Task<AnswerResult> QueryCoreAsync(QueryRequest request, Func<StreamEvent, ValueTask>? emit, CancellationToken cancellationToken)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);
        if (request.Question.Length > 8000 || request.TopK is < 1 or > 50 || request.Hops is < 0 or > 3 ||
            request.Mode is not ("local" or "global" or "naive")) throw new ArgumentException("Invalid question, mode, topK, or hops.");
        using Activity? activity = Activities.StartActivity("query");
        activity?.SetTag("openinference.span.kind", "CHAIN");
        activity?.SetTag("rag.mode", request.Mode);
        List<string> warnings = [];
        if (options.Fixture) warnings.Add("Fixture AI or embedding provider: engineering verification only. Graph algorithm: " + store.Algorithm + ".");
        GeneratedEmbeddings<Embedding<float>> generated = await embeddings.GenerateAsync([request.Question], cancellationToken: cancellationToken);
        Evidence[] local = index.Search(generated[0].Vector.Span, request.TopK);
        float confidence = local.Length == 0 ? 0 : Math.Clamp(local[0].Score, 0, 1);
        List<Evidence> evidence = [.. local];
        HashSet<string>? globalSourceIds = null;
        if (request.Mode == "global")
        {
            CommunitySummary[] communities = Volatile.Read(ref _communities);
            if (communities.Length == 0) throw new InvalidOperationException("Global search requires freshly built community summaries.");
            int highestLevel = communities.Max(c => c.Level);
            globalSourceIds = communities.Where(c => c.Level == highestLevel).SelectMany(c => c.ChunkIds).ToHashSet(StringComparer.Ordinal);
            // Global map: all communities at the coarsest level, not vector top-k.
            evidence = communities.Where(c => c.Level == highestLevel).OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => new Evidence(c.Id, "community:" + string.Join(",", c.ChunkIds), c.Summary, 1, "community")).ToList();
        }
        else if (request.Mode == "local" && local.Length > 0 && request.Hops > 0)
        {
            TextChunk[] neighbors = await store.ExpandAsync(local.Select(e => e.Id).ToArray(), request.Hops, request.TopK * 3, cancellationToken);
            evidence.AddRange(neighbors.Select(c => new Evidence(c.Id, c.Source, c.Text, confidence, "graph")));
        }
        bool fallback = false;
        if (request.Mode != "naive" && confidence < options.ConfidenceThreshold)
        {
            if (external.Enabled)
            {
                if (emit is not null) await emit(new StreamEvent("fallback", "Local confidence below threshold; retrieving MCP knowledge."));
                try
                {
                    Evidence[] supplement = await external.SearchAsync(request.Question, cancellationToken);
                    fallback = supplement.Length > 0;
                    evidence.AddRange(supplement);
                    if (!fallback) warnings.Add("MCP returned no supplemental evidence.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { warnings.Add("MCP timed out; continuing with available local evidence."); }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
                { warnings.Add("MCP unavailable; continuing with available local evidence."); }
            }
            else warnings.Add("Local confidence below threshold; no MCP fallback is configured.");
        }
        evidence = evidence.DistinctBy(e => e.Id).ToList();
        if (emit is not null) await emit(new StreamEvent("retrieval", $"Retrieved {evidence.Count} evidence items; cosine heuristic = {confidence:F3}."));
        if (evidence.Count == 0)
            return Result(new Draft("Insufficient evidence to answer this question.", []), new("[Retrieval]", "[Irrelevant]", "[No support]", 1, "No evidence."), 0);
        if (request.Mode == "naive")
        {
            evidence = [.. Limit(evidence)];
            Draft naive = await team.GenerateNaiveAsync(request.Question, evidence.ToArray(), cancellationToken);
            ValidateCitations(naive, evidence);
            return Result(naive, new("[No Retrieval]", "[Unchecked]", "[Unchecked]", 0, "Naive RAG baseline has no critic."), 1);
        }
        Evidence[] bounded;
        if (request.Mode == "global")
        {
            // Map each bounded batch to supported notes, then reduce with the same team.
            List<Evidence> mapped = [];
            foreach (Evidence[] batch in Batches(evidence))
            {
                string summary = await extractor.SummarizeAsync(batch, cancellationToken);
                mapped.Add(new Evidence("map-" + TextProcessing.Id(string.Join('|', batch.Select(e => e.Id))),
                    string.Join(";", batch.Select(e => e.Source)), summary, 1, "global-map"));
            }
            Evidence[] grounding = index.Snapshot.Where(c => globalSourceIds!.Contains(c.Id)).OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => new Evidence(c.Id, c.Source, c.Text, 1, "source")).ToArray();
            // Derived reports guide global reasoning; original chunks let the critic verify them.
            bounded = Limit(mapped.Concat(grounding));
            if (bounded.Length < mapped.Count + grounding.Length) warnings.Add("Global reports/source grounding exceed the reduce context budget; some evidence was omitted.");
            evidence = [.. bounded];
        }
        else { bounded = Limit(evidence); evidence = [.. bounded]; }
        string? feedback = null;
        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                AgentTurn turn = await team.RunAsync(request.Question, bounded, feedback, emit, cancellationToken);
                ValidateCitations(turn.Draft, bounded);
                if (turn.Reflection.Accepted) return Result(turn.Draft, turn.Reflection, attempt);
                feedback = turn.Reflection.Reason;
            }
            catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException) { feedback = error.Message; }
            if (emit is not null) await emit(new StreamEvent("correction", $"Critic requested correction (attempt {attempt})."));
        }
        warnings.Add("Drafts did not pass support/citation checks within the retry budget.");
        return Result(new Draft("Insufficient supported evidence; the critic rejected the generated drafts.", []),
            new("[Retrieval]", "[Relevant]", "[No support]", 1, feedback ?? "Rejected."), options.MaxAttempts);

        AnswerResult Result(Draft draft, ReflectionDecision reflection, int attempts) => new(request.Question, request.Mode, draft.Answer,
            draft.Citations.Select(id => new Citation(id, evidence.First(e => e.Id == id).Source)).ToArray(), evidence.ToArray(), reflection,
            confidence, fallback, attempts, activity?.TraceId.ToString() ?? Activity.Current?.TraceId.ToString() ?? "", warnings.ToArray());
    }

    public static void ValidateCitations(Draft draft, IEnumerable<Evidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Answer);
        HashSet<string> allowed = evidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if ((draft.Citations.Length == 0 && !draft.Abstained) || draft.Citations.Any(id => !allowed.Contains(id)))
            throw new InvalidOperationException("Every substantive draft must cite known evidence IDs.");
    }

    private Evidence[] Limit(IEnumerable<Evidence> source) => Batches(source).FirstOrDefault() ?? [];
    private IEnumerable<Evidence[]> Batches(IEnumerable<Evidence> source)
    {
        List<Evidence> batch = []; int length = 0;
        foreach (Evidence item in source)
        {
            string text = item.Text.Length <= options.MaxEvidenceCharacters ? item.Text : item.Text[..options.MaxEvidenceCharacters];
            if (length + text.Length > options.MaxEvidenceCharacters && batch.Count > 0)
            { yield return batch.ToArray(); batch.Clear(); length = 0; }
            batch.Add(item with { Text = text }); length += text.Length;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
    private void EnsureReady() { if (!Ready) throw new InvalidOperationException("GraphRAG service has not finished initialization."); }
}

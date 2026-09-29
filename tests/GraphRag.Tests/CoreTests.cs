using GraphRag.Core;
using GraphRag.Infrastructure.Agents;
using GraphRag.Infrastructure.Evaluation;
using GraphRag.Infrastructure.Graph;
using GraphRag.Infrastructure.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace GraphRag.Tests;

public sealed class CoreTests
{
    [Fact]
    public async Task FailedMcpFallbackKeepsAvailableLocalEvidence()
    {
        GraphRagService service = CreateService(external: new FailingExternal(), threshold: 1); await service.InitializeAsync();
        await service.IngestAsync(new("test://a", "Northwind sells Orion."));
        AnswerResult result = await service.QueryAsync(new("What does Northwind sell?"));
        Assert.False(result.UsedFallback); Assert.NotEmpty(result.Citations);
        Assert.Contains(result.Warnings, warning => warning.Contains("MCP unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void CosineUsesKnownGeometry()
    {
        Assert.Equal(1, VectorIndex.Cosine([1, 0], [1, 0]), 5);
        Assert.Equal(0, VectorIndex.Cosine([1, 0], [0, 1]), 5);
        Assert.Equal(-1, VectorIndex.Cosine([1, 0], [-1, 0]), 5);
    }

    [Fact]
    public void CosineRejectsInvalidVectors()
    {
        Assert.Throws<ArgumentException>(() => VectorIndex.Cosine([], []));
        Assert.Throws<ArgumentException>(() => VectorIndex.Cosine([1], [1, 2]));
        Assert.Throws<ArgumentException>(() => VectorIndex.Cosine([0, 0], [1, 0]));
        Assert.Throws<ArgumentException>(() => VectorIndex.Cosine([float.NaN, 1], [1, 0]));
        Assert.Throws<ArgumentException>(() => VectorIndex.Validate([float.PositiveInfinity]));
    }

    [Fact]
    public void CosineKernelAllocatesZeroManagedBytes()
    {
        float[] a = Enumerable.Repeat(0.1f, 384).ToArray(), b = Enumerable.Repeat(0.2f, 384).ToArray();
        for (int i = 0; i < 1000; i++) _ = VectorIndex.Cosine(a, b);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = VectorIndex.Cosine(a, b);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void IndexPreservesRankingAndRejectsDimensionDrift()
    {
        VectorIndex index = new();
        index.Replace([new("a", "doc", "src", "aligned", [1, 0]), new("b", "doc", "src", "orthogonal", [0, 1])]);
        Assert.Equal("a", index.Search([1, 0], 1)[0].Id);
        Assert.Throws<InvalidOperationException>(() => index.Add([new("c", "doc", "src", "bad", [1, 0, 0])]));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void ChunkingRetainsOverlapAndUnicodeBoundaries()
    {
        Assert.Equal(["abcd", "defg", "ghij"], TextProcessing.Chunk("abcdefghij", 4, 1));
        string[] chunks = TextProcessing.Chunk("abc😀def😀ghi", 5, 1);
        Assert.All(chunks, chunk => { Assert.False(char.IsLowSurrogate(chunk[0])); Assert.False(char.IsHighSurrogate(chunk[^1])); });
        Assert.Equal(TextProcessing.EntityId(" Contoso "), TextProcessing.EntityId("CONTOSO"));
    }

    [Fact]
    public void RagasFormulaUsesRankWeightedPrecision()
    {
        Assert.Equal((1.0 + 2.0 / 3) / 2, RagasEvaluator.AveragePrecision([true, false, true]), 8);
        Assert.Equal(0, RagasEvaluator.AveragePrecision([false, false]));
        Assert.Equal(2.0 / 3, RagasEvaluator.Fraction([true, false, true]), 8);
    }

    [Fact]
    public async Task IngestionIsIdempotentAndBuildsCitableGraph()
    {
        GraphRagService service = CreateService(); await service.InitializeAsync();
        DocumentInput document = new("test://corpus", "Northwind sells Orion. Orion depends on Contoso for compliance.");
        IngestResult first = await service.IngestAsync(document), second = await service.IngestAsync(document);
        Assert.Equal(first.DocumentId, second.DocumentId); Assert.Equal(1, service.ChunkCount);
        Assert.True(first.Entities >= 3); Assert.True(first.Relations >= 2);
        AnswerResult answer = await service.QueryAsync(new("How does Northwind depend on Contoso?"));
        Assert.True(answer.Reflection.Accepted); Assert.NotEmpty(answer.Citations);
        Assert.All(answer.Citations, citation => Assert.Contains(answer.Evidence, e => e.Id == citation.Id));
    }

    [Fact]
    public async Task GlobalRequiresCurrentSummaries()
    {
        GraphRagService service = CreateService(); await service.InitializeAsync();
        await service.IngestAsync(new("test://a", "Northwind funds Orion and Helios."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(new("What is Northwind strategy?", "global")));
        CommunityBuildResult report = await service.BuildCommunitiesAsync(new());
        Assert.Contains("not Leiden", report.Algorithm);
        AnswerResult result = await service.QueryAsync(new("What is Northwind strategy?", "global"));
        Assert.True(result.Reflection.Accepted);
        await service.IngestAsync(new("test://b", "Contoso funds Fabrikam."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(new("Strategy?", "global")));
    }

    [Fact]
    public async Task CriticRetriesAndFailsClosed()
    {
        RejectingTeam team = new();
        GraphRagService service = CreateService(team: team); await service.InitializeAsync();
        await service.IngestAsync(new("test://a", "Northwind funds Orion."));
        AnswerResult result = await service.QueryAsync(new("Northwind?"));
        Assert.Equal(2, team.Calls); Assert.Equal(2, result.Attempts); Assert.Empty(result.Citations); Assert.False(result.Reflection.Accepted);
        Assert.Contains("critic rejected", result.Answer);
    }

    [Fact]
    public async Task CragCallsExternalOnlyBelowThreshold()
    {
        CountingExternal external = new();
        GraphRagService service = CreateService(external: external, threshold: 1); await service.InitializeAsync();
        AnswerResult result = await service.QueryAsync(new("Northwind Orion risk?"));
        Assert.Equal(1, external.Calls); Assert.True(result.UsedFallback); Assert.NotEmpty(result.Citations);
        await service.QueryAsync(new("Northwind Orion risk?", "naive")); Assert.Equal(1, external.Calls);
    }

    [Fact]
    public async Task StreamingDeliversDeltasAndFinalResult()
    {
        GraphRagService service = CreateService(); await service.InitializeAsync();
        await service.IngestAsync(new("test://a", "Northwind sells Orion."));
        List<StreamEvent> events = [];
        await foreach (StreamEvent item in service.StreamAsync(new("Northwind Orion?"))) events.Add(item);
        Assert.Contains(events, item => item.Kind == "agent_delta");
        Assert.Equal("result", events[^1].Kind); Assert.True(events[^1].Result!.Reflection.Accepted);
    }

    [Fact]
    public async Task AbandonedStreamCancelsProducer()
    {
        GraphRagService service = CreateService(); await service.InitializeAsync();
        await service.IngestAsync(new("test://a", "Northwind sells Orion."));
        await using var stream = service.StreamAsync(new("Northwind?" )).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FixtureQualityEvaluationIsRejected()
    {
        GraphRagService service = CreateService(); await service.InitializeAsync();
        RagasEvaluator evaluator = new(new FixtureChatClient(), new FixtureEmbeddingGenerator());
        await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(service, [new("q", "q", "reference")], "[]"));
    }

    [Fact]
    public void UnknownCitationIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => GraphRagService.ValidateCitations(new("Invented fact", ["unknown"]), [new("known", "src", "context", 1)]));
    }

    [Fact]
    public async Task EmbeddingSpaceCannotSilentlyChange()
    {
        InMemoryGraphStore store = new();
        await store.SaveDocumentAsync(new("doc", "src", "different-space", [new("c", "doc", "src", "text", [1, 0])], [], []), default);
        GraphRagService service = CreateService(store: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync()); Assert.False(service.Ready);
    }

    [Fact]
    public async Task BoundedQueueAppliesBackpressureAndCancellation()
    {
        GraphRagService service = CreateService();
        using GraphRag.Infrastructure.Hosting.IngestionQueue queue = new(service, new RagOptions { QueueCapacity = 1 });
        using CancellationTokenSource first = new();
        Task<IngestResult> pending = queue.EnqueueAsync(new("test://first", "Northwind."), first.Token);
        using CancellationTokenSource second = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAsync(new("test://second", "Contoso."), second.Token));
        first.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    public static GraphRagService CreateService(IAgentTeam? team = null, IExternalKnowledgeSource? external = null, float threshold = 0.45f, IGraphStore? store = null)
    {
        FixtureChatClient client = new();
        return new GraphRagService(store ?? new InMemoryGraphStore(), new FixtureEmbeddingGenerator(), new SemanticKernelGraphExtractor(client),
            team ?? new MafAgentTeam(client), external ?? new CountingExternal(false), new VectorIndex(), new RagOptions { Fixture = true, ConfidenceThreshold = threshold });
    }
    private sealed class RejectingTeam : IAgentTeam
    {
        public int Calls { get; private set; }
        public Task<AgentTurn> RunAsync(string question, Evidence[] evidence, string? feedback, Func<StreamEvent, ValueTask>? emit, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new AgentTurn(new Draft("Unsupported fact", [evidence[0].Id]), new("[Retrieval]", "[Relevant]", "[No support]", 1, "Unsupported."))); }
        public Task<Draft> GenerateNaiveAsync(string question, Evidence[] evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class CountingExternal(bool enabled = true) : IExternalKnowledgeSource
    {
        public bool Enabled => enabled;
        public int Calls { get; private set; }
        public Task<Evidence[]> SearchAsync(string query, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<Evidence[]>([new("external", "test://external", "Northwind Orion risk is described by Contoso.", 0.8f, "external")]); }
    }
    private sealed class FailingExternal : IExternalKnowledgeSource
    {
        public bool Enabled => true;
        public Task<Evidence[]> SearchAsync(string query, CancellationToken cancellationToken) => throw new HttpRequestException("upstream unavailable");
    }
}

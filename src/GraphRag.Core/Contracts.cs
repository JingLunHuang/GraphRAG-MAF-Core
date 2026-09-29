using Microsoft.Extensions.AI;

namespace GraphRag.Core;

public sealed record DocumentInput(string Source, string Text);
public sealed record TextChunk(string Id, string DocumentId, string Source, string Text, float[] Vector);
public sealed record GraphEntity(string Id, string Name, string Type, string Description, string[] ChunkIds);
public sealed record GraphRelation(string SourceId, string TargetId, string Description, string ChunkId, float Weight = 1);
public sealed record GraphDocument(string Id, string Source, string EmbeddingSpace, TextChunk[] Chunks, GraphEntity[] Entities, GraphRelation[] Relations);
public sealed record ExtractedEntity(string Name, string Type, string Description);
public sealed record ExtractedRelation(string Source, string Target, string Description);
public sealed record Extraction(ExtractedEntity[] Entities, ExtractedRelation[] Relations);
public sealed record LeidenOptions(double Gamma = 1, double Theta = 0.01, int MaxLevels = 10, int RandomSeed = 19);
public sealed record CommunityPartition(string Id, int Level, string[] EntityIds, string[] ChunkIds);
public sealed record CommunitySummary(string Id, int Level, string Summary, string[] ChunkIds);
public sealed record GraphSnapshot(TextChunk[] Chunks, CommunitySummary[] Communities, string? EmbeddingSpace);
public sealed record Evidence(string Id, string Source, string Text, float Score, string Kind = "chunk");
public sealed record Citation(string Id, string Source);
public sealed record Draft(string Answer, string[] Citations, bool Abstained = false);
public sealed record ReflectionDecision(string Retrieval, string Relevance, string Support, int Utility, string Reason)
{
    public bool Accepted => Relevance == "[Relevant]" && Support == "[Fully supported]" && Utility >= 3;
}
public sealed record AgentTurn(Draft Draft, ReflectionDecision Reflection);
public sealed record QueryRequest(string Question, string Mode = "local", int TopK = 5, int Hops = 2);
public sealed record AnswerResult(string Question, string Mode, string Answer, Citation[] Citations, Evidence[] Evidence,
    ReflectionDecision Reflection, float RetrievalConfidence, bool UsedFallback, int Attempts, string TraceId, string[] Warnings);
public sealed record StreamEvent(string Kind, string Text, AnswerResult? Result = null);
public sealed record IngestResult(string DocumentId, int Chunks, int Entities, int Relations);
public sealed record CommunityBuildResult(int Communities, int Levels, double Gamma, double Theta, string Algorithm);

public interface IGraphStore
{
    string Algorithm { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<GraphSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task SaveDocumentAsync(GraphDocument document, CancellationToken cancellationToken);
    Task<TextChunk[]> ExpandAsync(string[] chunkIds, int hops, int limit, CancellationToken cancellationToken);
    Task<CommunityPartition[]> PartitionAsync(LeidenOptions options, CancellationToken cancellationToken);
    Task SaveCommunitiesAsync(CommunitySummary[] summaries, CancellationToken cancellationToken);
}

public interface IGraphExtractor
{
    Task<Extraction> ExtractAsync(string text, CancellationToken cancellationToken);
    Task<string> SummarizeAsync(Evidence[] evidence, CancellationToken cancellationToken);
}

public interface IAgentTeam
{
    Task<AgentTurn> RunAsync(string question, Evidence[] evidence, string? feedback,
        Func<StreamEvent, ValueTask>? emit, CancellationToken cancellationToken);
    Task<Draft> GenerateNaiveAsync(string question, Evidence[] evidence, CancellationToken cancellationToken);
}

public interface IExternalKnowledgeSource
{
    bool Enabled { get; }
    Task<Evidence[]> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed record RagOptions
{
    public string EmbeddingSpace { get; init; } = "fixture-hash-128-v1";
    public int ChunkSize { get; init; } = 1200;
    public int ChunkOverlap { get; init; } = 150;
    public int QueueCapacity { get; init; } = 16;
    public int MaxAttempts { get; init; } = 2;
    public float ConfidenceThreshold { get; init; } = 0.45f;
    public int StreamCapacity { get; init; } = 8;
    public int MaxEvidenceCharacters { get; init; } = 24000;
    public bool Fixture { get; init; }

    public void Validate()
    {
        if (ChunkSize < 100 || ChunkOverlap < 0 || ChunkOverlap >= ChunkSize)
            throw new ArgumentException("Chunk size must be >= 100 and overlap must be smaller than chunk size.");
        if (QueueCapacity < 1 || StreamCapacity < 1 || MaxAttempts is < 1 or > 5 ||
            ConfidenceThreshold is < 0 or > 1 || MaxEvidenceCharacters < ChunkSize)
            throw new ArgumentException("Invalid bounded pipeline or retrieval settings.");
        ArgumentException.ThrowIfNullOrWhiteSpace(EmbeddingSpace);
    }
}

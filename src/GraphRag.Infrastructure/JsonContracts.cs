using System.Text.Json;
using System.Text.Json.Serialization;
using GraphRag.Core;
using GraphRag.Infrastructure.Evaluation;

namespace GraphRag.Infrastructure;

public sealed record WireMessage(string Role, string Content);
public sealed record ChatWireRequest(string Model, WireMessage[] Messages, bool Stream, float? Temperature, int? Max_tokens, StreamWireOptions? Stream_options = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WireResponseFormat? Response_format = null);
public sealed record WireResponseFormat(string Type = "json_object");
public sealed record StreamWireOptions(bool Include_usage = true);
public sealed record EmbeddingWireRequest(string Model, string[] Input);
public sealed record OllamaChatRequest(string Model, WireMessage[] Messages, bool Stream, OllamaGenerationOptions Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null);
public sealed record OllamaGenerationOptions(float Temperature, int Num_predict);
public sealed record QueryPayload(string Question, Evidence[] Evidence, string? Feedback);
public sealed record Neo4jStatement(string Statement, JsonElement Parameters);
public sealed record Neo4jRequest(Neo4jStatement[] Statements);
public sealed record Neo4jDocumentParameters(string Id, string Source, string EmbeddingSpace, TextChunk[] Chunks, GraphEntity[] Entities, GraphRelation[] Relations);
public sealed record Neo4jIdsParameters(string[] Ids, int Limit);
public sealed record Neo4jGraphParameters(string Name, double Gamma, double Theta, int MaxLevels, int RandomSeed);
public sealed record Neo4jCommunityParameters(CommunitySummary[] Summaries);
public sealed record ExternalSearchPayload(Evidence[] Results);
public sealed record HealthResponse(string Status, string Provider, string GraphStore, bool NativeAot, int Chunks);
public sealed record ErrorResponse(string Error, string TraceId);
public sealed record CorpusInput(DocumentInput[] Documents);
public sealed record SmokeResult(string Status, IngestResult[] Ingestion, CommunityBuildResult Communities, AnswerResult Local, AnswerResult Global, AnswerResult Naive);
public sealed record OnnxProbeResult(string ArtifactType, string ModelId, int Dimensions, float SameTopicCosine, float DifferentTopicCosine, string Status);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DocumentInput))]
[JsonSerializable(typeof(DocumentInput[]))]
[JsonSerializable(typeof(CorpusInput))]
[JsonSerializable(typeof(Extraction))]
[JsonSerializable(typeof(Draft))]
[JsonSerializable(typeof(ReflectionDecision))]
[JsonSerializable(typeof(QueryPayload))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(AnswerResult))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(IngestResult))]
[JsonSerializable(typeof(LeidenOptions))]
[JsonSerializable(typeof(CommunityBuildResult))]
[JsonSerializable(typeof(ChatWireRequest))]
[JsonSerializable(typeof(EmbeddingWireRequest))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(Neo4jRequest))]
[JsonSerializable(typeof(Neo4jDocumentParameters))]
[JsonSerializable(typeof(Neo4jIdsParameters))]
[JsonSerializable(typeof(Neo4jGraphParameters))]
[JsonSerializable(typeof(Neo4jCommunityParameters))]
[JsonSerializable(typeof(ExternalSearchPayload))]
[JsonSerializable(typeof(Evidence[]))]
[JsonSerializable(typeof(WireMessage[]))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SmokeResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(EvaluationCase[]))]
[JsonSerializable(typeof(JudgePayload))]
[JsonSerializable(typeof(JudgeAssessment))]
[JsonSerializable(typeof(EvaluationReport))]
[JsonSerializable(typeof(BenchmarkReport))]
[JsonSerializable(typeof(OnnxProbeResult))]
public partial class RagJsonContext : JsonSerializerContext;

public static class ModelJson
{
    public static string Extract(string text)
    {
        string value = text.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            int newline = value.IndexOf('\n');
            if (newline < 0 || !value.EndsWith("```", StringComparison.Ordinal)) throw new JsonException("Malformed model JSON fence.");
            value = value[(newline + 1)..^3].Trim();
        }
        return value;
    }
}

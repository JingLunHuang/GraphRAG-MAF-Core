using System.Globalization;
using GraphRag.Core;
using GraphRag.Infrastructure.Agents;
using GraphRag.Infrastructure.Evaluation;
using GraphRag.Infrastructure.Graph;
using GraphRag.Infrastructure.Mcp;
using GraphRag.Infrastructure.Providers;
using GraphRag.Infrastructure.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenInference.NET.Core;
using OpenInference.NET.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GraphRag.Infrastructure.Hosting;

public sealed record RuntimeSettings(string Provider, string EmbeddingProvider, string GraphStore, bool CaptureContent, string? AppApiKey)
{
    public static string Env(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) ?? fallback;
    public static int Int(string name, int fallback) => int.Parse(Env(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    public static double Double(string name, double fallback) => double.Parse(Env(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    public static Uri Uri(string name, string fallback) => new(Env(name, fallback).TrimEnd('/') + "/", UriKind.Absolute);
}

public static class ServiceRegistration
{
    public static IServiceCollection AddGraphRag(this IServiceCollection services)
    {
        string provider = RuntimeSettings.Env("AI_PROVIDER", "fixture").ToLowerInvariant();
        string embeddingProvider = RuntimeSettings.Env("EMBEDDING_PROVIDER", provider).ToLowerInvariant();
        string graphStore = RuntimeSettings.Env("GRAPH_STORE", provider == "fixture" ? "memory" : "neo4j").ToLowerInvariant();
        bool capture = RuntimeSettings.Env("TELEMETRY_CAPTURE_CONTENT", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (graphStore == "memory" && provider != "fixture") throw new ArgumentException("Memory graph store is restricted to fixture runs; configure Neo4j for real inference.");
        RuntimeSettings settings = new(provider, embeddingProvider, graphStore, capture, Environment.GetEnvironmentVariable("APP_API_KEY"));
        services.AddSingleton(settings);
        services.AddHttpClient("ai", client => client.Timeout = TimeSpan.FromSeconds(180));
        services.AddHttpClient("neo4j", client => client.Timeout = TimeSpan.FromSeconds(120));
        string embeddingModel = embeddingProvider switch
        {
            "fixture" => "hash-128-v1",
            "onnx" => RuntimeSettings.Env("ONNX_EMBEDDING_MODEL_ID", "all-MiniLM-L6-v2"),
            _ => RestSettings(embeddingProvider).EmbeddingModel
        };
        RagOptions options = new()
        {
            Fixture = provider == "fixture" || embeddingProvider == "fixture",
            EmbeddingSpace = RuntimeSettings.Env("EMBEDDING_SPACE", embeddingProvider == "fixture" ? "fixture-hash-128-v1" : embeddingProvider + ":" + embeddingModel),
            ChunkSize = RuntimeSettings.Int("CHUNK_SIZE", 1200), ChunkOverlap = RuntimeSettings.Int("CHUNK_OVERLAP", 150),
            QueueCapacity = RuntimeSettings.Int("INGESTION_CAPACITY", 16), StreamCapacity = RuntimeSettings.Int("STREAM_CAPACITY", 8),
            MaxAttempts = RuntimeSettings.Int("MAX_CORRECTIONS", 2), ConfidenceThreshold = (float)RuntimeSettings.Double("RETRIEVAL_THRESHOLD", 0.45),
            MaxEvidenceCharacters = RuntimeSettings.Int("MAX_EVIDENCE_CHARACTERS", 24000)
        };
        options.Validate(); services.AddSingleton(options);
        services.AddSingleton<IChatClient>(sp => new TelemetryChatClient(CreateChat(provider, sp), capture));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => embeddingProvider switch
        {
            "fixture" => new FixtureEmbeddingGenerator(),
            "onnx" => new OnnxEmbeddingGenerator(RuntimeSettings.Env("ONNX_EMBEDDING_PATH", "models/embedding/model.onnx"),
                RuntimeSettings.Env("ONNX_VOCAB_PATH", "models/embedding/vocab.txt"), embeddingModel,
                RuntimeSettings.Env("ONNX_EMBEDDING_OUTPUT", "last_hidden_state"), RuntimeSettings.Int("ONNX_MAX_EMBEDDING_TOKENS", 256)),
            "ollama" or "azure" or "openai-compatible" => new RestEmbeddingGenerator(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai"), RestSettings(embeddingProvider)),
            _ => throw new ArgumentException("Unsupported embedding provider: " + embeddingProvider)
        });
        services.AddSingleton<IGraphStore>(sp => graphStore switch
        {
            "memory" => new InMemoryGraphStore(),
            "neo4j" => new Neo4jGraphStore(sp.GetRequiredService<IHttpClientFactory>().CreateClient("neo4j"), RuntimeSettings.Uri("NEO4J_HTTP", "http://localhost:7474/"),
                RuntimeSettings.Env("NEO4J_DATABASE", "neo4j"), RuntimeSettings.Env("NEO4J_USER", "neo4j"), Required("NEO4J_PASSWORD")),
            _ => throw new ArgumentException("Unsupported graph store: " + graphStore)
        });
        services.AddSingleton<IExternalKnowledgeSource>(_ => new McpKnowledgeSource(
            string.IsNullOrWhiteSpace(RuntimeSettings.Env("MCP_ENDPOINT")) ? null : new Uri(RuntimeSettings.Env("MCP_ENDPOINT")),
            string.IsNullOrWhiteSpace(RuntimeSettings.Env("MCP_SEARCH_TOOL")) ? null : RuntimeSettings.Env("MCP_SEARCH_TOOL")));
        services.AddSingleton<IGraphExtractor, SemanticKernelGraphExtractor>();
        services.AddSingleton<IAgentTeam, MafAgentTeam>();
        services.AddSingleton<VectorIndex>();
        services.AddSingleton<GraphRagService>();
        services.AddSingleton<IngestionQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<IngestionQueue>());
        services.AddKeyedSingleton<IChatClient>("judge", (sp, _) => new TelemetryChatClient(CreateChat(RuntimeSettings.Env("JUDGE_PROVIDER", provider), sp, "JUDGE_"), capture));
        services.AddSingleton(sp => new RagasEvaluator(sp.GetRequiredKeyedService<IChatClient>("judge"), sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));
        services.AddOpenInferenceTelemetry(instrumentation =>
        { instrumentation.EmitTextContent = capture; instrumentation.RecordTokenUsage = true; instrumentation.SanitizeSensitiveInfo = true; instrumentation.EmitMetrics = true; });
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("GraphRAG-MAF-Core", serviceVersion: "1.0.0"))
            .WithTracing(traces =>
            {
                traces.AddSource(GraphRagService.Activities.Name, LlmTelemetry.ActivitySource.Name);
                if (!string.IsNullOrWhiteSpace(RuntimeSettings.Env("OTEL_EXPORTER_OTLP_ENDPOINT"))) traces.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TelemetryChatClient.Metrics.Name);
                if (!string.IsNullOrWhiteSpace(RuntimeSettings.Env("OTEL_EXPORTER_OTLP_ENDPOINT"))) metrics.AddOtlpExporter();
            });
        return services;
    }

    private static string Required(string name) => string.IsNullOrWhiteSpace(RuntimeSettings.Env(name)) ? throw new ArgumentException("Required environment variable is missing: " + name) : RuntimeSettings.Env(name);
    private static IChatClient CreateChat(string provider, IServiceProvider services, string prefix = "") => provider.ToLowerInvariant() switch
    {
        "fixture" => new FixtureChatClient(),
        "onnx" => new OnnxChatClient(RuntimeSettings.Env(prefix + "ONNX_CHAT_DIRECTORY", RuntimeSettings.Env("ONNX_CHAT_DIRECTORY", "models/chat")),
            RuntimeSettings.Env("ONNX_CHAT_MODEL_ID", "llama3-onnx"), RuntimeSettings.Env("ONNX_CHAT_TEMPLATE", "llama3"), RuntimeSettings.Int("ONNX_CONTEXT_LENGTH", 8192)),
        "ollama" or "azure" or "openai-compatible" => new RestChatClient(services.GetRequiredService<IHttpClientFactory>().CreateClient("ai"), RestSettings(provider, prefix)),
        _ => throw new ArgumentException("Unsupported chat provider: " + provider)
    };
    public static RestAiSettings RestSettings(string provider, string prefix = "")
    {
        string Default(string name, string fallback) => RuntimeSettings.Env(prefix + name, RuntimeSettings.Env(name, fallback));
        return provider switch
        {
            "ollama" => new(provider, new Uri(Default("OLLAMA_ENDPOINT", "http://localhost:11434/").TrimEnd('/') + "/"), Default("CHAT_MODEL", "llama3.2:3b"), Default("EMBEDDING_MODEL", "nomic-embed-text")),
            "azure" => new(provider, new Uri(Default("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/").TrimEnd('/') + "/"), Default("CHAT_MODEL", "gpt-4o-mini"),
                Default("EMBEDDING_MODEL", "text-embedding-3-small"), Default("AZURE_OPENAI_API_KEY", ""), Default("AZURE_OPENAI_API_VERSION", "2024-10-21")),
            "openai-compatible" => new(provider, new Uri(Default("AI_ENDPOINT", "http://localhost:8000/v1/").TrimEnd('/') + "/"), Default("CHAT_MODEL", "local-chat"), Default("EMBEDDING_MODEL", "local-embedding"), Default("AI_API_KEY", "")),
            _ => throw new ArgumentException("Unsupported REST provider: " + provider)
        };
    }
}

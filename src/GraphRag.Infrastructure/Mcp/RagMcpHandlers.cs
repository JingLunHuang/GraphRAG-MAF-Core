using System.Text.Json;
using GraphRag.Core;
using GraphRag.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GraphRag.Infrastructure.Mcp;

public static class RagMcpHandlers
{
    // Explicit protocol handlers and literal JSON schemas avoid reflection discovery in Native AOT.
    public static IMcpServerBuilder WithRagTools(this IMcpServerBuilder builder) => builder
        .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
        {
            Tools = [Tool("rag_query", "Query local, global, or naive RAG with citations and critic results.",
                "{\"type\":\"object\",\"properties\":{\"question\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\",\"enum\":[\"local\",\"global\",\"naive\"]},\"topK\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50},\"hops\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3}},\"required\":[\"question\"],\"additionalProperties\":false}"),
                Tool("rag_ingest", "Ingest a UTF-8 document through the bounded ingestion queue.",
                "{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}},\"required\":[\"source\",\"text\"],\"additionalProperties\":false}"),
                Tool("rag_build_communities", "Run Neo4j GDS Leiden and generate hierarchical community reports.",
                "{\"type\":\"object\",\"properties\":{\"gamma\":{\"type\":\"number\"},\"theta\":{\"type\":\"number\"},\"maxLevels\":{\"type\":\"integer\"},\"randomSeed\":{\"type\":\"integer\"}},\"additionalProperties\":false}")]
        }))
        .WithCallToolHandler(async (context, cancellationToken) =>
        {
            var arguments = context.Params?.Arguments ?? new Dictionary<string, JsonElement>();
            string Required(string name) => arguments.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Missing string argument: " + name);
            int Integer(string name, int fallback) => arguments.TryGetValue(name, out JsonElement value) ? value.GetInt32() : fallback;
            double Number(string name, double fallback) => arguments.TryGetValue(name, out JsonElement value) ? value.GetDouble() : fallback;
            try
            {
                GraphRagService service = context.Services!.GetRequiredService<GraphRagService>();
                string json;
                switch (context.Params?.Name)
                {
                    case "rag_query":
                        QueryRequest request = new(Required("question"), arguments.ContainsKey("mode") ? Required("mode") : "local", Integer("topK", 5), Integer("hops", 2));
                        AnswerResult answer = await service.QueryAsync(request, cancellationToken);
                        json = JsonSerializer.Serialize(answer, RagJsonContext.Default.AnswerResult); break;
                    case "rag_ingest":
                        IngestResult result = await context.Services!.GetRequiredService<IngestionQueue>().EnqueueAsync(new DocumentInput(Required("source"), Required("text")), cancellationToken);
                        json = JsonSerializer.Serialize(result, RagJsonContext.Default.IngestResult); break;
                    case "rag_build_communities":
                        CommunityBuildResult communities = await service.BuildCommunitiesAsync(new LeidenOptions(Number("gamma", 1), Number("theta", 0.01), Integer("maxLevels", 10), Integer("randomSeed", 19)), cancellationToken);
                        json = JsonSerializer.Serialize(communities, RagJsonContext.Default.CommunityBuildResult); break;
                    default: throw new McpException("Unknown tool.");
                }
                return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
            { return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] }; }
        });

    private static Tool Tool(string name, string description, string schema) => new()
    { Name = name, Description = description, InputSchema = JsonDocument.Parse(schema).RootElement.Clone() };
}

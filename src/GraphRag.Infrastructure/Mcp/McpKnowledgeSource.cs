using System.Diagnostics;
using System.Text.Json;
using GraphRag.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GraphRag.Infrastructure.Mcp;

public sealed class McpKnowledgeSource(Uri? endpoint, string? toolName = null) : IExternalKnowledgeSource
{
    public bool Enabled => endpoint is not null;
    public async Task<Evidence[]> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (endpoint is null) return [];
        using Activity? activity = GraphRagService.Activities.StartActivity("mcp.corrective-search");
        activity?.SetTag("openinference.span.kind", "TOOL");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        { Endpoint = endpoint, Name = "GraphRAG CRAG fallback" }), cancellationToken: timeout.Token);
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        McpClientTool? selected = tools.FirstOrDefault(tool => (toolName is null ? tool.Name.Contains("search", StringComparison.OrdinalIgnoreCase) : tool.Name == toolName)
            && QueryParameter(tool.JsonSchema) is not null);
        if (selected is null) throw new InvalidOperationException("MCP endpoint exposes no configured search tool with a query or q string parameter.");
        string parameter = QueryParameter(selected.JsonSchema)!;
        activity?.SetTag("tool.name", selected.Name);
        CallToolResult response = await client.CallToolAsync(new CallToolRequestParams
        { Name = selected.Name, Arguments = new Dictionary<string, JsonElement> { [parameter] = JsonSerializer.SerializeToElement(query, RagJsonContext.Default.Options.GetTypeInfo(typeof(string))) } }, timeout.Token);
        if (response.IsError == true) throw new InvalidOperationException("MCP search tool returned an error.");
        List<Evidence> results = [];
        foreach (TextContentBlock block in response.Content.OfType<TextContentBlock>())
        {
            try
            {
                ExternalSearchPayload? payload = JsonSerializer.Deserialize(block.Text, RagJsonContext.Default.ExternalSearchPayload);
                if (payload?.Results is { Length: > 0 })
                {
                    results.AddRange(payload.Results.Select(e => e with { Id = "mcp-" + TextProcessing.Id(e.Source + e.Id), Kind = "external", Score = Math.Clamp(e.Score, 0, 1) }));
                    continue;
                }
            }
            catch (JsonException) { }
            if (!string.IsNullOrWhiteSpace(block.Text)) results.Add(new Evidence("mcp-" + TextProcessing.Id(block.Text),
                endpoint.GetLeftPart(UriPartial.Path) + "#" + selected.Name, block.Text, 0, "external"));
        }
        return results.Where(e => !string.IsNullOrWhiteSpace(e.Text) && !string.IsNullOrWhiteSpace(e.Source)).Take(20).ToArray();
    }

    private static string? QueryParameter(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties)) return null;
        foreach (string name in new[] { "query", "q" })
            if (properties.TryGetProperty(name, out JsonElement definition) && definition.TryGetProperty("type", out JsonElement type) && type.GetString() == "string") return name;
        return null;
    }
}

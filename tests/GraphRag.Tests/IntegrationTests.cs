using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using GraphRag.Core;
using GraphRag.Infrastructure;
using GraphRag.Infrastructure.Mcp;
using GraphRag.Infrastructure.Providers;
using GraphRag.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GraphRag.Tests;

public sealed class IntegrationTests
{
    [Fact]
    public async Task McpFallbackDiscoversAndCallsAnExternalSearchTool()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        string? searched = null;
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool { Name = "enterprise_search", Description = "Search enterprise evidence",
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}},\"required\":[\"q\"]}").RootElement.Clone() }]
            }))
            .WithCallToolHandler((context, _) =>
            {
                searched = context.Params!.Arguments!["q"].GetString();
                string json = JsonSerializer.Serialize(new ExternalSearchPayload([new Evidence("id", "https://example.test/report", "A documented enterprise fact.", 0.8f)]), RagJsonContext.Default.ExternalSearchPayload);
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = json }] });
            });
        await using WebApplication app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            McpKnowledgeSource source = new(new Uri(app.Urls.Single() + "/mcp"));
            Evidence[] evidence = await source.SearchAsync("enterprise risk?", default);
            Assert.Equal("enterprise risk?", searched);
            Evidence item = Assert.Single(evidence);
            Assert.Equal("external", item.Kind); Assert.StartsWith("mcp-", item.Id);
            Assert.Equal("https://example.test/report", item.Source);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task TelemetryRecordsUsageWithoutCapturingPromptsByDefault()
    {
        List<Activity> spans = [];
        string sourceName = OpenInference.NET.Core.LlmTelemetry.ActivitySource.Name;
        using ActivityListener activities = new()
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add(activity)
        };
        ActivitySource.AddActivityListener(activities);
        List<(string Name, long Value)> measured = [];
        using MeterListener metrics = new();
        metrics.InstrumentPublished = (instrument, listener) =>
        { if (instrument.Meter.Name == TelemetryChatClient.Metrics.Name) listener.EnableMeasurementEvents(instrument); };
        metrics.SetMeasurementEventCallback<long>((instrument, value, _, _) => measured.Add((instrument.Name, value)));
        metrics.Start();
        using TelemetryChatClient client = new(new UsageClient(), captureContent: false);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "private-test-prompt")]);
        Activity span = Assert.Single(spans);
        Assert.Equal("LLM", span.GetTagItem("openinference.span.kind"));
        Assert.Equal(3L, span.GetTagItem("llm.token_count.prompt"));
        Assert.Equal(2L, span.GetTagItem("llm.token_count.completion"));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString()?.Contains("private-test-prompt", StringComparison.Ordinal) == true);
        Assert.Contains(measured, metric => metric.Name == "llm.tokens" && metric.Value == 3);
        Assert.Contains(measured, metric => metric.Name == "llm.tokens" && metric.Value == 2);
    }

    private sealed class UsageClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("test", defaultModelId: "test-model") : null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "response")) { Usage = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 2, TotalTokenCount = 5 } });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

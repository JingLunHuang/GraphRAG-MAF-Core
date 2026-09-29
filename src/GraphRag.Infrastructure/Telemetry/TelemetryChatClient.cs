using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using OpenInference.NET.Core;

namespace GraphRag.Infrastructure.Telemetry;

public sealed class TelemetryChatClient(IChatClient inner, bool captureContent) : IChatClient
{
    public static readonly Meter Metrics = new("GraphRag.AI", "1.0.0");
    private static readonly Histogram<double> Latency = Metrics.CreateHistogram<double>("llm.duration", "ms");
    private static readonly Counter<long> Tokens = Metrics.CreateCounter<long>("llm.tokens", "tokens");
    private static readonly Counter<long> Calls = Metrics.CreateCounter<long>("llm.calls");
    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
    public void Dispose() => inner.Dispose();

    private Activity? Start(ChatMessage[] messages)
    {
        ChatClientMetadata? metadata = inner.GetService<ChatClientMetadata>();
        Activity? activity = LlmTelemetry.StartLlmActivity(metadata?.DefaultModelId ?? "unknown",
            captureContent ? string.Join("\n", messages.Select(m => m.Role.Value + ": " + m.Text)) : "", "chat", metadata?.ProviderName ?? "unknown");
        activity?.SetTag("openinference.span.kind", "LLM");
        return activity;
    }
    private static void Record(UsageDetails? usage, double elapsed, bool success)
    {
        Latency.Record(elapsed); Calls.Add(1, new KeyValuePair<string, object?>("success", success));
        if (usage?.InputTokenCount is long input) Tokens.Add(input, new KeyValuePair<string, object?>("direction", "input"));
        if (usage?.OutputTokenCount is long output) Tokens.Add(output, new KeyValuePair<string, object?>("direction", "output"));
    }
    private static void Usage(Activity? activity, UsageDetails? usage)
    {
        if (usage?.InputTokenCount is long input) activity?.SetTag("llm.token_count.prompt", input);
        if (usage?.OutputTokenCount is long output) activity?.SetTag("llm.token_count.completion", output);
        if (usage?.TotalTokenCount is long total) activity?.SetTag("llm.token_count.total", total);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage[] materialized = messages.ToArray();
        using Activity? activity = Start(materialized);
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            ChatResponse response = await inner.GetResponseAsync(materialized, options, cancellationToken);
            Usage(activity, response.Usage); Record(response.Usage, watch.Elapsed.TotalMilliseconds, true);
            LlmTelemetry.EndLlmActivity(activity, captureContent ? response.Text : "", true, watch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
            Record(null, watch.Elapsed.TotalMilliseconds, false);
            LlmTelemetry.EndLlmActivity(activity, "", false, watch.ElapsedMilliseconds); throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] materialized = messages.ToArray();
        using Activity? activity = Start(materialized);
        Stopwatch watch = Stopwatch.StartNew();
        StringBuilder output = new(); UsageDetails? usage = null; bool completed = false;
        try
        {
            await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(materialized, options, cancellationToken))
            {
                if (captureContent) output.Append(update.Text);
                usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
                yield return update;
            }
            completed = true;
        }
        finally
        {
            Usage(activity, usage); Record(usage, watch.Elapsed.TotalMilliseconds, completed);
            if (!completed) activity?.SetStatus(ActivityStatusCode.Error, "stream did not complete");
            LlmTelemetry.EndLlmActivity(activity, captureContent ? output.ToString() : "", completed, watch.ElapsedMilliseconds);
        }
    }
}

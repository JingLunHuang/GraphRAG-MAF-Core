using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GraphRag.Core;
using Microsoft.Extensions.AI;

namespace GraphRag.Infrastructure.Providers;

public sealed record RestAiSettings(string Provider, Uri Endpoint, string ChatModel, string EmbeddingModel, string? ApiKey = null, string ApiVersion = "2024-10-21");

internal static class RestProtocol
{
    public static HttpRequestMessage Request(RestAiSettings settings, string operation, string json, bool streaming = false)
    {
        string path;
        if (settings.Provider == "azure")
        {
            string model = operation == "embeddings" ? settings.EmbeddingModel : settings.ChatModel;
            path = $"openai/deployments/{Uri.EscapeDataString(model)}/{operation}?api-version={Uri.EscapeDataString(settings.ApiVersion)}";
        }
        else path = settings.Provider == "ollama" ? (operation == "embeddings" ? "api/embed" : "api/chat") : operation;
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(settings.Endpoint, path));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            if (settings.Provider == "azure") request.Headers.Add("api-key", settings.ApiKey);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }
        if (streaming && settings.Provider != "ollama") request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    public static WireMessage[] Messages(IEnumerable<ChatMessage> messages, ChatOptions? options = null) =>
        [.. string.IsNullOrWhiteSpace(options?.Instructions) ? Array.Empty<WireMessage>() : [new WireMessage("system", options.Instructions)],
            .. messages.Select(m => new WireMessage(m.Role.Value, m.Text))];
    public static UsageDetails? Usage(JsonElement root, bool ollama)
    {
        if (ollama)
        {
            if (!root.TryGetProperty("prompt_eval_count", out JsonElement input)) return null;
            long output = root.TryGetProperty("eval_count", out JsonElement count) ? count.GetInt64() : 0;
            return new UsageDetails { InputTokenCount = input.GetInt64(), OutputTokenCount = output, TotalTokenCount = input.GetInt64() + output };
        }
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object) return null;
        long? Read(string name) => usage.TryGetProperty(name, out JsonElement count) ? count.GetInt64() : null;
        return new UsageDetails { InputTokenCount = Read("prompt_tokens"), OutputTokenCount = Read("completion_tokens"), TotalTokenCount = Read("total_tokens") };
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        // Provider bodies may echo prompts or authorization data. Keep diagnostics secret-safe.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        throw new HttpRequestException($"AI provider returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
    }
}

public sealed class RestChatClient(HttpClient http, RestAiSettings settings) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(settings.Provider, settings.Endpoint, settings.ChatModel) :
        serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    private string Body(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        WireMessage[] wire = RestProtocol.Messages(messages, options);
        bool json = options?.ResponseFormat is ChatResponseFormatJson;
        return settings.Provider == "ollama"
            ? JsonSerializer.Serialize(new OllamaChatRequest(settings.ChatModel, wire, stream, new(options?.Temperature ?? 0, options?.MaxOutputTokens ?? 2048), json ? "json" : null), RagJsonContext.Default.OllamaChatRequest)
            : JsonSerializer.Serialize(new ChatWireRequest(settings.ChatModel, wire, stream, options?.Temperature ?? 0, options?.MaxOutputTokens ?? 2048,
                stream ? new StreamWireOptions() : null, json ? new WireResponseFormat() : null), RagJsonContext.Default.ChatWireRequest);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = RestProtocol.Request(settings, "chat/completions", Body(messages, options, false));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await RestProtocol.EnsureSuccessAsync(response, cancellationToken);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement root = doc.RootElement;
        string content = settings.Provider == "ollama" ? root.GetProperty("message").GetProperty("content").GetString() ?? "" :
            root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        { ModelId = settings.ChatModel, Usage = RestProtocol.Usage(root, settings.Provider == "ollama") };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = RestProtocol.Request(settings, "chat/completions", Body(messages, options, true), true);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await RestProtocol.EnsureSuccessAsync(response, cancellationToken);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            string data;
            if (settings.Provider == "ollama") data = line;
            else
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                data = line[5..].TrimStart();
            }
            if (data == "[DONE]") break;
            if (string.IsNullOrWhiteSpace(data)) continue;
            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) throw new HttpRequestException("AI streaming provider reported an error.");
            string text = "";
            if (settings.Provider == "ollama")
            {
                if (root.TryGetProperty("message", out JsonElement message)) text = message.GetProperty("content").GetString() ?? "";
            }
            else if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out JsonElement delta) && delta.TryGetProperty("content", out JsonElement content))
                text = content.GetString() ?? "";
            ChatResponseUpdate update = new(ChatRole.Assistant, text) { ModelId = settings.ChatModel };
            UsageDetails? usage = RestProtocol.Usage(root, settings.Provider == "ollama");
            if (usage is not null) update.Contents.Add(new UsageContent(usage));
            yield return update;
        }
    }
}

public sealed class RestEmbeddingGenerator(HttpClient http, RestAiSettings settings) : IEmbeddingGenerator<string, Embedding<float>>
{
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata(settings.Provider, settings.Endpoint, settings.EmbeddingModel) :
        serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string[] input = values.ToArray();
        if (input.Length == 0) return new GeneratedEmbeddings<Embedding<float>>();
        string json = JsonSerializer.Serialize(new EmbeddingWireRequest(settings.EmbeddingModel, input), RagJsonContext.Default.EmbeddingWireRequest);
        using HttpRequestMessage request = RestProtocol.Request(settings, "embeddings", json);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        await RestProtocol.EnsureSuccessAsync(response, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        IEnumerable<float[]> vectors = settings.Provider == "ollama"
            ? document.RootElement.GetProperty("embeddings").EnumerateArray().Select(e => e.EnumerateArray().Select(x => x.GetSingle()).ToArray())
            : document.RootElement.GetProperty("data").EnumerateArray().OrderBy(e => e.GetProperty("index").GetInt32())
                .Select(e => e.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray());
        GeneratedEmbeddings<Embedding<float>> result = new();
        foreach (float[] vector in vectors)
        {
            VectorIndex.Validate(vector);
            result.Add(new Embedding<float>(vector) { ModelId = settings.EmbeddingModel });
        }
        if (result.Count != input.Length) throw new InvalidOperationException("Embedding provider returned an unexpected batch length.");
        return result;
    }
}

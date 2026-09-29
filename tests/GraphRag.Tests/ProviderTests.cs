using System.Net;
using System.Text;
using System.Text.Json;
using GraphRag.Core;
using GraphRag.Infrastructure.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace GraphRag.Tests;

public sealed class ProviderTests
{
    [Theory]
    [InlineData("azure", "/openai/deployments/chat/chat/completions", "api-key")]
    [InlineData("ollama", "/api/chat", "Authorization")]
    [InlineData("openai-compatible", "/v1/chat/completions", "Authorization")]
    public async Task ProviderHonorsMafInstructionsAndRoutes(string provider, string expectedPath, string header)
    {
        string? path = null; string? body = null; bool credentialAttached = false;
        using HttpClient http = new(new Handler(async request =>
        {
            path = request.RequestUri!.AbsolutePath; body = await request.Content!.ReadAsStringAsync(); credentialAttached = request.Headers.Contains(header);
            return Json(provider == "ollama" ? "{\"message\":{\"content\":\"answer\"},\"prompt_eval_count\":3,\"eval_count\":2}" : "{\"choices\":[{\"message\":{\"content\":\"answer\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}");
        }));
        RestChatClient client = new(http, new(provider, new Uri(provider == "openai-compatible" ? "http://localhost/v1/" : "http://localhost/"), "chat", "embed", "test-fixture-key"));
        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "question")], new ChatOptions { Instructions = "System instruction" });
        Assert.Equal(expectedPath, path); Assert.True(credentialAttached); Assert.Equal("answer", response.Text); Assert.Equal(5, response.Usage!.TotalTokenCount);
        using JsonDocument payload = JsonDocument.Parse(body!);
        Assert.Equal("system", payload.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("System instruction", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OllamaJsonModeHonorsTheStructuredAgentContract()
    {
        string? body = null;
        using HttpClient http = new(new Handler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json("{\"message\":{\"content\":\"{}\"}}");
        }));
        RestChatClient client = new(http, new("ollama", new("http://localhost/"), "chat", "embed"));
        await client.GetResponseAsync([new(ChatRole.User, "Return JSON")], new ChatOptions { ResponseFormat = ChatResponseFormat.Json });
        using JsonDocument payload = JsonDocument.Parse(body!);
        Assert.Equal("json", payload.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task OpenAiStreamDeliversTextAndReportedUsage()
    {
        using HttpClient http = new(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":1,\"total_tokens\":5}}\n\ndata: [DONE]\n", Encoding.UTF8, "text/event-stream") })));
        RestChatClient client = new(http, new("openai-compatible", new("http://localhost/v1/"), "chat", "embed"));
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "q")])) updates.Add(update);
        Assert.Equal("hello", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(5, updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single().Details.TotalTokenCount);
    }

    [Fact]
    public async Task ProviderErrorBodyIsNotExposed()
    {
        using HttpClient http = new(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-prompt-credential") })));
        RestChatClient client = new(http, new("azure", new("https://example.invalid/"), "chat", "embed"));
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new(ChatRole.User, "q")]));
        Assert.DoesNotContain("secret-prompt", error.Message); Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task EmbeddingResponsePreservesInputOrder()
    {
        using HttpClient http = new(new Handler(_ => Task.FromResult(Json("{\"data\":[{\"index\":1,\"embedding\":[0,1]},{\"index\":0,\"embedding\":[1,0]}]}"))));
        RestEmbeddingGenerator generator = new(http, new("openai-compatible", new("http://localhost/v1/"), "chat", "embed"));
        var vectors = await generator.GenerateAsync(["one", "two"]);
        Assert.Equal(1, vectors[0].Vector.Span[0]); Assert.Equal(1, vectors[1].Vector.Span[1]);
    }

    [Fact]
    public void OnnxChatRejectsGgufWithoutGenaiConfig()
    { Assert.Throws<FileNotFoundException>(() => new OnnxChatClient("missing-model.gguf", "llama", "llama3")); }

    [Fact]
    public void BertWordpieceUsesVocabularyAndSubwords()
    {
        string file = Path.Combine(Path.GetTempPath(), "graph-rag-vocab-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(file, ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "hello", "world", "##s", "!"]);
        try { Assert.Equal<long>([2, 4, 5, 6, 7, 3], new BertWordPieceTokenizer(file).Encode("Héllo worlds!")); }
        finally { File.Delete(file); }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request); }
}

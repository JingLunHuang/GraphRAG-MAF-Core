using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GraphRag.Api;
using GraphRag.Core;
using GraphRag.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace GraphRag.Tests;

[Collection("ApiHost")]
public sealed class ApiSmokeTests
{
    private const string ApiKey = "smoke-test-key";

    private static async Task<(WebApplication App, HttpClient Client)> StartHostAsync(string? apiKey)
    {
        // Mirrors scripts/ci-native-smoke.sh: fully in-memory deterministic host.
        Environment.SetEnvironmentVariable("AI_PROVIDER", "fixture");
        Environment.SetEnvironmentVariable("EMBEDDING_PROVIDER", "fixture");
        Environment.SetEnvironmentVariable("GRAPH_STORE", "memory");
        Environment.SetEnvironmentVariable("APP_API_KEY", apiKey);
        try
        {
            WebApplication app = ApiProgram.BuildApi([]);
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            return (app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_PROVIDER", null);
            Environment.SetEnvironmentVariable("EMBEDDING_PROVIDER", null);
            Environment.SetEnvironmentVariable("GRAPH_STORE", null);
            Environment.SetEnvironmentVariable("APP_API_KEY", null);
        }
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string route, object body)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(route, body, RagJsonContext.Default.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string CorpusPath()
    {
        // Walk up from the test assembly output directory to the repo root.
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory.Parent is not null && !File.Exists(Path.Combine(directory.FullName, "GraphRag.slnx")))
            directory = directory.Parent;
        return Path.Combine(directory.FullName, "data", "demo-corpus.json");
    }

    private static async Task IngestCorpusAsync(HttpClient client)
    {
        DocumentInput[] corpus = JsonSerializer.Deserialize(await File.ReadAllTextAsync(CorpusPath()), RagJsonContext.Default.DocumentInputArray)!;
        foreach (DocumentInput document in corpus)
        {
            JsonElement result = await PostJsonAsync(client, "/api/v1/documents", document);
            Assert.True(result.GetProperty("chunks").GetInt32() > 0);
        }
    }

    [Fact]
    public async Task HostBootsAndHealthLiveReturnsOk()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(ApiKey);
        await using (app)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement health = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("live", health.GetProperty("status").GetString());
            Assert.Equal("fixture", health.GetProperty("provider").GetString());
            Assert.Equal("memory", health.GetProperty("graphStore").GetString());
            Assert.False(health.GetProperty("nativeAot").GetBoolean(), "Tests run under CoreCLR, not a Native AOT binary.");
        }
    }

    [Fact]
    public async Task HealthReadyReturns200WithExpectedFields()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(ApiKey);
        await using (app)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement health = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("ready", health.GetProperty("status").GetString());
            Assert.Equal("fixture", health.GetProperty("provider").GetString());
            Assert.Equal("memory", health.GetProperty("graphStore").GetString());
            Assert.Equal(0, health.GetProperty("chunks").GetInt32());
        }
    }

    [Fact]
    public async Task ApiKeyMiddlewareRejectsMissingAndWrongKeysAndAcceptsCorrectKey()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(ApiKey);
        await using (app)
        using (client)
        {
            using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/v1/query", new QueryRequest("q"), RagJsonContext.Default.QueryRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

            using HttpRequestMessage wrong = new(HttpMethod.Post, "/api/v1/query")
            { Content = JsonContent.Create(new QueryRequest("q"), options: RagJsonContext.Default.Options) };
            wrong.Headers.Add("X-API-Key", "wrong-key");
            using HttpResponseMessage rejected = await client.SendAsync(wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

            using HttpRequestMessage correct = new(HttpMethod.Post, "/api/v1/query")
            { Content = JsonContent.Create(new QueryRequest("q"), options: RagJsonContext.Default.Options) };
            correct.Headers.Add("X-API-Key", ApiKey);
            using HttpResponseMessage accepted = await client.SendAsync(correct);
            Assert.NotEqual(HttpStatusCode.Unauthorized, accepted.StatusCode);

            // Health endpoints bypass the API key by design.
            using HttpResponseMessage health = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Fact]
    public async Task HostWithoutApiKeyAcceptsAnonymousRequests()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(null);
        await using (app)
        using (client)
        {
            JsonElement result = await PostJsonAsync(client, "/api/v1/documents", new DocumentInput("test://open", "Northwind sells Orion."));
            Assert.True(result.GetProperty("chunks").GetInt32() > 0);
        }
    }

    [Fact]
    public async Task IngestThenQueryReturnsDeterministicFixtureAnswer()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(ApiKey);
        await using (app)
        using (client)
        {
            client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
            await IngestCorpusAsync(client);

            JsonElement communities = await PostJsonAsync(client, "/api/v1/communities", new LeidenOptions(Gamma: 1, Theta: 0.01, RandomSeed: 19, MaxLevels: 10));
            Assert.True(communities.GetProperty("communities").GetInt32() >= 1);

            JsonElement answer = await PostJsonAsync(client, "/api/v1/query",
                new QueryRequest("How can Fabrikam maintenance affect Northwind Orion releases?", Mode: "local", TopK: 2, Hops: 2));
            Assert.True(answer.GetProperty("citations").GetArrayLength() > 0);
            Assert.True(answer.GetProperty("reflection").GetProperty("accepted").GetBoolean());
        }
    }

    [Fact]
    public async Task StreamingQueryEmitsAgentDeltasAndFinalResult()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(ApiKey);
        await using (app)
        using (client)
        {
            client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
            await IngestCorpusAsync(client);

            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/query/stream")
            { Content = JsonContent.Create(new QueryRequest("How can Fabrikam maintenance affect Northwind Orion releases?", Mode: "local", TopK: 2, Hops: 2),
                options: RagJsonContext.Default.Options) };
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            string stream = await response.Content.ReadAsStringAsync();
            Assert.Contains("event: agent_delta", stream, StringComparison.Ordinal);
            Assert.Contains("event: result", stream, StringComparison.Ordinal);
            Assert.DoesNotContain("event: error", stream, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OpenApiDocumentIsServed()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync(ApiKey);
        await using (app)
        using (client)
        {
            using HttpResponseMessage response = await client.GetAsync("/openapi.json");
            // /openapi.json sits behind the X-API-Key middleware like every other non-/health route.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
            using HttpResponseMessage authenticated = await client.GetAsync("/openapi.json");
            Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
            JsonElement document = JsonDocument.Parse(await authenticated.Content.ReadAsStringAsync()).RootElement;
            Assert.True(document.TryGetProperty("openapi", out _));
        }
    }
}

[CollectionDefinition("ApiHost", DisableParallelization = true)]
public sealed class ApiHostCollection;

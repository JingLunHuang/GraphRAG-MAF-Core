using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using GraphRag.Core;
using GraphRag.Infrastructure.Graph;
using Xunit;

namespace GraphRag.Tests;

// These tests require a Docker daemon. The Neo4j image is built once per test run from
// deploy/neo4j/Dockerfile (neo4j:5.26.31-community + GDS 2.13.12) because InitializeAsync
// calls gds.version() and PartitionAsync runs gds.leiden.stream — a plain neo4j image fails.
// When Docker is unavailable every test reports inconclusive instead of failing, so local
// runs without Docker stay green. Run explicitly with: dotnet test --filter Category=Neo4j
[Collection("Neo4j")]
[Trait("Category", "Neo4j")]
public sealed class Neo4jGraphStoreTests(Neo4jFixture fixture)
{
    private const string Password = "test-password";

    private Neo4jGraphStore CreateStore() => new(new HttpClient { BaseAddress = fixture.Endpoint }, fixture.Endpoint, "neo4j", "neo4j", Password);

    private async Task<Neo4jGraphStore> ReadyStoreAsync()
    {
        Neo4jGraphStore store = CreateStore();
        await fixture.ResetAsync(Password);
        await store.InitializeAsync(default);
        return store;
    }

    private static GraphDocument SampleDocument(string id = "doc-1", string space = "test-space") => new(id, "test://" + id, space,
        [new("c1", id, "test://" + id, "Alpha sells Beta.", [1, 0]), new("c2", id, "test://" + id, "Beta buys Gamma.", [0, 1])],
        [new("e1", "Alpha", "ORG", "Seller", ["c1"]), new("e2", "Beta", "ORG", "Bridge", ["c1", "c2"]), new("e3", "Gamma", "ORG", "Buyer", ["c2"])],
        [new("e1", "e2", "sells", "c1", 1), new("e2", "e3", "buys", "c2", 1)]);

    [Fact]
    public async Task SaveAndLoadRoundTripsChunksAndEmbeddingSpace()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        Neo4jGraphStore store = await ReadyStoreAsync();
        await store.SaveDocumentAsync(SampleDocument(), default);
        GraphSnapshot snapshot = await store.LoadAsync(default);
        Assert.Equal("test-space", snapshot.EmbeddingSpace);
        Assert.Equal(["c1", "c2"], snapshot.Chunks.Select(c => c.Id));
        Assert.Equal("Alpha sells Beta.", snapshot.Chunks[0].Text);
        Assert.Equal([1f, 0f], snapshot.Chunks[0].Vector);
        Assert.Empty(snapshot.Communities);
    }

    [Fact]
    public async Task SaveDocumentIsIdempotentUpsert()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        Neo4jGraphStore store = await ReadyStoreAsync();
        await store.SaveDocumentAsync(SampleDocument(), default);
        GraphDocument updated = SampleDocument() with { Chunks = [new("c1", "doc-1", "test://doc-1", "Updated text.", [1, 0])] };
        await store.SaveDocumentAsync(updated, default);
        GraphSnapshot snapshot = await store.LoadAsync(default);
        TextChunk chunk = Assert.Single(snapshot.Chunks);
        Assert.Equal("Updated text.", chunk.Text);
    }

    [Fact]
    public async Task ExpandTraversesRelatedEntitiesAndRespectsHopsAndLimit()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        Neo4jGraphStore store = await ReadyStoreAsync();
        await store.SaveDocumentAsync(SampleDocument(), default);
        Assert.Equal(["c1"], (await store.ExpandAsync(["c1"], 0, 10, default)).Select(c => c.Id));
        Assert.Equal(["c1", "c2"], (await store.ExpandAsync(["c1"], 1, 10, default)).Select(c => c.Id));
        TextChunk limited = Assert.Single(await store.ExpandAsync(["c1"], 1, 1, default));
        Assert.Equal("c1", limited.Id);
        Assert.Equal(["c1", "c2"], (await store.ExpandAsync(["c1"], 3, 10, default)).Select(c => c.Id));
    }

    [Fact]
    public async Task ExpandRejectsOutOfRangeHops()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        Neo4jGraphStore store = await ReadyStoreAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ExpandAsync(["c1"], 4, 10, default));
    }

    [Fact]
    public async Task PartitionRunsGdsLeidenAndCoversAllEntities()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        Neo4jGraphStore store = await ReadyStoreAsync();
        await store.SaveDocumentAsync(SampleDocument(), default);
        CommunityPartition[] partitions = await store.PartitionAsync(new LeidenOptions(), default);
        Assert.NotEmpty(partitions);
        Assert.Equal("Neo4j GDS Leiden", store.Algorithm);
        Assert.Equal(["e1", "e2", "e3"], partitions.SelectMany(p => p.EntityIds).Order(StringComparer.Ordinal));
        Assert.Equal(["c1", "c2"], partitions.SelectMany(p => p.ChunkIds).Distinct().Order(StringComparer.Ordinal));
        // The projected GDS graph must be dropped after partitioning.
        Assert.Empty(await fixture.QueryAsync(Password, "CALL gds.graph.list() YIELD graphName RETURN graphName"));
    }

    [Fact]
    public async Task CommunitiesSaveLoadAndClearOnNextDocumentSave()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        Neo4jGraphStore store = await ReadyStoreAsync();
        await store.SaveDocumentAsync(SampleDocument(), default);
        await store.SaveCommunitiesAsync([new("community-0-1", 0, "Alpha and Beta trade.", ["c1", "c2"])], default);
        GraphSnapshot loaded = await store.LoadAsync(default);
        CommunitySummary summary = Assert.Single(loaded.Communities);
        Assert.Equal("community-0-1", summary.Id);
        Assert.Equal(0, summary.Level);
        Assert.Equal("Alpha and Beta trade.", summary.Summary);
        Assert.Equal(["c1", "c2"], summary.ChunkIds);
        // SaveDocumentAsync deletes stale Community nodes, matching InMemoryGraphStore behavior.
        await store.SaveDocumentAsync(SampleDocument("doc-2"), default);
        GraphSnapshot cleared = await store.LoadAsync(default);
        Assert.Empty(cleared.Communities);
        Assert.Equal(4, cleared.Chunks.Length);
    }

    [Fact]
    public async Task BadCredentialsFailFast()
    {
        if (!await fixture.EnsureAvailableAsync()) return;
        await fixture.ResetAsync(Password);
        Neo4jGraphStore store = new(new HttpClient { BaseAddress = fixture.Endpoint }, fixture.Endpoint, "neo4j", "neo4j", "wrong-password");
        await Assert.ThrowsAsync<HttpRequestException>(() => store.LoadAsync(default));
    }
}

[CollectionDefinition("Neo4j")]
public sealed class Neo4jCollection : ICollectionFixture<Neo4jFixture>;

public sealed class Neo4jFixture : IAsyncLifetime
{
    private IContainer? _container;

    public Uri Endpoint { get; private set; } = new("http://127.0.0.1:7474/");

    // True when Docker is unavailable; tests then report inconclusive instead of failing.
    public bool Unavailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            // Build once from deploy/neo4j (neo4j:5.26.31-community + GDS 2.13.12); cached afterwards.
            ImageFromDockerfileBuilder imageBuilder = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), "deploy/neo4j/Dockerfile")
                .WithDockerfile("deploy/neo4j/Dockerfile")
                .WithName("graphrag-test-neo4j-gds:5.26.31")
                .WithBuildArgument("GDS_VERSION", "2.13.12")
                .WithBuildArgument("GDS_SHA256", "d24af9cfe8890dcb1556766aba6c25ac77515f1205da20baabb6eb40088950f9")
                .WithDeleteIfExists(false);
            await imageBuilder.Build().CreateAsync();
            _container = new ContainerBuilder("graphrag-test-neo4j-gds:5.26.31")
                .WithImagePullPolicy(PullPolicy.Never)
                .WithEnvironment("NEO4J_AUTH", "neo4j/test-password")
                .WithEnvironment("NEO4J_server_memory_heap_initial__size", "256m")
                .WithEnvironment("NEO4J_server_memory_heap_max__size", "512m")
                .WithEnvironment("NEO4J_server_memory_pagecache_size", "128m")
                .WithEnvironment("NEO4J_dbms_security_procedures_unrestricted", "gds.*")
                .WithEnvironment("NEO4J_dbms_security_procedures_allowlist", "gds.*")
                .WithPortBinding(7474, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(7474).ForPath("/"), o => o.WithTimeout(TimeSpan.FromMinutes(3))))
                .Build();
            await _container.StartAsync();
            Endpoint = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(7474)}/");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Unavailable = true;
            Console.Error.WriteLine("Neo4j container unavailable, skipping Neo4j integration tests: " + ex.Message);
        }
    }

    public async Task<bool> EnsureAvailableAsync()
    {
        if (Unavailable || _container is null) return false;
        // The HTTP wait strategy only proves the server is up; wait until the tx endpoint accepts auth.
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await QueryAsync("test-password", "RETURN 1");
                return true;
            }
            catch (HttpRequestException) { await Task.Delay(1000); }
            catch (InvalidOperationException) { await Task.Delay(1000); }
        }
        Unavailable = true; // Container started but never accepted auth; report inconclusive.
        return false;
    }

    public async Task ResetAsync(string password)
    {
        await QueryAsync(password, "MATCH (n) DETACH DELETE n");
        await QueryAsync(password, "CALL gds.graph.list() YIELD graphName CALL gds.graph.drop(graphName, false) YIELD graphName AS dropped RETURN dropped");
    }

    public async Task<JsonElement[]> QueryAsync(string password, string cypher)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(Endpoint, "db/neo4j/tx/commit"));
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("neo4j:" + password)));
        request.Content = new StringContent("{\"statements\":[{\"statement\":" + JsonSerializer.Serialize(cypher) + "}]}", Encoding.UTF8, "application/json");
        using HttpClient http = new();
        using HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement errors = doc.RootElement.GetProperty("errors");
        if (errors.GetArrayLength() > 0) throw new InvalidOperationException("Cypher failed: " + errors[0].GetProperty("code").GetString());
        return doc.RootElement.GetProperty("results")[0].GetProperty("data").EnumerateArray().Select(d => d.Clone()).ToArray();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

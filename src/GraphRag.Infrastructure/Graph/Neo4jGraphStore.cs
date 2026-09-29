using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GraphRag.Core;

namespace GraphRag.Infrastructure.Graph;

// Transactional HTTP avoids a reflection-heavy transport inside the Native AOT service.
public sealed class Neo4jGraphStore : IGraphStore
{
    private readonly HttpClient _http;
    private readonly Uri _commit;
    private readonly AuthenticationHeaderValue _auth;
    public string Algorithm => "Neo4j GDS Leiden";

    public Neo4jGraphStore(HttpClient http, Uri endpoint, string database, string user, string password)
    {
        _http = http;
        _commit = new Uri(endpoint, $"db/{Uri.EscapeDataString(database)}/tx/commit");
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync([
            Statement("CREATE CONSTRAINT rag_doc IF NOT EXISTS FOR (n:Document) REQUIRE n.id IS UNIQUE"),
            Statement("CREATE CONSTRAINT rag_chunk IF NOT EXISTS FOR (n:Chunk) REQUIRE n.id IS UNIQUE"),
            Statement("CREATE CONSTRAINT rag_entity IF NOT EXISTS FOR (n:Entity) REQUIRE n.id IS UNIQUE"),
            Statement("CREATE CONSTRAINT rag_community IF NOT EXISTS FOR (n:Community) REQUIRE n.id IS UNIQUE"),
            Statement("CREATE CONSTRAINT rag_metadata IF NOT EXISTS FOR (n:RagMetadata) REQUIRE n.id IS UNIQUE")], cancellationToken);
        await ExecuteAsync([Statement("RETURN gds.version() AS version")], cancellationToken);
    }

    public async Task<GraphSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        JsonElement[] result = await ExecuteAsync([
            Statement("MATCH (c:Chunk) RETURN c.id,c.documentId,c.source,c.text,c.vector ORDER BY c.id"),
            Statement("MATCH (c:Community) RETURN c.id,c.level,c.summary,c.chunkIds ORDER BY c.level,c.id"),
            Statement("MATCH (m:RagMetadata {id:'embedding-space'}) RETURN m.value")], cancellationToken);
        TextChunk[] chunks = Rows(result[0]).Select(ChunkFromRow).ToArray();
        CommunitySummary[] summaries = Rows(result[1]).Select(r => new CommunitySummary(r[0].GetString()!, r[1].GetInt32(), r[2].GetString()!,
            r[3].EnumerateArray().Select(x => x.GetString()!).ToArray())).ToArray();
        string? space = Rows(result[2]).FirstOrDefault() is { ValueKind: JsonValueKind.Array } row ? row[0].GetString() : null;
        return new GraphSnapshot(chunks, summaries, space);
    }

    public async Task SaveDocumentAsync(GraphDocument document, CancellationToken cancellationToken)
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(new Neo4jDocumentParameters(document.Id, document.Source, document.EmbeddingSpace,
            document.Chunks, document.Entities, document.Relations), RagJsonContext.Default.Neo4jDocumentParameters);
        await ExecuteAsync([
            Statement("MERGE (m:RagMetadata {id:'embedding-space'}) ON CREATE SET m.value=$embeddingSpace", parameters),
            Statement("MERGE (d:Document {id:$id}) SET d.source=$source,d.embeddingSpace=$embeddingSpace", parameters),
            Statement("MATCH (d:Document {id:$id}) UNWIND $chunks AS chunk MERGE (c:Chunk {id:chunk.id}) SET c.documentId=chunk.documentId,c.source=chunk.source,c.text=chunk.text,c.vector=chunk.vector MERGE (d)-[:HAS_CHUNK]->(c)", parameters),
            Statement("UNWIND $entities AS entity MERGE (e:Entity {id:entity.id}) SET e.name=entity.name,e.type=entity.type,e.description=entity.description WITH e,entity UNWIND entity.chunkIds AS chunkId MATCH (c:Chunk {id:chunkId}) MERGE (c)-[:MENTIONS]->(e)", parameters),
            Statement("UNWIND $relations AS relation MATCH (a:Entity {id:relation.sourceId}),(b:Entity {id:relation.targetId}) MERGE (a)-[r:RELATED {chunkId:relation.chunkId,description:relation.description}]->(b) SET r.weight=relation.weight", parameters),
            Statement("MATCH (c:Community) DETACH DELETE c")], cancellationToken);
    }

    public async Task<TextChunk[]> ExpandAsync(string[] chunkIds, int hops, int limit, CancellationToken cancellationToken)
    {
        if (hops is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(hops));
        JsonElement parameters = JsonSerializer.SerializeToElement(new Neo4jIdsParameters(chunkIds, limit), RagJsonContext.Default.Neo4jIdsParameters);
        // Variable path length is an integer validated above; all corpus strings remain parameters.
        string query = $"MATCH (c:Chunk)-[:MENTIONS]->(e:Entity) WHERE c.id IN $ids MATCH (e)-[:RELATED*0..{hops}]-(neighbor:Entity)<-[:MENTIONS]-(n:Chunk) RETURN DISTINCT n.id,n.documentId,n.source,n.text,n.vector ORDER BY n.id LIMIT $limit";
        JsonElement[] result = await ExecuteAsync([Statement(query, parameters)], cancellationToken);
        return Rows(result[0]).Select(ChunkFromRow).ToArray();
    }

    public async Task<CommunityPartition[]> PartitionAsync(LeidenOptions options, CancellationToken cancellationToken)
    {
        JsonElement[] count = await ExecuteAsync([Statement("MATCH (e:Entity) RETURN count(e)")], cancellationToken);
        if (Rows(count[0]).First()[0].GetInt64() == 0) return [];
        string graphName = "rag-" + Guid.NewGuid().ToString("N");
        JsonElement parameters = JsonSerializer.SerializeToElement(new Neo4jGraphParameters(graphName, options.Gamma, options.Theta, options.MaxLevels, options.RandomSeed), RagJsonContext.Default.Neo4jGraphParameters);
        await ExecuteAsync([Statement("CALL gds.graph.project($name,'Entity',{RELATED:{orientation:'UNDIRECTED',properties:'weight'}}) YIELD graphName RETURN graphName", parameters)], cancellationToken);
        try
        {
            JsonElement[] result = await ExecuteAsync([
                // Neo4j's HTTP JSON integers can be Java Integer; GDS requires a Long seed.
                Statement("CALL gds.leiden.stream($name,{gamma:toFloat($gamma),theta:toFloat($theta),maxLevels:toInteger($maxLevels),randomSeed:toInteger(toString($randomSeed)),concurrency:1,relationshipWeightProperty:'weight',includeIntermediateCommunities:true}) YIELD nodeId,communityId,intermediateCommunityIds RETURN gds.util.asNode(nodeId).id,communityId,intermediateCommunityIds", parameters),
                Statement("MATCH (c:Chunk)-[:MENTIONS]->(e:Entity) RETURN e.id,collect(DISTINCT c.id)")], cancellationToken);
            Dictionary<string, string[]> mentions = Rows(result[1]).ToDictionary(r => r[0].GetString()!, r => r[1].EnumerateArray().Select(x => x.GetString()!).ToArray());
            Dictionary<(int Level, long Community), List<string>> groups = [];
            foreach (JsonElement row in Rows(result[0]))
            {
                long[] levels = row[2].ValueKind == JsonValueKind.Array ? row[2].EnumerateArray().Select(x => x.GetInt64()).ToArray() : [row[1].GetInt64()];
                if (levels.Length == 0) levels = [row[1].GetInt64()];
                for (int level = 0; level < levels.Length; level++)
                {
                    var key = (level, levels[level]);
                    if (!groups.TryGetValue(key, out List<string>? members)) groups[key] = members = [];
                    members.Add(row[0].GetString()!);
                }
            }
            return groups.OrderBy(g => g.Key.Level).ThenBy(g => g.Key.Community).Select(g => new CommunityPartition(
                $"community-{g.Key.Level}-{g.Key.Community}", g.Key.Level, g.Value.ToArray(),
                g.Value.Where(mentions.ContainsKey).SelectMany(id => mentions[id]).Distinct().Order(StringComparer.Ordinal).ToArray())).ToArray();
        }
        finally
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            await ExecuteAsync([Statement("CALL gds.graph.drop($name,false) YIELD graphName RETURN graphName", parameters)], cleanup.Token);
        }
    }

    public async Task SaveCommunitiesAsync(CommunitySummary[] summaries, CancellationToken cancellationToken)
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(new Neo4jCommunityParameters(summaries), RagJsonContext.Default.Neo4jCommunityParameters);
        await ExecuteAsync([Statement("MATCH (c:Community) DETACH DELETE c"),
            Statement("UNWIND $summaries AS summary CREATE (c:Community {id:summary.id,level:summary.level,summary:summary.summary,chunkIds:summary.chunkIds}) WITH c,summary UNWIND summary.chunkIds AS chunkId MATCH (chunk:Chunk {id:chunkId}) MERGE (c)-[:SUMMARIZES]->(chunk)", parameters)], cancellationToken);
    }

    private static JsonElement Empty => JsonSerializer.SerializeToElement(new Neo4jIdsParameters([], 0), RagJsonContext.Default.Neo4jIdsParameters);
    private static Neo4jStatement Statement(string query, JsonElement? parameters = null) => new(query, parameters ?? Empty);
    private static IEnumerable<JsonElement> Rows(JsonElement result) => result.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("row"));
    private static TextChunk ChunkFromRow(JsonElement row) => new(row[0].GetString()!, row[1].GetString()!, row[2].GetString()!, row[3].GetString()!,
        row[4].EnumerateArray().Select(v => v.GetSingle()).ToArray());

    private async Task<JsonElement[]> ExecuteAsync(Neo4jStatement[] statements, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _commit);
        request.Headers.Authorization = _auth;
        request.Content = new StringContent(JsonSerializer.Serialize(new Neo4jRequest(statements), RagJsonContext.Default.Neo4jRequest), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement errors = doc.RootElement.GetProperty("errors");
        if (errors.GetArrayLength() > 0) throw new InvalidOperationException("Neo4j transaction failed: " + errors[0].GetProperty("code").GetString());
        return doc.RootElement.GetProperty("results").EnumerateArray().Select(r => r.Clone()).ToArray();
    }
}

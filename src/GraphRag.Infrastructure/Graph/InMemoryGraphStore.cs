using GraphRag.Core;

namespace GraphRag.Infrastructure.Graph;

public sealed class InMemoryGraphStore : IGraphStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GraphDocument> _documents = new(StringComparer.Ordinal);
    private CommunitySummary[] _communities = [];
    public string Algorithm => "fixture-connected-components (not Leiden)";
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<GraphSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(new GraphSnapshot(_documents.Values.SelectMany(d => d.Chunks).ToArray(), _communities,
            _documents.Values.FirstOrDefault()?.EmbeddingSpace));
    }

    public Task SaveDocumentAsync(GraphDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_documents.Values.Any(d => d.EmbeddingSpace != document.EmbeddingSpace)) throw new InvalidOperationException("Embedding space mismatch.");
            _documents[document.Id] = document;
            _communities = [];
        }
        return Task.CompletedTask;
    }

    public Task<TextChunk[]> ExpandAsync(string[] chunkIds, int hops, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            GraphEntity[] entities = _documents.Values.SelectMany(d => d.Entities).ToArray();
            GraphRelation[] edges = _documents.Values.SelectMany(d => d.Relations).ToArray();
            HashSet<string> reached = entities.Where(e => e.ChunkIds.Intersect(chunkIds).Any()).Select(e => e.Id).ToHashSet();
            for (int hop = 0; hop < hops; hop++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (GraphRelation edge in edges.Where(e => reached.Contains(e.SourceId) || reached.Contains(e.TargetId)).ToArray())
                { reached.Add(edge.SourceId); reached.Add(edge.TargetId); }
            }
            HashSet<string> ids = entities.Where(e => reached.Contains(e.Id)).SelectMany(e => e.ChunkIds).ToHashSet();
            return Task.FromResult(_documents.Values.SelectMany(d => d.Chunks).Where(c => ids.Contains(c.Id)).OrderBy(c => c.Id).Take(limit).ToArray());
        }
    }

    public Task<CommunityPartition[]> PartitionAsync(LeidenOptions options, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            GraphEntity[] entities = _documents.Values.SelectMany(d => d.Entities).ToArray();
            GraphRelation[] edges = _documents.Values.SelectMany(d => d.Relations).ToArray();
            HashSet<string> remaining = entities.Select(e => e.Id).ToHashSet();
            List<CommunityPartition> result = [];
            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HashSet<string> group = [remaining.Order(StringComparer.Ordinal).First()];
                bool changed;
                do
                {
                    int count = group.Count;
                    foreach (GraphRelation edge in edges)
                        if (group.Contains(edge.SourceId) || group.Contains(edge.TargetId)) { group.Add(edge.SourceId); group.Add(edge.TargetId); }
                    changed = count != group.Count;
                } while (changed);
                remaining.ExceptWith(group);
                result.Add(new CommunityPartition("fixture-community-" + result.Count, 0, group.ToArray(),
                    entities.Where(e => group.Contains(e.Id)).SelectMany(e => e.ChunkIds).Distinct().ToArray()));
            }
            return Task.FromResult(result.ToArray());
        }
    }

    public Task SaveCommunitiesAsync(CommunitySummary[] summaries, CancellationToken cancellationToken)
    { lock (_gate) _communities = summaries; return Task.CompletedTask; }
}

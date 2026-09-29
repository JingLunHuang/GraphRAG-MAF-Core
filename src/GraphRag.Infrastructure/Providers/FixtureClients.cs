using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphRag.Core;
using Microsoft.Extensions.AI;

namespace GraphRag.Infrastructure.Providers;

// Explicit deterministic fixtures for offline integration testing, not a language model.
public sealed partial class FixtureChatClient : IChatClient
{
    [GeneratedRegex(@"\b[A-Z][a-zA-Z0-9]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Names();
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fixture", null, "deterministic-fixture-v1") :
        serviceType.IsInstanceOfType(this) ? this : null;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChatMessage[] list = messages.ToArray();
        string instruction = options?.Instructions ?? list.LastOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
        string user = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        string output;
        if (instruction.Contains("ROLE:EXTRACTOR", StringComparison.Ordinal))
        {
            string[] names = Names().Matches(user).Select(m => m.Value).Distinct().Take(12).ToArray();
            output = JsonSerializer.Serialize(new Extraction(names.Select(n => new ExtractedEntity(n, "fixture-entity", n)).ToArray(),
                names.Zip(names.Skip(1), (a, b) => new ExtractedRelation(a, b, "Fixture co-occurrence")).ToArray()), RagJsonContext.Default.Extraction);
        }
        else if (instruction.Contains("ROLE:SUMMARIZER", StringComparison.Ordinal))
        {
            Evidence[] items = JsonSerializer.Deserialize(user, RagJsonContext.Default.EvidenceArray) ?? [];
            output = string.Join("\n", items.Select(e => $"[{e.Id}] {e.Text}"));
        }
        else if (instruction.Contains("ROLE:JUDGE", StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture chat client cannot provide LLM evaluation scores. Configure a real judge model.");
        else
        {
            QueryPayload? payload = null;
            foreach (ChatMessage message in list.Where(m => m.Role != ChatRole.System))
            {
                try
                {
                    QueryPayload? candidate = JsonSerializer.Deserialize(ModelJson.Extract(message.Text), RagJsonContext.Default.QueryPayload);
                    if (candidate?.Evidence is not null) payload = candidate;
                }
                catch (JsonException) { }
            }
            if (payload is null) throw new JsonException("Missing fixture query payload.");
            if (instruction.Contains("ROLE:CRITIC", StringComparison.Ordinal))
            {
                Draft? draft = null;
                foreach (ChatMessage message in list.Where(m => m.Role != ChatRole.System))
                {
                    try
                    {
                        Draft? candidate = JsonSerializer.Deserialize(ModelJson.Extract(message.Text), RagJsonContext.Default.Draft);
                        if (candidate?.Citations is not null) draft = candidate;
                    }
                    catch (JsonException) { }
                }
                bool supported = draft is not null && draft.Citations.Length > 0 && draft.Citations.All(id => payload.Evidence.Any(e => e.Id == id)) &&
                    payload.Evidence.Any(e => draft.Answer.Contains(e.Text, StringComparison.Ordinal));
                output = JsonSerializer.Serialize(new ReflectionDecision("[Retrieval]", "[Relevant]", supported ? "[Fully supported]" : "[No support]",
                    supported ? 4 : 1, "Deterministic fixture support check."), RagJsonContext.Default.ReflectionDecision);
            }
            else if (instruction.Contains("ROLE:RESEARCHER", StringComparison.Ordinal))
                output = "Evidence inventory: " + string.Join(",", payload.Evidence.Select(e => e.Id));
            else
            {
                Evidence[] chosen = payload.Evidence.Take(3).ToArray();
                output = JsonSerializer.Serialize(new Draft(string.Join("\n", chosen.Select(e => e.Text)), chosen.Select(e => e.Id).ToArray()), RagJsonContext.Default.Draft);
            }
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, output)) { ModelId = "deterministic-fixture-v1" });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (string chunk in TextProcessing.Chunk(response.Text, 80, 0))
        { cancellationToken.ThrowIfCancellationRequested(); yield return new ChatResponseUpdate(ChatRole.Assistant, chunk); }
    }
}

public sealed partial class FixtureEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    [GeneratedRegex(@"[A-Za-z0-9]+|[\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fixture", null, "hash-128-v1", 128) :
        serviceType.IsInstanceOfType(this) ? this : null;
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> result = new();
        foreach (string value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] vector = new float[128];
            foreach (Match match in Tokens().Matches(value))
            {
                uint hash = 2166136261;
                foreach (byte item in Encoding.UTF8.GetBytes(match.Value.ToLowerInvariant())) hash = unchecked((hash ^ item) * 16777619);
                vector[hash % 128]++;
            }
            if (vector.All(x => x == 0)) vector[0] = 1;
            result.Add(new Embedding<float>(vector) { ModelId = "hash-128-v1" });
        }
        return Task.FromResult(result);
    }
}

using System.Text.Json;
using GraphRag.Core;
using GraphRag.Infrastructure;
using GraphRag.Infrastructure.Evaluation;
using GraphRag.Infrastructure.Hosting;
using GraphRag.Infrastructure.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;

Dictionary<string, string> flags = new(StringComparer.Ordinal);
string command = args.FirstOrDefault() ?? "help";
try
{
    for (int i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected --option: " + args[i]);
        string name = args[i][2..];
        if (name == "fixture") flags[name] = "true";
        else if (++i < args.Length) flags[name] = args[i];
        else throw new ArgumentException("Missing option value: " + name);
    }
    if (command == "help")
    {
        Console.WriteLine("GraphRAG-MAF-Core\n  smoke --output artifacts/smoke.json\n  benchmark --iterations 200000 --output artifacts/vector-benchmark.json\n  ingest --corpus data/demo-corpus.json\n  communities --gamma 1 --theta 0.01\n  query --question \"...\" --mode local|global|naive [--corpus ...] [--fixture]\n  evaluate --dataset data/evaluation.json --corpus data/demo-corpus.json --output artifacts/evaluation.json\n  mcp [--fixture]\n  --fixture selects deterministic testing, not LLM quality evaluation.");
        return 0;
    }
    if (command == "benchmark")
    {
        BenchmarkReport report = VectorBenchmark.Run(flags.TryGetValue("iterations", out string? iterations) ? int.Parse(iterations) : 200_000);
        await OutputAsync(JsonSerializer.Serialize(report, RagJsonContext.Default.BenchmarkReport)); return 0;
    }
    if (command == "onnx-check")
    {
        using var generator = new GraphRag.Infrastructure.Providers.OnnxEmbeddingGenerator(
            RuntimeSettings.Env("ONNX_EMBEDDING_PATH", "models/embedding/model.onnx"), RuntimeSettings.Env("ONNX_VOCAB_PATH", "models/embedding/vocab.txt"),
            RuntimeSettings.Env("ONNX_EMBEDDING_MODEL_ID", "all-MiniLM-L6-v2"));
        var vectors = await generator.GenerateAsync(["Northwind builds enterprise analytics.", "Northwind sells enterprise analytics.", "Bananas are yellow fruit."]);
        float same = VectorIndex.Cosine(vectors[0].Vector.Span, vectors[1].Vector.Span), other = VectorIndex.Cosine(vectors[0].Vector.Span, vectors[2].Vector.Span);
        if (same <= other) throw new InvalidOperationException("ONNX embedding semantic smoke failed.");
        await OutputAsync(JsonSerializer.Serialize(new OnnxProbeResult("real-onnx-int8-inference-smoke", "all-MiniLM-L6-v2", vectors[0].Vector.Length, same, other, "passed"), RagJsonContext.Default.OnnxProbeResult));
        return 0;
    }
    if (command == "smoke" || flags.ContainsKey("fixture"))
    {
        Environment.SetEnvironmentVariable("AI_PROVIDER", "fixture"); Environment.SetEnvironmentVariable("EMBEDDING_PROVIDER", "fixture");
        Environment.SetEnvironmentVariable("GRAPH_STORE", "memory"); Environment.SetEnvironmentVariable("JUDGE_PROVIDER", "fixture");
    }
    if (command is not ("smoke" or "ingest" or "communities" or "query" or "evaluate" or "mcp")) throw new ArgumentException("Unknown command: " + command);
    HostApplicationBuilder builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddGraphRag();
    if (command == "mcp") builder.Services.AddMcpServer().WithStdioServerTransport().WithRagTools();
    using IHost host = builder.Build();
    if (command == "mcp") { await host.RunAsync(); return 0; }
    await host.StartAsync();
    try
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        GraphRagService service = host.Services.GetRequiredService<GraphRagService>();
        while (!service.Ready) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(50, timeout.Token); }
        IngestionQueue queue = host.Services.GetRequiredService<IngestionQueue>();
        string? corpusPath = flags.TryGetValue("corpus", out string? configuredCorpus) ? configuredCorpus : command == "smoke" ? "data/demo-corpus.json" : null;
        List<IngestResult> ingested = [];
        if (corpusPath is not null)
        {
            DocumentInput[] corpus = JsonSerializer.Deserialize(await File.ReadAllTextAsync(corpusPath, timeout.Token), RagJsonContext.Default.DocumentInputArray) ?? throw new JsonException("Missing corpus.");
            foreach (DocumentInput document in corpus) ingested.Add(await queue.EnqueueAsync(document, timeout.Token));
        }
        LeidenOptions options = new(Number("gamma", 1), Number("theta", 0.01), (int)Number("max-levels", 10), (int)Number("seed", 19));
        string json;
        switch (command)
        {
            case "smoke":
                CommunityBuildResult communities = await service.BuildCommunitiesAsync(options, timeout.Token);
                const string question = "How do Northwind and Contoso depend on Orion and Helios?";
                AnswerResult local = await service.QueryAsync(new(question), timeout.Token);
                AnswerResult global = await service.QueryAsync(new(question, "global"), timeout.Token);
                AnswerResult naive = await service.QueryAsync(new(question, "naive"), timeout.Token);
                if (local.Citations.Length == 0 || global.Citations.Length == 0 || !local.Reflection.Accepted || !global.Reflection.Accepted)
                    throw new InvalidOperationException("Smoke test failed citation or critic checks.");
                json = JsonSerializer.Serialize(new SmokeResult("passed-fixture-workflow", ingested.ToArray(), communities, local, global, naive), RagJsonContext.Default.SmokeResult); break;
            case "ingest":
                if (corpusPath is null) throw new ArgumentException("ingest requires --corpus.");
                json = JsonSerializer.Serialize(new CorpusInput([]), RagJsonContext.Default.CorpusInput);
                Console.WriteLine($"Ingested {ingested.Count} documents; {service.ChunkCount} chunks."); return 0;
            case "communities":
                json = JsonSerializer.Serialize(await service.BuildCommunitiesAsync(options, timeout.Token), RagJsonContext.Default.CommunityBuildResult); break;
            case "query":
                string mode = flags.GetValueOrDefault("mode", "local");
                if (mode == "global" && corpusPath is not null) await service.BuildCommunitiesAsync(options, timeout.Token);
                if (!flags.TryGetValue("question", out string? query)) throw new ArgumentException("query requires --question.");
                json = JsonSerializer.Serialize(await service.QueryAsync(new QueryRequest(query, mode, (int)Number("top-k", 5), (int)Number("hops", 2)), timeout.Token), RagJsonContext.Default.AnswerResult); break;
            case "evaluate":
                if (!flags.TryGetValue("dataset", out string? datasetPath)) throw new ArgumentException("evaluate requires --dataset.");
                string dataset = await File.ReadAllTextAsync(datasetPath, timeout.Token);
                EvaluationCase[] cases = JsonSerializer.Deserialize(dataset, RagJsonContext.Default.EvaluationCaseArray) ?? throw new JsonException("Missing evaluation cases.");
                if (cases.Any(c => c.GraphMode == "global")) await service.BuildCommunitiesAsync(options, timeout.Token);
                EvaluationReport report = await host.Services.GetRequiredService<RagasEvaluator>().EvaluateAsync(service, cases, dataset, timeout.Token);
                json = JsonSerializer.Serialize(report, RagJsonContext.Default.EvaluationReport); break;
            default: throw new ArgumentException("Unknown command.");
        }
        await OutputAsync(json);
    }
    finally { await host.StopAsync(); }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is HttpRequestException ? "An upstream request failed; check service configuration and availability." : error.ToString());
    return 1;
}

double Number(string name, double fallback) => flags.TryGetValue(name, out string? value) ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
async Task OutputAsync(string json)
{
    if (flags.TryGetValue("output", out string? output))
    { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); await File.WriteAllTextAsync(output, json); }
    Console.WriteLine(json);
}

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace GraphRag.Infrastructure.Providers;

public sealed class OnnxChatClient : IChatClient
{
    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _modelId;
    private readonly string _template;
    private readonly int _contextLength;

    public OnnxChatClient(string modelDirectory, string modelId, string template, int contextLength = 8192)
    {
        if (!File.Exists(Path.Combine(modelDirectory, "genai_config.json")))
            throw new FileNotFoundException("ONNX chat requires an ORT GenAI model directory containing genai_config.json. GGUF must run through Ollama.");
        if (template is not ("llama3" or "phi3" or "tokenizer")) throw new ArgumentException("ONNX template must be llama3, phi3, or tokenizer.");
        _model = new Model(modelDirectory);
        _tokenizer = new Tokenizer(_model);
        _modelId = modelId; _template = template; _contextLength = contextLength;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("onnx", null, _modelId) : serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { _tokenizer.Dispose(); _model.Dispose(); _gate.Dispose(); }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        StringBuilder content = new(); UsageDetails? usage = null;
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            content.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content.ToString())) { ModelId = _modelId, Usage = usage };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            WireMessage[] wire = RestProtocol.Messages(messages, options);
            string prompt = _template switch
            {
                "llama3" => "<|begin_of_text|>" + string.Concat(wire.Select(m => $"<|start_header_id|>{m.Role}<|end_header_id|>\n\n{m.Content}<|eot_id|>")) + "<|start_header_id|>assistant<|end_header_id|>\n\n",
                "phi3" => string.Concat(wire.Select(m => $"<|{m.Role}|>\n{m.Content}<|end|>\n")) + "<|assistant|>\n",
                _ => _tokenizer.ApplyChatTemplate("", JsonSerializer.Serialize(wire, RagJsonContext.Default.WireMessageArray), "", true)
            };
            using Sequences tokens = _tokenizer.Encode(prompt);
            int inputTokens = tokens[0].Length;
            int maxTokens = Math.Min(options?.MaxOutputTokens ?? 2048, _contextLength - inputTokens);
            if (maxTokens <= 0) throw new ArgumentException("Prompt exceeds the configured ONNX model context length.");
            using GeneratorParams parameters = new(_model);
            parameters.SetSearchOption("max_length", inputTokens + maxTokens);
            parameters.SetSearchOption("do_sample", options?.Temperature > 0);
            if (options?.Temperature > 0) parameters.SetSearchOption("temperature", options.Temperature.Value);
            using Generator generator = new(_model, parameters);
            using TokenizerStream stream = _tokenizer.CreateStream();
            generator.AppendTokenSequences(tokens);
            int outputTokens = 0;
            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Native inference is CPU-bound. Yield between tokens so callers can observe cancellation/backpressure.
                generator.GenerateNextToken(); outputTokens++;
                string text = stream.Decode(generator.GetSequence(0)[^1]);
                yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ModelId = _modelId };
                await Task.Yield();
            }
            ChatResponseUpdate usage = new(ChatRole.Assistant, "") { ModelId = _modelId };
            usage.Contents.Add(new UsageContent(new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens, TotalTokenCount = inputTokens + outputTokens }));
            yield return usage;
        }
        finally { _gate.Release(); }
    }
}

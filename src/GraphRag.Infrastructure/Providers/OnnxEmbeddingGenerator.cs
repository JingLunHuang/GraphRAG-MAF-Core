using GraphRag.Core;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GraphRag.Infrastructure.Providers;

public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly InferenceSession _session;
    private readonly BertWordPieceTokenizer _tokenizer;
    private readonly string _modelId;
    private readonly string _outputName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OnnxEmbeddingGenerator(string modelPath, string vocabularyPath, string modelId, string outputName = "last_hidden_state", int maxTokens = 256)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("ONNX embedding model is missing.", modelPath);
        _session = new InferenceSession(modelPath);
        _tokenizer = new BertWordPieceTokenizer(vocabularyPath, maxTokens);
        _modelId = modelId; _outputName = outputName;
        if (!_session.InputMetadata.ContainsKey("input_ids") || !_session.InputMetadata.ContainsKey("attention_mask") ||
            !_session.OutputMetadata.ContainsKey(outputName)) throw new ArgumentException("ONNX model does not match the BERT encoder input/output contract.");
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null :
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("onnx", null, _modelId) : serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { _session.Dispose(); _gate.Dispose(); }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> generated = new();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (string value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long[] ids = _tokenizer.Encode(value);
                int count = ids.Length;
                List<NamedOnnxValue> input = [NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, count])),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, count).ToArray(), [1, count]))];
                if (_session.InputMetadata.ContainsKey("token_type_ids")) input.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[count], [1, count])));
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = _session.Run(input, [_outputName]);
                Tensor<float> tensor = result.First().AsTensor<float>();
                if (tensor.Dimensions.Length != 3 || tensor.Dimensions[0] != 1 || tensor.Dimensions[1] != count)
                    throw new InvalidOperationException("Embedding output must be [1, tokens, hidden_dimension].");
                int dimensions = tensor.Dimensions[2];
                float[] pooled = new float[dimensions];
                for (int token = 0; token < count; token++)
                    for (int dimension = 0; dimension < dimensions; dimension++) pooled[dimension] += tensor[0, token, dimension] / count;
                VectorIndex.Validate(pooled);
                double norm = Math.Sqrt(pooled.Sum(x => (double)x * x));
                for (int i = 0; i < pooled.Length; i++) pooled[i] /= (float)norm;
                generated.Add(new Embedding<float>(pooled) { ModelId = _modelId });
            }
        }
        finally { _gate.Release(); }
        return generated;
    }
}

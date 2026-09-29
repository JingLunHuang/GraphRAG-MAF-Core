using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using GraphRag.Core;

namespace GraphRag.Infrastructure.Evaluation;

public sealed record VectorMeasurement(int Dimensions, int Iterations, double ScalarNs, double TensorNs, double Speedup, long TensorAllocatedBytes, float AbsoluteError);
public sealed record BenchmarkReport(string ArtifactType, DateTimeOffset CreatedUtc, string Os, string Runtime, int ProcessorCount,
    bool VectorHardwareAccelerated, string BuildConfiguration, VectorMeasurement[] Measurements);

public static class VectorBenchmark
{
    private static float _sink;
    public static BenchmarkReport Run(int iterations = 200_000)
    {
        if (iterations < 1000) throw new ArgumentOutOfRangeException(nameof(iterations));
        List<VectorMeasurement> measurements = [];
        foreach (int dimensions in new[] { 128, 384, 768, 1536 })
        {
            Random random = new(42 + dimensions);
            float[] left = Enumerable.Range(0, dimensions).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
            float[] right = Enumerable.Range(0, dimensions).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
            for (int i = 0; i < 10_000; i++) { _sink = Scalar(left, right); _sink = VectorIndex.Cosine(left, right); }
            List<double> scalarTimes = [], tensorTimes = [];
            long allocated = 0;
            for (int round = 0; round < 5; round++)
            {
                // Alternate order to reduce thermal/order bias; median of five rounds.
                if (round % 2 == 0) { scalarTimes.Add(ScalarTime()); tensorTimes.Add(TensorTime()); }
                else { tensorTimes.Add(TensorTime()); scalarTimes.Add(ScalarTime()); }
            }
            double scalar = scalarTimes.Order().ElementAt(2), tensor = tensorTimes.Order().ElementAt(2);
            measurements.Add(new VectorMeasurement(dimensions, iterations, scalar, tensor, scalar / tensor, allocated,
                Math.Abs(Scalar(left, right) - VectorIndex.Cosine(left, right))));

            double ScalarTime()
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) _sink = Scalar(left, right);
                return Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            }
            double TensorTime()
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) _sink = VectorIndex.Cosine(left, right);
                long finish = Stopwatch.GetTimestamp();
                allocated = Math.Max(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
                return (double)(finish - start) / Stopwatch.Frequency * 1e9 / iterations;
            }
        }
        return new BenchmarkReport("measured-vector-kernel-benchmark", DateTimeOffset.UtcNow, RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription, Environment.ProcessorCount, Vector.IsHardwareAccelerated,
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            measurements.ToArray());
    }
    public static float Scalar(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        float dot = 0, a = 0, b = 0;
        for (int i = 0; i < left.Length; i++) { dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i]; }
        return dot / MathF.Sqrt(a * b);
    }
}

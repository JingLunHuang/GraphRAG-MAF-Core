using System.Threading.Channels;
using GraphRag.Core;
using Microsoft.Extensions.Hosting;

namespace GraphRag.Infrastructure.Hosting;

public sealed class IngestionQueue(GraphRagService service, RagOptions options) : BackgroundService
{
    private sealed record Job(DocumentInput Document, TaskCompletionSource<IngestResult> Completion, CancellationToken RequestCancellation);
    private readonly Channel<Job> _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(options.QueueCapacity)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });

    public async Task<IngestResult> EnqueueAsync(DocumentInput input, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<IngestResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new Job(input, completion, cancellationToken), cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await service.InitializeAsync(stoppingToken);
        try
        {
            await foreach (Job job in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.RequestCancellation);
                try { job.Completion.TrySetResult(await service.IngestAsync(job.Document, linked.Token)); }
                catch (OperationCanceledException) { job.Completion.TrySetCanceled(linked.Token); }
                catch (Exception error) { job.Completion.TrySetException(error); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out Job? job)) job.Completion.TrySetCanceled(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    { _channel.Writer.TryComplete(); await base.StopAsync(cancellationToken); }
}

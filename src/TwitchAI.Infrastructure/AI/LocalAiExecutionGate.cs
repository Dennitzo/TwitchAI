namespace TwitchAI.Infrastructure.AI;

public interface ILocalAiExecutionGate
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        CancellationToken cancellationToken = default);
}

public sealed class LocalAiExecutionGate : ILocalAiExecutionGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public static LocalAiExecutionGate ProcessWide { get; } = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

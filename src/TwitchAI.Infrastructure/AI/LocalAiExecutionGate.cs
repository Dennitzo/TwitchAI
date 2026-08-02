namespace TwitchAI.Infrastructure.AI;

public enum LocalAiRunPriority
{
    Normal,
    ManualReview,
}

public interface ILocalAiExecutionGate
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        LocalAiRunPriority priority = LocalAiRunPriority.Normal,
        string? schedulingKey = null,
        CancellationToken cancellationToken = default);
}

public sealed class LocalAiExecutionGate : ILocalAiExecutionGate, IDisposable
{
    public const int MaximumConcurrency = 4;

    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _manualReviewWaiters = new();
    private readonly LinkedList<Waiter> _normalWaiters = new();
    private readonly HashSet<string> _activeSchedulingKeys = new(StringComparer.Ordinal);
    private int _activeLeaseCount;
    private bool _disposed;

    public static LocalAiExecutionGate ProcessWide { get; } = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(
        LocalAiRunPriority priority = LocalAiRunPriority.Normal,
        string? schedulingKey = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        schedulingKey = NormalizeSchedulingKey(schedulingKey);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeLeaseCount < MaximumConcurrency
                && CanAcquireImmediately(priority, schedulingKey))
            {
                if (schedulingKey is not null
                    && !_activeSchedulingKeys.Add(schedulingKey))
                {
                    return Enqueue(priority, schedulingKey, cancellationToken);
                }

                _activeLeaseCount++;
                return ValueTask.FromResult<IAsyncDisposable>(
                    new Lease(this, schedulingKey));
            }

            return Enqueue(priority, schedulingKey, cancellationToken);
        }
    }

    public void Dispose()
    {
        Waiter[] waiters;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waiters = [.. _manualReviewWaiters, .. _normalWaiters];
            _manualReviewWaiters.Clear();
            _normalWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Dispose(new ObjectDisposedException(nameof(LocalAiExecutionGate)));
        }
    }

    private LinkedList<Waiter> QueueFor(LocalAiRunPriority priority) =>
        priority == LocalAiRunPriority.ManualReview
            ? _manualReviewWaiters
            : _normalWaiters;

    private bool CanAcquireImmediately(
        LocalAiRunPriority priority,
        string? schedulingKey)
    {
        if (schedulingKey is not null
            && _activeSchedulingKeys.Contains(schedulingKey))
        {
            return false;
        }

        if (HasEligibleWaiter(_manualReviewWaiters))
        {
            return false;
        }

        return priority == LocalAiRunPriority.ManualReview
            || !HasEligibleWaiter(_normalWaiters);
    }

    private bool HasEligibleWaiter(LinkedList<Waiter> queue)
    {
        for (var node = queue.First; node is not null; node = node.Next)
        {
            var waiter = node.Value;
            if (!waiter.IsCanceled
                && (waiter.SchedulingKey is null
                    || !_activeSchedulingKeys.Contains(waiter.SchedulingKey)))
            {
                return true;
            }
        }

        return false;
    }

    private ValueTask<IAsyncDisposable> Enqueue(
        LocalAiRunPriority priority,
        string? schedulingKey,
        CancellationToken cancellationToken)
    {
        var waiter = new Waiter(schedulingKey, cancellationToken);
        QueueFor(priority).AddLast(waiter);
        waiter.RegisterCancellation();
        return new ValueTask<IAsyncDisposable>(waiter.Task);
    }

    private void Release(string? schedulingKey)
    {
        Waiter? next = null;
        lock (_sync)
        {
            if (schedulingKey is not null)
            {
                _activeSchedulingKeys.Remove(schedulingKey);
            }

            if (_disposed)
            {
                _activeLeaseCount--;
                return;
            }

            next = DequeueEligible(_manualReviewWaiters)
                ?? DequeueEligible(_normalWaiters);
            if (next is null)
            {
                _activeLeaseCount--;
            }
            else if (next.SchedulingKey is not null)
            {
                _activeSchedulingKeys.Add(next.SchedulingKey);
            }
        }

        next?.Grant(new Lease(this, next.SchedulingKey));
    }

    private Waiter? DequeueEligible(LinkedList<Waiter> queue)
    {
        var node = queue.First;
        while (node is not null)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (waiter.IsCanceled)
            {
                queue.Remove(node);
            }
            else if (waiter.SchedulingKey is null
                || !_activeSchedulingKeys.Contains(waiter.SchedulingKey))
            {
                queue.Remove(node);
                return waiter;
            }

            node = next;
        }

        return null;
    }

    private static string? NormalizeSchedulingKey(string? schedulingKey) =>
        string.IsNullOrWhiteSpace(schedulingKey)
            ? null
            : schedulingKey.Trim();

    private sealed class Lease(
        LocalAiExecutionGate owner,
        string? schedulingKey) : IAsyncDisposable
    {
        private LocalAiExecutionGate? _owner = owner;

        public ValueTask DisposeAsync()
        {
            ReleaseNow();
            return ValueTask.CompletedTask;
        }

        public void ReleaseNow() =>
            Interlocked.Exchange(ref _owner, null)?.Release(schedulingKey);
    }

    private sealed class Waiter(
        string? schedulingKey,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<IAsyncDisposable> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _completed;

        public Task<IAsyncDisposable> Task => _source.Task;

        public string? SchedulingKey { get; } = schedulingKey;

        public bool IsCanceled => Volatile.Read(ref _completed) != 0;

        public void RegisterCancellation()
        {
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(
                    static state => ((Waiter)state!).Cancel(),
                    this);
            }
        }

        public void Grant(Lease lease)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _registration.Dispose();
                _source.TrySetResult(lease);
                return;
            }

            lease.ReleaseNow();
        }

        public void Dispose(Exception exception)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _registration.Dispose();
                _source.TrySetException(exception);
            }
        }

        private void Cancel()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _source.TrySetCanceled(cancellationToken);
            }
        }
    }
}

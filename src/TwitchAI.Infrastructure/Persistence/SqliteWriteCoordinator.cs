namespace TwitchAI.Infrastructure.Persistence;

/// <summary>
/// Serializes SQLite write units across the process while leaving read-only
/// operations unconstrained.
/// </summary>
public interface ISqliteWriteCoordinator
{
    ValueTask<SqliteWriteLease> AcquireAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-wide asynchronous write coordinator. A lease represents one complete
/// write unit, including all statements in a multi-step transaction.
/// </summary>
public sealed class SqliteWriteCoordinator : ISqliteWriteCoordinator, IDisposable
{
    private static readonly AsyncLocal<AcquisitionMarker?> AmbientAcquisition = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public static SqliteWriteCoordinator ProcessWide { get; } = new();

    public ValueTask<SqliteWriteLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        var ambient = AmbientAcquisition.Value;
        if (ambient is not null &&
            ReferenceEquals(ambient.Coordinator, this) &&
            ambient.State is AcquisitionState.Waiting or AcquisitionState.Held)
        {
            throw new InvalidOperationException(
                "A SQLite write lease cannot be acquired recursively. " +
                "Acquire it once at the outer write boundary and pass the active " +
                "connection and transaction to nested helpers.");
        }

        var marker = new AcquisitionMarker(this, ambient);
        AmbientAcquisition.Value = marker;
        var waitTask = _gate.WaitAsync(cancellationToken);
        if (waitTask.IsCompletedSuccessfully)
        {
            marker.State = AcquisitionState.Held;
            return ValueTask.FromResult(new SqliteWriteLease(this, marker));
        }

        return AwaitLeaseAsync(waitTask, marker);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    internal void Release(AcquisitionMarker marker)
    {
        if (!ReferenceEquals(marker.Coordinator, this) ||
            marker.State != AcquisitionState.Held)
        {
            return;
        }

        marker.State = AcquisitionState.Released;
        if (ReferenceEquals(AmbientAcquisition.Value, marker))
        {
            AmbientAcquisition.Value = marker.Parent;
        }

        _gate.Release();
    }

    private async ValueTask<SqliteWriteLease> AwaitLeaseAsync(
        Task waitTask,
        AcquisitionMarker marker)
    {
        try
        {
            await waitTask.ConfigureAwait(false);
            marker.State = AcquisitionState.Held;
            return new SqliteWriteLease(this, marker);
        }
        catch
        {
            marker.State = AcquisitionState.Failed;
            if (ReferenceEquals(AmbientAcquisition.Value, marker))
            {
                AmbientAcquisition.Value = marker.Parent;
            }

            throw;
        }
    }

    internal sealed class AcquisitionMarker(
        SqliteWriteCoordinator coordinator,
        AcquisitionMarker? parent)
    {
        public SqliteWriteCoordinator Coordinator { get; } = coordinator;

        public AcquisitionMarker? Parent { get; } = parent;

        public AcquisitionState State { get; set; } = AcquisitionState.Waiting;
    }

    internal enum AcquisitionState
    {
        Waiting,
        Held,
        Released,
        Failed,
    }
}

public sealed class SqliteWriteLease : IDisposable, IAsyncDisposable
{
    private SqliteWriteCoordinator? _coordinator;
    private SqliteWriteCoordinator.AcquisitionMarker? _marker;

    internal SqliteWriteLease(
        SqliteWriteCoordinator coordinator,
        SqliteWriteCoordinator.AcquisitionMarker marker)
    {
        _coordinator = coordinator;
        _marker = marker;
    }

    public void Dispose()
    {
        var coordinator = Interlocked.Exchange(ref _coordinator, null);
        var marker = Interlocked.Exchange(ref _marker, null);
        if (coordinator is not null && marker is not null)
        {
            coordinator.Release(marker);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

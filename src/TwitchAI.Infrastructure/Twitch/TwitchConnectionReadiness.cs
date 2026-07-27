namespace TwitchAI.Infrastructure.Twitch;

public enum TwitchEventSubConnectionState
{
    Offline,
    Connecting,
    Ready,
    Reconnecting,
    Faulted,
}

public sealed record TwitchConnectionReadinessSnapshot(
    bool IsSessionReady,
    TwitchEventSubConnectionState EventSubState,
    string Status)
{
    public bool IsEventSubReady =>
        EventSubState == TwitchEventSubConnectionState.Ready;

    public bool IsFullyReady => IsSessionReady && IsEventSubReady;
}

public interface ITwitchConnectionReadiness
{
    TwitchConnectionReadinessSnapshot Current { get; }

    bool IsSessionReady { get; }

    bool IsEventSubReady { get; }

    bool IsFullyReady { get; }

    void SetSessionReady(bool isReady, string status);

    void SetEventSubConnecting(bool isReconnect, string status);

    void SetEventSubReady(string status);

    void SetEventSubOffline(string status, bool isFaulted = false);

    Task WaitForEventSubReadyAsync(CancellationToken cancellationToken = default);
}

public sealed class TwitchConnectionReadiness : ITwitchConnectionReadiness
{
    private readonly object _gate = new();
    private TwitchConnectionReadinessSnapshot _current = new(
        IsSessionReady: false,
        TwitchEventSubConnectionState.Offline,
        "Twitch ist nicht verbunden.");
    private TaskCompletionSource _fullyReady = NewReadySource();

    public TwitchConnectionReadinessSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsSessionReady => Current.IsSessionReady;

    public bool IsEventSubReady => Current.IsEventSubReady;

    public bool IsFullyReady => Current.IsFullyReady;

    public void SetSessionReady(bool isReady, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        lock (_gate)
        {
            _current = isReady
                ? _current with
                {
                    IsSessionReady = true,
                    Status = status,
                }
                : new TwitchConnectionReadinessSnapshot(
                    IsSessionReady: false,
                    TwitchEventSubConnectionState.Offline,
                    status);
            RefreshReadySource();
        }
    }

    public void SetEventSubConnecting(bool isReconnect, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        lock (_gate)
        {
            _current = _current with
            {
                EventSubState = isReconnect
                    ? TwitchEventSubConnectionState.Reconnecting
                    : TwitchEventSubConnectionState.Connecting,
                Status = status,
            };
            RefreshReadySource();
        }
    }

    public void SetEventSubReady(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        lock (_gate)
        {
            _current = _current with
            {
                EventSubState = TwitchEventSubConnectionState.Ready,
                Status = status,
            };
            RefreshReadySource();
        }
    }

    public void SetEventSubOffline(string status, bool isFaulted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        lock (_gate)
        {
            _current = _current with
            {
                EventSubState = isFaulted
                    ? TwitchEventSubConnectionState.Faulted
                    : TwitchEventSubConnectionState.Offline,
                Status = status,
            };
            RefreshReadySource();
        }
    }

    public async Task WaitForEventSubReadyAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task readyTask;
            lock (_gate)
            {
                if (_current.IsFullyReady)
                {
                    return;
                }

                readyTask = _fullyReady.Task;
            }

            await readyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RefreshReadySource()
    {
        if (_current.IsFullyReady)
        {
            _fullyReady.TrySetResult();
        }
        else if (_fullyReady.Task.IsCompleted)
        {
            _fullyReady = NewReadySource();
        }
    }

    private static TaskCompletionSource NewReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

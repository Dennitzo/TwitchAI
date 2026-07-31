namespace TwitchAI.Infrastructure.AI;

public enum LocalAiRunState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Superseded,
}

public enum LocalAiRunKind
{
    Moderation,
    LearningTest,
}

public sealed record LocalAiRunActivity(
    string CorrelationId,
    LocalAiRunState State,
    int MessageCount,
    string? ModelId,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long? ElapsedMilliseconds,
    DateTimeOffset OccurredAtUtc,
    string? ErrorCode = null,
    LocalAiRunKind RunKind = LocalAiRunKind.Moderation);

public sealed class LocalAiRunActivityEventArgs(LocalAiRunActivity activity)
    : EventArgs
{
    public LocalAiRunActivity Activity { get; } = activity;
}

public interface ILocalAiRunActivityMonitor
{
    event EventHandler<LocalAiRunActivityEventArgs>? ActivityChanged;

    void Publish(LocalAiRunActivity activity);
}

public sealed class LocalAiRunActivityMonitor : ILocalAiRunActivityMonitor
{
    public event EventHandler<LocalAiRunActivityEventArgs>? ActivityChanged;

    public void Publish(LocalAiRunActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentException.ThrowIfNullOrWhiteSpace(activity.CorrelationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activity.MessageCount);

        ActivityChanged?.Invoke(this, new LocalAiRunActivityEventArgs(activity));
    }
}

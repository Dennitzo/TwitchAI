using TwitchAI.Core.Contracts;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class LearningContextRevision : ILearningContextRevision
{
    private long _current = 1;

    public static LearningContextRevision ProcessWide { get; } = new();

    public long Current => Interlocked.Read(ref _current);

    public long Advance() => Interlocked.Increment(ref _current);
}

using TwitchAI.Core.Contracts;

namespace TwitchAI.Core.Services;

public sealed class ModerationQueueRuntimeSettings(
    bool removeIdenticalMessages = true)
    : IModerationQueueRuntimeSettings
{
    private int _removeIdenticalMessages =
        removeIdenticalMessages ? 1 : 0;

    public bool RemoveIdenticalMessages
    {
        get => Volatile.Read(ref _removeIdenticalMessages) != 0;
        set => Volatile.Write(
            ref _removeIdenticalMessages,
            value ? 1 : 0);
    }
}

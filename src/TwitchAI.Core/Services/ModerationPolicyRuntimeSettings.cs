using TwitchAI.Core.Contracts;

namespace TwitchAI.Core.Services;

public sealed class ModerationPolicyRuntimeSettings(
    bool backseatgamingEnabled = true)
    : IModerationPolicyRuntimeSettings
{
    private int _backseatgamingEnabled = backseatgamingEnabled ? 1 : 0;

    public bool BackseatgamingEnabled
    {
        get => Volatile.Read(ref _backseatgamingEnabled) != 0;
        set => Volatile.Write(ref _backseatgamingEnabled, value ? 1 : 0);
    }
}

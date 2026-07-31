using TwitchAI.Core.Contracts;

namespace TwitchAI.Core.Services;

public sealed class TwitchRuntimeModeSettings(bool isReadOnly = false)
    : ITwitchRuntimeModeSettings
{
    private int _isReadOnly = isReadOnly ? 1 : 0;

    public bool IsReadOnly
    {
        get => Volatile.Read(ref _isReadOnly) != 0;
        set => Volatile.Write(ref _isReadOnly, value ? 1 : 0);
    }
}

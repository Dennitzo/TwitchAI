using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace TwitchAI.Infrastructure.AI;

public sealed class LocalAiRuntime : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _startup;
    private bool _started;
    private readonly Func<CancellationToken, Task>? _startOverride;

    public LocalAiRuntime() { }

    public LocalAiRuntime(Func<CancellationToken, Task> startOverride)
    {
        _startOverride = startOverride;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startup = StartInBackgroundAsync();
        return Task.CompletedTask;
    }

    private async Task StartInBackgroundAsync()
    {
        try { await EnsureStartedAsync(_shutdown.Token).ConfigureAwait(false); }
        catch (Exception exception) { Trace.TraceError("TwitchAI runtime startup: {0}", exception); }
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_started) return;
            if (_startOverride is not null)
            {
                await _startOverride(linked.Token).ConfigureAwait(false);
                _started = true;
                return;
            }
            await RunScriptAsync("start-ai-stack.ps1", linked.Token).ConfigureAwait(false);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync("http://127.0.0.1:8180/health", linked.Token).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && body.Contains("twitchai", StringComparison.Ordinal)) { _started = true; return; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(1000, linked.Token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("TwitchAI gateway did not become ready on port 8180.");
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_startup is not null) await _startup.ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RunScriptAsync("stop-ai-stack.ps1", cancellationToken).ConfigureAwait(false); _started = false; }
        finally { _gate.Release(); }
    }

    private static async Task RunScriptAsync(string name, CancellationToken cancellationToken)
    {
        var supportRoot = LocalAiRuntimeSupport.EnsureAvailable();
        var script = Path.Combine(supportRoot, "windows", name);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell could not start.");
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".twitchai", "logs", Path.GetFileNameWithoutExtension(name) + ".log");
            var details = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : string.Empty;
            if (details.Length > 2000) details = details[^2000..];
            throw new InvalidOperationException(
                $"{name} failed ({process.ExitCode}). "
                + (details.Length > 0 ? details : $"Runtime-Log: {logPath}"));
        }
    }

    public void Dispose() { _shutdown.Dispose(); _gate.Dispose(); }
}

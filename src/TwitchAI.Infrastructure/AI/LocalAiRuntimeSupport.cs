namespace TwitchAI.Infrastructure.AI;

/// <summary>
/// Provides runtime files from the assembly rather than the single-file host's
/// temporary extraction directory. A copied portable EXE remains self-contained.
/// </summary>
internal static class LocalAiRuntimeSupport
{
    private static readonly string[] RelativePaths =
    [
        "windows/ai-stack-common.ps1",
        "windows/build-ai-stack.ps1",
        "windows/start-ai-stack.ps1",
        "windows/stop-ai-stack.ps1",
        "windows/manage-coding-llama.ps1",
        "windows/manage-llama-server.ps1",
        "workers/coding/catalog.py",
        "workers/coding/session_cache.py",
        "deploy/twitchai/compose.yaml",
        "deploy/twitchai/Dockerfile",
        "deploy/twitchai/gateway.py",
    ];

    public static string EnsureAvailable()
    {
        var assembly = typeof(LocalAiRuntimeSupport).Assembly;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".twitchai",
            "runtime-support",
            assembly.ManifestModule.ModuleVersionId.ToString("N"));

        foreach (var relativePath in RelativePaths)
        {
            var resourceName = "TwitchAI.Runtime." + Path.GetFileName(relativePath);
            using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Die eingebettete TwitchAI-Runtime-Datei fehlt: {resourceName}");
            using var content = new MemoryStream();
            resource.CopyTo(content);
            var expected = content.ToArray();
            var destination = Path.Combine(root, relativePath);
            if (File.Exists(destination)
                && File.ReadAllBytes(destination).AsSpan().SequenceEqual(expected))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, expected);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        return root;
    }
}

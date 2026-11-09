using System.IO;
using System.Diagnostics;
using System.Text.Json;
using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.Tests;

public static class ExternalToolTests
{
    public static Task RunAsync()
    {
        var unsafePath = "C:\\Work Area\\한글 & files\\name \"quoted\".txt";
        var settings = new ExternalToolSettings
        {
            ArgumentTemplates = ["--left", "{left}", "--right={right}", "--repo", "{repo}", "{file}"]
        };
        var rendered = ExternalToolLauncher.RenderArguments(settings, ExternalToolKind.Diff,
            new Dictionary<string, string>
            {
                ["left"] = unsafePath,
                ["right"] = "C:\\temp\\second file.txt",
                ["repo"] = "C:\\repo & echo unsafe",
                ["file"] = unsafePath
            });
        Check(rendered.Count == 6, "templates remain distinct arguments");
        Check(rendered[1] == unsafePath && rendered[2] == "--right=C:\\temp\\second file.txt", "spaces, quotes and Unicode remain literal argument content");
        Check(rendered[3] == "--repo" && rendered[4] == "C:\\repo & echo unsafe", "shell metacharacters are not parsed");
        Check(ExternalToolLauncher.UsesPlaceholder(settings, "left"), "placeholder detection supports fetch decisions");

        Throws<VcsException>(() => ExternalToolLauncher.RenderArguments(
            new ExternalToolSettings { ArgumentTemplates = ["{left}"] }, ExternalToolKind.Diff,
            new Dictionary<string, string> { ["left"] = "left.txt" }), "diff requires both snapshots or a patch");
        Throws<VcsException>(() => ExternalToolLauncher.RenderArguments(
            new ExternalToolSettings { ArgumentTemplates = ["{unsupported}"] }, ExternalToolKind.Blame,
            new Dictionary<string, string>()), "unknown placeholders are rejected");
        Throws<VcsException>(() => ExternalToolLauncher.RenderArguments(
            new ExternalToolSettings { ArgumentTemplates = ["{file}"] }, ExternalToolKind.Blame,
            new Dictionary<string, string>()), "missing placeholder values are rejected");

        CheckSettingsRoundTrip();
        CheckSnapshotCleanup();
        CheckProcessArgumentRoundTrip();
        return Task.CompletedTask;
    }

    private static void CheckProcessArgumentRoundTrip()
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new InvalidOperationException("External tool test requires Windows PowerShell.");
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "external-launch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "capture arguments.ps1");
        var output = Path.Combine(root, "captured arguments.json");
        File.WriteAllText(script,
            "param([string]$outputPath)\n" +
            "$json = ConvertTo-Json -InputObject @($args) -Compress\n" +
            "[IO.File]::WriteAllText($outputPath, $json, [Text.UTF8Encoding]::new($false))\n");
        try
        {
            var settings = new ExternalToolSettings
            {
                ExecutablePath = powershell,
                ArgumentTemplates = ["-WindowStyle", "Hidden", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, output, "label with spaces & \"quotes\"", "repo={repo}", "{left}", "{right}"]
            };
            using var process = ExternalToolLauncher.Launch(settings, ExternalToolKind.Diff,
                new ExternalToolContext(@"C:\Repo & echo", "source file.txt", "left text", "right text"), Path.Combine(root, "snapshots"));
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("External tool argument probe timed out.");
            }
            Check(process.ExitCode == 0 && File.Exists(output), "external process starts with ArgumentList");
            var actual = JsonSerializer.Deserialize<string[]>(File.ReadAllText(output)) ?? [];
            Check(actual.Length == 4, "external process receives each configured argument separately");
            Check(actual[0] == "label with spaces & \"quotes\"", "external process receives spaces, quotes and metacharacters literally");
            Check(actual[1] == "repo=C:\\Repo & echo", "external process receives repository path as one argument");
            Check(File.ReadAllText(actual[2]) == "left text" && File.ReadAllText(actual[3]) == "right text", "external process receives materialized UTF-8 snapshots");
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckSnapshotCleanup()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "snapshot-test-" + Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        try
        {
            var stale = SnapshotStore.CreateDirectory(root);
            File.WriteAllText(Path.Combine(stale, "left.txt"), "private snapshot");
            Directory.SetLastWriteTimeUtc(stale, now - SnapshotStore.Retention - TimeSpan.FromMinutes(1));
            var recent = SnapshotStore.CreateDirectory(root);
            File.WriteAllText(Path.Combine(recent, "right.txt"), "recent snapshot");
            var nested = SnapshotStore.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(nested, "unexpected"));
            Directory.SetLastWriteTimeUtc(nested, now - SnapshotStore.Retention - TimeSpan.FromMinutes(1));

            Check(SnapshotStore.CleanupExpired(now, root) == 1, "cleanup removes only expired flat generated directories");
            Check(!Directory.Exists(stale), "expired snapshot directory is removed");
            Check(Directory.Exists(recent), "recent snapshot directory is retained");
            Check(Directory.Exists(nested), "unexpected nested folders are never recursively deleted");
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckSettingsRoundTrip()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "external-settings-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "settings.json");
        try
        {
            var store = new SettingsStore(file);
            store.Save(new AppSettings
            {
                ExternalTools = new Dictionary<ExternalToolKind, ExternalToolSettings>
                {
                    [ExternalToolKind.Diff] = new() { UseExternal = true, ExecutablePath = "C:\\Tools\\WinMerge.exe", ArgumentTemplates = ["/e", "{left}", "{right}"] },
                    [ExternalToolKind.Blame] = new() { ArgumentTemplates = ["{blame}"] }
                }
            });
            var settings = store.Load();
            Check(settings.ExternalTools[ExternalToolKind.Diff].UseExternal, "external tool selection persists");
            Check(settings.ExternalTools[ExternalToolKind.Diff].ArgumentTemplates.SequenceEqual(["/e", "{left}", "{right}"]), "argument templates persist");
            Check(settings.ExternalTools[ExternalToolKind.Blame].ArgumentTemplates.Single() == "{blame}", "blame templates persist");
        }
        finally
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".local")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException($"External tool test failed: {message}");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"External tool test failed: {message}");
    }
}

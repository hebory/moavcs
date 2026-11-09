using MoaVcs.Core;

namespace MoaVcs.Tests;

public static class ProcessTests
{
    public static async Task RunAsync()
    {
        var runner = new ProcessCommandRunner();
        Assert(ProcessCommandRunner.DefaultTimeout == TimeSpan.FromMinutes(5), "commands without an override have a finite default timeout");
        var result = await runner.RunAsync(new CommandSpec("git", ["--version"], Environment.CurrentDirectory));
        Assert(result.Success && result.StandardOutput.Contains("git version", StringComparison.OrdinalIgnoreCase), "process captures successful stdout");

        var failed = await runner.RunAsync(new CommandSpec("git", ["definitely-not-a-git-command"], Environment.CurrentDirectory));
        Assert(!failed.Success && !string.IsNullOrWhiteSpace(failed.Output), "process returns nonzero exit and stderr");

        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            try
            {
                await runner.RunAsync(new CommandSpec("missing-command-should-not-start", [], Environment.CurrentDirectory),
                    alreadyCancelled.Token);
                throw new InvalidOperationException("Expected a pre-cancelled process not to start.");
            }
            catch (OperationCanceledException) { }
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await runner.RunAsync(new CommandSpec("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"],
                Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(15)), cancellation.Token);
            throw new InvalidOperationException("Expected a cancelled process to throw.");
        }
        catch (OperationCanceledException) { }

        try
        {
            await runner.RunAsync(new CommandSpec("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"],
                Environment.CurrentDirectory, Timeout: TimeSpan.FromMilliseconds(300)));
            throw new InvalidOperationException("Expected the command timeout to stop a slow process.");
        }
        catch (VcsException ex)
        {
            Assert(ex.Message.Contains("제한 시간", StringComparison.Ordinal), "timeout reports a clear diagnostic");
        }

        try
        {
            await runner.RunAsync(new CommandSpec("powershell.exe", ["-NoProfile", "-Command",
                    "[Console]::Out.Write('x' * 5000000); [Console]::Error.Write('y' * 5000000)"],
                Environment.CurrentDirectory, Timeout: TimeSpan.FromSeconds(15)));
            throw new InvalidOperationException("Expected excessive process output to be rejected.");
        }
        catch (VcsException ex)
        {
            Assert(ex.Message.Contains("출력이", StringComparison.Ordinal) &&
                   ex.Message.Contains("MiB", StringComparison.Ordinal), "output limit reports a clear diagnostic");
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Process test failed: {description}");
    }
}

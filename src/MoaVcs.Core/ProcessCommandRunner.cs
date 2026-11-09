using System.Diagnostics;
using System.Text;
using System.ComponentModel;

namespace MoaVcs.Core;

/// <summary>Runs version-control CLIs without invoking a shell. Output is kept bounded to protect the GUI process.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public const int MaxOutputCharacters = 4 * 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command.Executable)) throw new ArgumentException("명령 실행 파일을 지정해야 합니다.", nameof(command));
        var workingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
            ? Environment.CurrentDirectory : Path.GetFullPath(command.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException($"작업 폴더를 찾을 수 없습니다: {workingDirectory}");

        var start = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, false),
            StandardErrorEncoding = new UTF8Encoding(false, false),
            StandardInputEncoding = new UTF8Encoding(false, false)
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new VcsException($"'{command.Executable}' 명령을 시작하지 못했습니다.");
        }
        catch (Win32Exception e)
        {
            throw new VcsException($"'{command.Executable}' 실행 파일을 찾을 수 없습니다. 설치 여부 또는 실행 파일 경로를 확인하세요.\n{e.Message}");
        }
        catch (InvalidOperationException e)
        {
            throw new VcsException($"'{command.Executable}' 명령을 시작하지 못했습니다. {e.Message}");
        }

        var timeout = command.Timeout ?? DefaultTimeout;
        using var timeoutSource = timeout > TimeSpan.Zero
            ? new CancellationTokenSource(timeout) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdout = ReadBoundedAsync(process.StandardOutput, linked.Token, process);
        var stderr = ReadBoundedAsync(process.StandardError, linked.Token, process);
        var stdin = WriteInputAsync(process, command.StandardInput, linked.Token);
        var wait = process.WaitForExitAsync(linked.Token);
        var outputs = Task.WhenAll(stdout, stderr);

        try
        {
            var first = await Task.WhenAny(outputs, wait).ConfigureAwait(false);
            if (first == outputs && outputs.IsFaulted) TryKill(process);
            await Task.WhenAll(outputs, stdin, wait).ConfigureAwait(false);
            return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAfterKillAsync(stdout, stderr, stdin, process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new VcsException($"'{command.Executable}' 명령이 제한 시간 {timeout.TotalSeconds:0}초를 초과했습니다.");
        }
        catch (OutputLimitException)
        {
            TryKill(process);
            await DrainAfterKillAsync(stdout, stderr, stdin, process).ConfigureAwait(false);
            throw new VcsException($"'{command.Executable}' 명령의 출력이 {MaxOutputCharacters / (1024 * 1024)} MiB 제한을 초과했습니다.");
        }
        catch (IOException e) when (e is not OutputLimitException)
        {
            TryKill(process);
            await DrainAfterKillAsync(stdout, stderr, stdin, process).ConfigureAwait(false);
            throw new VcsException($"'{command.Executable}' 명령의 입력 또는 출력을 처리하지 못했습니다. {e.Message}");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken, Process process)
    {
        try
        {
            const int bufferSize = 4096;
            var buffer = new char[bufferSize];
            var result = new StringBuilder(Math.Min(bufferSize, MaxOutputCharacters));
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) return result.ToString();
                if (result.Length + count > MaxOutputCharacters) throw new OutputLimitException();
                result.Append(buffer, 0, count);
            }
        }
        catch { TryKill(process); throw; }
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) when (process.HasExited) { }
    }

    private static async Task DrainAfterKillAsync(Task<string> stdout, Task<string> stderr, Task stdin, Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }
        try { await Task.WhenAll(stdout, stderr, stdin).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed class OutputLimitException : IOException { }
}

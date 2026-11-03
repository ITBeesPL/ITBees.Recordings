using System.Diagnostics;
using System.Text;

namespace ITBees.Recordings.Infrastructure;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string Describe() => $"exit {ExitCode}: {(StandardError.Trim().Length > 0 ? StandardError.Trim() : StandardOutput.Trim())}";
}

internal interface ICommandRunner
{
    /// <summary>Runs a program without a shell (arguments are passed as they are). Exit code -1 = could not start or timed out.</summary>
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            // Program missing (Win32Exception) - reported like a failed command.
            return new CommandResult(-1, string.Empty, $"{fileName}: {e.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
            {
                // Exited in the meantime, or a child runs as root (sudo) and cannot be killed by us - it ends on its own.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new CommandResult(-1, output.ToString(), $"{fileName}: timed out after {timeout.TotalSeconds:0} s");
        }

        // Flush the asynchronous readers.
        process.WaitForExit();
        return new CommandResult(process.ExitCode, output.ToString(), error.ToString());
    }
}

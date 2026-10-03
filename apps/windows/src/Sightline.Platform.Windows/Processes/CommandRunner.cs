using System.Diagnostics;
using System.Text;

namespace Sightline.Platform.Windows.Processes;

/// <summary>What a command printed, and how it ended.</summary>
/// <param name="ExitCode">The process's exit code; 0 is success for every tool Sightline runs.</param>
/// <param name="Output">Everything it wrote to standard output and standard error.</param>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>Runs a Windows tool. The seam that keeps tests from running real ones.</summary>
public interface ICommandRunner
{
    /// <summary>Runs <paramref name="file"/> with <paramref name="arguments"/> and waits for it.</summary>
    CommandResult Run(string file, string arguments);
}

/// <summary>Runs tools for real, hidden, with their output captured as UTF-8.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <inheritdoc />
    public CommandResult Run(string file, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // A neighbouring network with an emoji in its name is not a reason for this to fail.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        process.Start();
        // Both streams read concurrently: a tool that fills one while this waits on the other
        // would otherwise deadlock.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, output + error.GetAwaiter().GetResult());
    }
}

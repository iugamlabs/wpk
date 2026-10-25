using System.Diagnostics;
using System.Text;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed class ProcessRunner(TextWriter? output = null, TextWriter? error = null) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(CommandSpec command, TimeSpan? timeout,
        bool streamOutput, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue) deadline.CancelAfter(timeout.Value);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = !streamOutput, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        if (command.Environment is not null)
            foreach (var (name, value) in command.Environment) process.StartInfo.Environment[name] = value;
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        if (!streamOutput) process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(process.StandardOutput, stdout, streamOutput ? output ?? Console.Out : null, deadline.Token);
        var errTask = PumpAsync(process.StandardError, stderr, streamOutput ? error ?? Console.Error : null, deadline.Token);
        var interrupted = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            // Descendants can inherit pipes. Reading is bounded by the same deadline.
            await Task.WhenAll(outTask, errTask);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try { await Task.WhenAll(outTask, errTask); } catch (OperationCanceledException) { }
        }
        return new(process.HasExited ? process.ExitCode : null, stdout.ToString(), stderr.ToString(),
            interrupted && !cancellationToken.IsCancellationRequested, interrupted && cancellationToken.IsCancellationRequested);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder capture, TextWriter? writer, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            capture.Append(buffer, 0, length);
            if (writer is not null)
            {
                await writer.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
    }
}

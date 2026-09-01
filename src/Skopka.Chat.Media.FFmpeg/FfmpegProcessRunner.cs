using System.ComponentModel;
using System.Diagnostics;

namespace Skopka.Chat.Media.FFmpeg;

internal sealed record FfmpegInvocation(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

internal interface IFfmpegProcessRunner
{
    ValueTask<int> RunAsync(FfmpegInvocation invocation, CancellationToken cancellationToken);
}

internal sealed class FfmpegProcessRunner : IFfmpegProcessRunner
{
    public async ValueTask<int> RunAsync(FfmpegInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new MediaPreparationException("Media processor could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new MediaPreparationException("Media processor could not be started.");
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        using var timeout = new CancellationTokenSource(invocation.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await AwaitDrainsAsync(stdout, stderr).ConfigureAwait(false);
            throw new MediaPreparationException("Media processing timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitDrainsAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        await AwaitDrainsAsync(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task AwaitDrainsAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The process may close redirected pipes while cancellation terminates its process tree.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (SystemException)
        {
        }
    }
}

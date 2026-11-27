using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    public sealed record ProcessResult(string StdOut, string StdErr);

    public static class ProcessRunner
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

        /// <summary>Runs a program with an argument list (no shell quoting), a timeout and cancellation.</summary>
        public static Task<ProcessResult> RunAsync(string fileName, CancellationToken ct, params string[] args) =>
            RunAsync(fileName, DefaultTimeout, ct, args);

        public static async Task<ProcessResult> RunAsync(string fileName, TimeSpan timeout, CancellationToken ct, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException($"{Path.GetFileName(fileName)} took longer than {timeout.TotalMinutes} minutes");
            }

            var result = new ProcessResult(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
            {
                var tail = Regex.Replace(result.StdErr, @"\s+", " ");
                throw new InvalidOperationException(
                    $"{Path.GetFileName(fileName)} failed (exit {process.ExitCode}): {tail[Math.Max(0, tail.Length - 400)..]}");
            }

            return result;
        }
    }
}

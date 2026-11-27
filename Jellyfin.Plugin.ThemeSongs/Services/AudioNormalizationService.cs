using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    /// <summary>
    /// Trims silence, normalizes loudness (two-pass EBU R128 loudnorm to the configured LUFS
    /// target) and adds fades. Uses Jellyfin's own ffmpeg unless another path is configured.
    /// </summary>
    public class AudioNormalizationService : IAudioNormalizationService
    {
        private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(3);
        private readonly ILogger<AudioNormalizationService> _logger;
        private readonly IMediaEncoder _mediaEncoder;

        public AudioNormalizationService(ILogger<AudioNormalizationService> logger, IMediaEncoder mediaEncoder)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mediaEncoder = mediaEncoder;
        }

        /// <summary>Folder of the ffmpeg in use (for yt-dlp --ffmpeg-location).</summary>
        public string FfmpegDirectory => Path.GetDirectoryName(FfmpegPath());

        private string FfmpegPath()
        {
            var configured = Plugin.Instance?.Configuration?.FfmpegPath;
            if (!string.IsNullOrWhiteSpace(configured) && configured != "ffmpeg")
            {
                return configured;
            }

            var own = _mediaEncoder?.EncoderPath;
            return string.IsNullOrWhiteSpace(own) ? "ffmpeg" : own;
        }

        private string FfprobePath()
        {
            var ffmpeg = FfmpegPath();
            if (ffmpeg == _mediaEncoder?.EncoderPath && !string.IsNullOrWhiteSpace(_mediaEncoder?.ProbePath))
            {
                return _mediaEncoder.ProbePath;
            }

            var dir = Path.GetDirectoryName(ffmpeg);
            return string.IsNullOrEmpty(dir) ? "ffprobe" : Path.Combine(dir, "ffprobe");
        }

        public async Task<string> ProcessAsync(string inputFilePath, string workDir, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            Directory.CreateDirectory(workDir);
            int maxLength = Math.Max(config?.MaxDurationSeconds ?? 0, 0);
            if (config?.NormalizeAudio != true)
            {
                double inLength = await GetDurationAsync(inputFilePath, cancellationToken).ConfigureAwait(false);
                bool tooLong = maxLength > 0 && inLength > maxLength;
                if (AudioFile.LooksLikeMp3(inputFilePath) && !tooLong)
                {
                    return inputFilePath;
                }

                // Not MP3 (e.g. a YouTube download) and/or too long: convert, cut, fade out the cut.
                string mp3 = Path.Combine(workDir, Path.GetFileNameWithoutExtension(inputFilePath) + ".converted.mp3");
                var args = new List<string> { "-nostdin", "-hide_banner", "-y", "-i", inputFilePath, "-vn" };
                if (tooLong)
                {
                    double fade = Math.Min(Math.Max(config?.FadeOutDuration ?? 3, 1), maxLength * 0.25);
                    args.AddRange(new[]
                    {
                        "-t", maxLength.ToString(CultureInfo.InvariantCulture),
                        "-af", string.Create(CultureInfo.InvariantCulture, $"afade=t=out:st={maxLength - fade:F2}:d={fade:F2}:curve=hsin")
                    });
                }

                args.AddRange(new[] { "-ar", "44100", "-c:a", "libmp3lame", "-q:a", "2", mp3 });
                await ProcessRunner.RunAsync(FfmpegPath(), cancellationToken, args.ToArray()).ConfigureAwait(false);
                return mp3;
            }

            string stem = Path.Combine(workDir, Path.GetFileNameWithoutExtension(inputFilePath));
            string trimmed = stem + ".trim.wav";
            string output = stem + ".normalized.mp3";
            try
            {
                // 1. Trim leading/trailing silence first, so the fade-out lands on the real end.
                await ProcessRunner.RunAsync(FfmpegPath(), cancellationToken,
                    "-nostdin", "-hide_banner", "-y", "-i", inputFilePath, "-vn",
                    "-af", "silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB," +
                           "areverse,silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB,areverse",
                    "-c:a", "pcm_s16le", trimmed).ConfigureAwait(false);

                double duration = await GetDurationAsync(trimmed, cancellationToken).ConfigureAwait(false);
                if (duration <= 1)
                {
                    throw new InvalidDataException($"audio is only {duration:F1} s long after trimming silence");
                }

                // Cut long themes (full songs) to the configured maximum.
                double length = maxLength > 0 ? Math.Min(duration, maxLength) : duration;
                string lengthArg = length.ToString("F2", CultureInfo.InvariantCulture);

                // 2. Measure loudness (of the part that is kept).
                string target = config.NormalizeAudioVolume.ToString(CultureInfo.InvariantCulture);
                string loudnorm = $"loudnorm=I={target}:TP=-1.5:LRA=11";
                var measure = await ProcessRunner.RunAsync(FfmpegPath(), cancellationToken,
                    "-nostdin", "-hide_banner", "-t", lengthArg, "-i", trimmed, "-af", loudnorm + ":print_format=json", "-f", "null", "-")
                    .ConfigureAwait(false);
                var m = ParseLoudnorm(measure.StdErr);

                // 3. Apply measured loudnorm + fades (never longer than a quarter of the song each).
                double maxFade = length * 0.25;
                double fadeIn = Math.Min(Math.Max(config.FadeInDuration, 0), maxFade);
                double fadeOut = Math.Min(Math.Max(config.FadeOutDuration, 0), maxFade);
                var filters = new List<string>
                {
                    m == null
                        ? loudnorm
                        : string.Create(CultureInfo.InvariantCulture,
                            $"{loudnorm}:measured_I={m["input_i"]}:measured_TP={m["input_tp"]}:measured_LRA={m["input_lra"]}:measured_thresh={m["input_thresh"]}:offset={m["target_offset"]}:linear=true")
                };
                if (fadeIn > 0)
                {
                    filters.Add(string.Create(CultureInfo.InvariantCulture, $"afade=t=in:st=0:d={fadeIn:F2}:curve=hsin"));
                }

                if (fadeOut > 0)
                {
                    filters.Add(string.Create(CultureInfo.InvariantCulture, $"afade=t=out:st={length - fadeOut:F2}:d={fadeOut:F2}:curve=hsin"));
                }

                await ProcessRunner.RunAsync(FfmpegPath(), cancellationToken,
                    "-nostdin", "-hide_banner", "-y", "-t", lengthArg, "-i", trimmed, "-af", string.Join(",", filters),
                    "-ar", "44100", "-c:a", "libmp3lame", "-q:a", "2", output).ConfigureAwait(false);

                _logger.LogDebug("Normalized {Input} ({Duration:F1} of {Full:F1} s, measured {Loudness} LUFS)", inputFilePath, length, duration,
                    m?["input_i"] ?? "?");
                return output;
            }
            finally
            {
                TryDelete(trimmed);
            }
        }

        private static Dictionary<string, string> ParseLoudnorm(string stderr)
        {
            int start = stderr.LastIndexOf('{');
            int end = stderr.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            try
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stderr.Substring(start, end - start + 1));
                foreach (var key in new[] { "input_i", "input_tp", "input_lra", "input_thresh", "target_offset" })
                {
                    if (!values.TryGetValue(key, out var v) ||
                        !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
                        double.IsInfinity(d))
                    {
                        return null;   // silence/invalid measurement: fall back to one-pass loudnorm
                    }
                }

                return values;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private async Task<double> GetDurationAsync(string path, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync(FfprobePath(), ct,
                "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path)
                .ConfigureAwait(false);
            return double.TryParse(r.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

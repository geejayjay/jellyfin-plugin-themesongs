using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    /// <summary>Downloads the audio of a video page (YouTube) with yt-dlp.</summary>
    public class VideoAudioDownloader
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
        private readonly ILogger<VideoAudioDownloader> _logger;
        private bool _warnedMissing;

        public VideoAudioDownloader(ILogger<VideoAudioDownloader> logger)
        {
            _logger = logger;
        }

        private static string YtDlp()
        {
            var p = Plugin.Instance?.Configuration?.YtDlpPath;
            return string.IsNullOrWhiteSpace(p) ? "yt-dlp" : p.Trim();
        }

        /// <summary>Returns the downloaded audio file (any format), or null.</summary>
        public async Task<string> DownloadAsync(string url, string workDir, string ffmpegDirectory, CancellationToken ct)
        {
            Directory.CreateDirectory(workDir);
            try
            {
                var args = new[]
                {
                    "--no-playlist", "--no-progress", "--no-warnings", "--quiet",
                    "-f", "bestaudio/best", "--max-filesize", "60M",
                    "--ffmpeg-location", ffmpegDirectory ?? string.Empty,
                    "-o", Path.Combine(workDir, "video.%(ext)s"),
                    "--", url
                };
                await ProcessRunner.RunAsync(YtDlp(), Timeout, ct, args).ConfigureAwait(false);
            }
            catch (Win32Exception)
            {
                if (!_warnedMissing)
                {
                    _logger.LogWarning("yt-dlp not found ({Path}) - providers that return YouTube links are skipped. Install yt-dlp or set its path in the plugin settings.", YtDlp());
                    _warnedMissing = true;
                }

                return null;
            }

            var file = Directory.GetFiles(workDir, "video.*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal));
            return file != null && new FileInfo(file).Length > 0 ? file : null;
        }
    }
}

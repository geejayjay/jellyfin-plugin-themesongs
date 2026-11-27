using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeSongs.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.ScheduledTasks
{
    public class DownloadThemeSongsTask : IScheduledTask
    {
        private readonly IThemeSongDownloadService _downloadService;
        private readonly ILogger<DownloadThemeSongsTask> _logger;

        public DownloadThemeSongsTask(IThemeSongDownloadService downloadService, ILogger<DownloadThemeSongsTask> logger)
        {
            _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => "Download TV Theme Songs";

        public string Key => "DownloadTV ThemeSongs";

        public string Description =>
            "Downloads theme songs for series that don't have one. Uses Jellyfin's database to see which series already have a theme (no media disk scan); series no provider had are asked again after the configured number of days.";

        public string Category => "Theme Songs";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!await _downloadService.DownloadAllThemeSongsAsync(false, progress, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Skipped: a theme song download started from the plugin page is still running");
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            };
        }
    }
}

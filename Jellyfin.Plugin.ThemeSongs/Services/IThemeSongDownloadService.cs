using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    public interface IThemeSongDownloadService
    {
        bool IsRunning { get; }

        /// <summary>Returns false when a run is already in progress (nothing started).</summary>
        /// <param name="forceDownload">Re-download every theme (except uploaded/deleted ones).</param>
        /// <param name="retryMissing">Ask again for series not found recently (ignores RetryNotFoundDays), existing themes untouched.</param>
        Task<bool> DownloadAllThemeSongsAsync(bool forceDownload = false, IProgress<double> progress = null,
            CancellationToken cancellationToken = default, bool retryMissing = false);

        Task<bool> DownloadThemeSongForSeriesAsync(Series series, CancellationToken cancellationToken = default);

        Task InstallUploadAsync(Series series, string file, CancellationToken cancellationToken);

        void Delete(Series series);

        Task<int> MoveExistingToStorageAsync(CancellationToken cancellationToken);
    }
}

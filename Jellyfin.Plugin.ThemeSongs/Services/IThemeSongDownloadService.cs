using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    public interface IThemeSongDownloadService
    {
        Task DownloadAllThemeSongsAsync(bool forceDownload = false, CancellationToken cancellationToken = default);
        Task<bool> DownloadThemeSongForSeriesAsync(Series series, bool forceDownload = false, CancellationToken cancellationToken = default);
    }
}
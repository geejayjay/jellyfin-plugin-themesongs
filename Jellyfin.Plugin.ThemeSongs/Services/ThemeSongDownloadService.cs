using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeSongs.Configuration;
using Jellyfin.Plugin.ThemeSongs.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    public class ThemeSongDownloadService : IThemeSongDownloadService
    {
        private readonly SemaphoreSlim _runLock = new(1, 1);
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClientService _httpClientService;
        private readonly IAudioNormalizationService _audioNormalizationService;
        private readonly IEnumerable<IThemeSongProvider> _providers;
        private readonly ThemeSongState _state;
        private readonly ThemeSongStorage _storage;
        private readonly VideoAudioDownloader _videoDownloader;
        private readonly ILogger<ThemeSongDownloadService> _logger;
        private readonly string _cachePath;

        public ThemeSongDownloadService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IHttpClientService httpClientService,
            IAudioNormalizationService audioNormalizationService,
            IEnumerable<IThemeSongProvider> providers,
            ThemeSongState state,
            ThemeSongStorage storage,
            VideoAudioDownloader videoDownloader,
            ILogger<ThemeSongDownloadService> logger,
            string cachePath)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _httpClientService = httpClientService ?? throw new ArgumentNullException(nameof(httpClientService));
            _audioNormalizationService = audioNormalizationService ?? throw new ArgumentNullException(nameof(audioNormalizationService));
            _providers = providers ?? throw new ArgumentNullException(nameof(providers));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _videoDownloader = videoDownloader ?? throw new ArgumentNullException(nameof(videoDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cachePath = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
        }

        public bool IsRunning => _runLock.CurrentCount == 0;

        private List<Series> AllSeries() =>
            _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Series>().Where(s => !string.IsNullOrEmpty(s.Path)).ToList();

        /// <summary>Theme songs Jellyfin knows from its database (no disk access).</summary>
        private static bool HasTheme(Series s) => s.GetThemeSongs().Any();

        public async Task<bool> DownloadAllThemeSongsAsync(bool forceDownload = false, IProgress<double> progress = null,
            CancellationToken cancellationToken = default, bool retryMissing = false)
        {
            if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Theme song download is already running - not started twice");
                return false;
            }

            try
            {
                var config = Plugin.Instance?.Configuration;
                if (EnabledProviders().Count == 0)
                {
                    _logger.LogWarning("Theme songs: no providers configured - add one in Dashboard -> Plugins -> Theme Songs");
                    return true;
                }

                int retryDays = Math.Max(config?.RetryNotFoundDays ?? 14, 1);
                var all = AllSeries();
                var todo = new List<Series>();
                int skippedRecent = 0, skippedDeleted = 0, withTheme = 0;

                foreach (var s in all)
                {
                    var entry = _state.Get(s.Id);
                    if (entry?.Status == ThemeStatus.Uploaded || entry?.Status == ThemeStatus.Deleted)
                    {
                        skippedDeleted += entry.Status == ThemeStatus.Deleted ? 1 : 0;
                        withTheme += entry.Status == ThemeStatus.Uploaded ? 1 : 0;
                        continue;                              // the user decided: never touched automatically
                    }

                    if (HasTheme(s))
                    {
                        withTheme++;
                        if (entry == null)
                        {
                            _state.Set(s.Id, s.Name, ThemeStatus.Existing);
                        }

                        if (!forceDownload)
                        {
                            continue;
                        }
                    }
                    else if (!forceDownload && !retryMissing && entry != null &&
                             (entry.Status == ThemeStatus.NotFound || entry.Status == ThemeStatus.Failed) &&
                             entry.Checked > DateTime.UtcNow.AddDays(-retryDays))
                    {
                        skippedRecent++;
                        continue;
                    }

                    todo.Add(s);
                }

                _logger.LogInformation(
                    "Theme songs: {Total} series, {WithTheme} with a theme, {Todo} to look up, {Recent} not found in the last {Days} days, {Deleted} deleted by you (force: {Force})",
                    all.Count, withTheme, todo.Count, skippedRecent, retryDays, skippedDeleted, forceDownload);

                int done = 0, ok = 0;
                foreach (var series in todo)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await DownloadThemeSongForSeriesAsync(series, cancellationToken).ConfigureAwait(false))
                    {
                        ok++;
                    }

                    done++;
                    progress?.Report(100.0 * done / todo.Count);
                    if (done % 10 == 0)
                    {
                        _state.Save();
                    }
                }

                _logger.LogInformation("Theme songs: {Ok} of {Count} downloaded. Totals: {Counts}", ok, todo.Count,
                    string.Join(", ", _state.Counts().Select(kv => $"{kv.Key} {kv.Value}")));
                return true;
            }
            finally
            {
                _state.Save();
                progress?.Report(100);
                _runLock.Release();
            }
        }

        public async Task<bool> DownloadThemeSongForSeriesAsync(Series series, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(series);
            try
            {
                // Ask the providers in order; if one's file can't be downloaded, try the next.
                bool anyFound = false;
                foreach (var (entry, provider) in EnabledProviders())
                {
                    ThemeSource source = null;
                    try
                    {
                        source = await provider.FindAsync(entry, series, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning("Provider {Provider} failed for {SeriesName}: {Message}", entry.Name, series.Name, ex.Message);
                    }

                    if (source == null)
                    {
                        continue;
                    }

                    anyFound = true;
                    if (await DownloadAndInstallAsync(series, source, entry.Name, cancellationToken).ConfigureAwait(false))
                    {
                        _state.Set(series.Id, series.Name, ThemeStatus.Downloaded, entry.Name);
                        _logger.LogInformation("Theme song for {SeriesName} installed (from {Provider})", series.Name, entry.Name);
                        QueueRefresh(series);
                        return true;
                    }
                }

                _state.Set(series.Id, series.Name, anyFound ? ThemeStatus.Failed : ThemeStatus.NotFound);
                if (!anyFound)
                {
                    _logger.LogDebug("No theme song found for {SeriesName}", series.Name);
                }

                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Typical: the series folder is read-only for Jellyfin, ffmpeg failed, disk full.
                _logger.LogError("Theme song for {SeriesName} failed: {Message}", series.Name, ex.Message);
                _state.Set(series.Id, series.Name, ThemeStatus.Failed);
                return false;
            }
        }

        /// <summary>Installs a file the user uploaded (already checked to be an MP3).</summary>
        public async Task InstallUploadAsync(Series series, string file, CancellationToken cancellationToken)
        {
            await _storage.InstallAsync(series, file, cancellationToken).ConfigureAwait(false);
            _state.Set(series.Id, series.Name, ThemeStatus.Uploaded, "upload");
            _state.Save();
            QueueRefresh(series);
        }

        public void Delete(Series series)
        {
            _storage.Delete(series);
            _state.Set(series.Id, series.Name, ThemeStatus.Deleted);
            _state.Save();
            QueueRefresh(series);
        }

        /// <summary>Moves every real theme.mp3 that Jellyfin knows about into the storage folder (symlink left behind).</summary>
        public async Task<int> MoveExistingToStorageAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(ThemeSongStorage.StoragePath))
            {
                throw new InvalidOperationException("no theme storage folder configured");
            }

            if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("a theme song download is running, try again later");
            }

            try
            {
                int moved = 0;
                foreach (var series in AllSeries().Where(HasTheme))
                {
                    try
                    {
                        if (await _storage.MoveToStorageAsync(series, cancellationToken).ConfigureAwait(false))
                        {
                            moved++;
                            QueueRefresh(series);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogError("Moving the theme of {SeriesName} failed: {Message}", series.Name, ex.Message);
                    }
                }

                _logger.LogInformation("Moved {Count} theme songs to {Path}", moved, ThemeSongStorage.StoragePath);
                return moved;
            }
            finally
            {
                _runLock.Release();
            }
        }

        private void QueueRefresh(Series series)
        {
            // Refresh only the local files (extras): no online metadata lookups, no image downloads.
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly
            };
            _providerManager.QueueRefresh(series.Id, options, RefreshPriority.Normal);
        }

        /// <summary>Configured provider entries (enabled, in priority order) with their type implementation.</summary>
        private List<(ProviderEntry Entry, IThemeSongProvider Provider)> EnabledProviders()
        {
            var types = _providers.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);
            var result = new List<(ProviderEntry, IThemeSongProvider)>();
            foreach (var e in (Plugin.Instance?.Configuration?.Providers ?? new List<ProviderEntry>())
                         .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Url)).OrderBy(e => e.Priority))
            {
                if (types.TryGetValue(e.Type ?? string.Empty, out var p))
                {
                    result.Add((e, p));
                }
                else
                {
                    _logger.LogWarning("Theme song provider {Name} has unknown type {Type} - skipped", e.Name, e.Type);
                }
            }

            return result;
        }

        private async Task<bool> DownloadAndInstallAsync(Series series, ThemeSource source, string providerName, CancellationToken cancellationToken)
        {
            string work = Path.Combine(_cachePath, series.Id.ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string downloaded;
                if (source.Kind == ThemeSourceKind.Video)
                {
                    downloaded = await _videoDownloader.DownloadAsync(source.Url, work,
                        (_audioNormalizationService as AudioNormalizationService)?.FfmpegDirectory, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    downloaded = Path.Combine(work, "download.mp3");
                    if (!await _httpClientService.DownloadFileAsync(source.Url, downloaded, cancellationToken).ConfigureAwait(false))
                    {
                        downloaded = null;
                    }
                }

                if (downloaded == null)
                {
                    _logger.LogWarning("{Provider} had a theme for {SeriesName} but the download failed ({Url})", providerName, series.Name, source.Url);
                    return false;
                }

                string processed = await _audioNormalizationService.ProcessAsync(downloaded, work, cancellationToken).ConfigureAwait(false);
                await _storage.InstallAsync(series, processed, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

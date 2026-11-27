using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeSongs.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Api
{
    /// <summary>
    /// Theme Songs API. Administrators only: these endpoints write into the media folders.
    /// </summary>
    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("ThemeSongs")]
    [Produces(MediaTypeNames.Application.Json)]
    public class ThemeSongsController : ControllerBase
    {
        private readonly IThemeSongDownloadService _downloadService;
        private readonly ThemeSongState _state;
        private readonly ILogger<ThemeSongsController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerApplicationPaths _paths;

        public ThemeSongsController(
            IThemeSongDownloadService downloadService,
            ThemeSongState state,
            ILogger<ThemeSongsController> logger,
            ILibraryManager libraryManager,
            IServerApplicationPaths paths)
        {
            _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        }

        /// <summary>Starts a theme song download for all series in the background.</summary>
        /// <response code="202">Started.</response>
        /// <response code="409">A download is already running.</response>
        [HttpPost("DownloadTVShows")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult DownloadTVThemeSongs([FromQuery] bool forceDownload = false, [FromQuery] bool retryMissing = false)
        {
            if (_downloadService.IsRunning)
            {
                return Conflict("A theme song download is already running");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _downloadService.DownloadAllThemeSongsAsync(forceDownload, null, CancellationToken.None, retryMissing).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Theme song download failed");
                }
            });
            return Accepted();
        }

        /// <summary>Moves existing theme.mp3 files into the configured storage folder (symlinks are left in the series folders).</summary>
        [HttpPost("MoveToStorage")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult> MoveToStorage(CancellationToken cancellationToken)
        {
            try
            {
                int moved = await _downloadService.MoveExistingToStorageAsync(cancellationToken).ConfigureAwait(false);
                return Ok(new { moved });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>Provider types for the "Add provider" dropdown.</summary>
        [HttpGet("ProviderTypes")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetProviderTypes([FromServices] IEnumerable<Providers.IThemeSongProvider> providers) =>
            Ok(providers.Select(p => new { type = p.Type, name = p.DisplayName, urlHelp = p.UrlHelp }));

        /// <summary>Running state and counts per status.</summary>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetStatus() =>
            Ok(new { running = _downloadService.IsRunning, storage = ThemeSongStorage.StoragePath, counts = _state.Counts() });

        /// <summary>All TV series with their theme status (from Jellyfin's database).</summary>
        [HttpGet("Series")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<SeriesDto>> GetAllSeries()
        {
            var series = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true
            })
            .OfType<Series>()
            .OrderBy(s => s.SortName ?? s.Name)
            .Select(s => new SeriesDto
            {
                Id = s.Id.ToString(),
                Name = s.Name,
                Path = s.Path,
                HasTheme = s.GetThemeSongs().Any(),
                Status = _state.Get(s.Id)?.Status
            })
            .ToList();

            return Ok(series);
        }

        /// <summary>The theme song of a series (for the player on the plugin page).</summary>
        [HttpGet("Series/{seriesId}/Theme")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetThemeSong(Guid seriesId)
        {
            if (_libraryManager.GetItemById(seriesId) is not Series series)
            {
                return NotFound("Series not found");
            }

            var file = new FileInfo(ThemeSongStorage.LinkPath(series));
            string path = file.LinkTarget != null ? file.ResolveLinkTarget(true)?.FullName : file.FullName;
            if (path == null || !System.IO.File.Exists(path))
            {
                return NotFound("Theme song not found");
            }

            return PhysicalFile(path, "audio/mpeg", "theme.mp3", enableRangeProcessing: true);
        }

        /// <summary>Uploads an MP3 as the theme song of a series (replaces the current one).</summary>
        [HttpPost("Series/{seriesId}/Theme")]
        [RequestSizeLimit(HttpClientService.MaxDownloadBytes + 65536)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> UploadThemeSong(Guid seriesId, IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file provided");
            }

            if (file.Length > HttpClientService.MaxDownloadBytes)
            {
                return BadRequest("File is larger than 20 MB");
            }

            if (_libraryManager.GetItemById(seriesId) is not Series series)
            {
                return NotFound("Series not found");
            }

            string tmpDir = Path.Combine(_paths.CachePath, "ThemeSongs", "upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string tmp = Path.Combine(tmpDir, "upload.mp3");
            try
            {
                await using (var stream = new FileStream(tmp, FileMode.CreateNew))
                {
                    await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                // Check the content, not the browser-supplied content type.
                if (!AudioFile.LooksLikeMp3(tmp))
                {
                    return BadRequest("File is not an MP3");
                }

                await _downloadService.InstallUploadAsync(series, tmp, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Theme song uploaded for {SeriesName}", series.Name);
                return Ok(new { message = "Theme song uploaded successfully" });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Uploading the theme of {SeriesName} failed: {Message}", series.Name, ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError, "Could not save the theme song: " + ex.Message);
            }
            finally
            {
                try
                {
                    Directory.Delete(tmpDir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>Deletes the theme song of a series. It is then not downloaded again automatically.</summary>
        [HttpDelete("Series/{seriesId}/Theme")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteThemeSong(Guid seriesId)
        {
            if (_libraryManager.GetItemById(seriesId) is not Series series)
            {
                return NotFound("Series not found");
            }

            if (ThemeSongStorage.Existing(series) == null)
            {
                return NotFound("Theme song not found");
            }

            try
            {
                _downloadService.Delete(series);
                _logger.LogInformation("Theme song deleted for {SeriesName}", series.Name);
                return Ok(new { message = "Theme song deleted (it won't be downloaded again automatically)" });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Deleting the theme of {SeriesName} failed: {Message}", series.Name, ex.Message);
                return StatusCode(StatusCodes.Status500InternalServerError, "Could not delete the theme song: " + ex.Message);
            }
        }
    }

    public class SeriesDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool HasTheme { get; set; }
        public string Status { get; set; }
    }
}

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeSongs.Configuration;
using Jellyfin.Plugin.ThemeSongs.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeSongs.Providers
{
    /// <summary>
    /// ThemerrDB (community database of theme songs, by TMDB id): {url}/tv_shows/themoviedb/{tmdb}.json
    /// contains a youtube_theme_url, which is downloaded with yt-dlp.
    /// </summary>
    public class ThemerrDbProvider : IThemeSongProvider
    {
        private readonly IHttpClientService _http;

        public ThemerrDbProvider(IHttpClientService http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public string Type => "themerrdb";

        public string DisplayName => "ThemerrDB (by TMDB id, YouTube via yt-dlp)";

        public string UrlHelp => "Base URL of the database, e.g. https://raw.githubusercontent.com/LizardByte/ThemerrDB/gh-pages (needs yt-dlp)";

        public async Task<ThemeSource> FindAsync(ProviderEntry entry, Series series, CancellationToken cancellationToken)
        {
            string tmdb = series.GetProviderId(MetadataProvider.Tmdb);
            string baseUrl = (entry.Url ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrEmpty(tmdb) || !int.TryParse(tmdb, out _) || string.IsNullOrEmpty(baseUrl))
            {
                return null;
            }

            string json = await _http.GetStringAsync($"{baseUrl}/tv_shows/themoviedb/{tmdb}.json", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("youtube_theme_url", out var u) && u.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) &&
                    (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)))
                {
                    return new ThemeSource(uri.ToString(), ThemeSourceKind.Video);
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }
    }
}

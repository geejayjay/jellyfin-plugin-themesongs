using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeSongs.Configuration;
using Jellyfin.Plugin.ThemeSongs.Services;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeSongs.Providers
{
    /// <summary>
    /// Any site that serves one MP3 per show at a predictable address, e.g. Plex's theme archive
    /// https://tvthemes.plexapp.com/{tvdb}.mp3. Checked with a HEAD request.
    /// </summary>
    public class UrlTemplateProvider : IThemeSongProvider
    {
        private readonly IHttpClientService _http;

        public UrlTemplateProvider(IHttpClientService http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public string Type => "url-template";

        public string DisplayName => "Direct MP3 URL (template)";

        public string UrlHelp => "URL with placeholders {tvdb} {tmdb} {imdb} {anidb} {name}, e.g. https://tvthemes.plexapp.com/{tvdb}.mp3";

        public async Task<ThemeSource> FindAsync(ProviderEntry entry, Series series, CancellationToken cancellationToken)
        {
            string url = ProviderIds.Fill(entry.Url, series);
            if (url == null)
            {
                return null;   // the show lacks an id the template needs
            }

            return await _http.HeadRequestAsync(url, cancellationToken).ConfigureAwait(false)
                ? new ThemeSource(url, ThemeSourceKind.Direct)
                : null;
        }
    }
}

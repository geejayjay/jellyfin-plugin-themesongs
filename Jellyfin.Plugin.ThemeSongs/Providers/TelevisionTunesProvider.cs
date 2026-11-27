using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeSongs.Configuration;
using Jellyfin.Plugin.ThemeSongs.Services;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Providers
{
    /// <summary>
    /// televisiontunes.com style site: A–Z pages at {url}/browse/{letter}; every song on them has
    /// data-show="Show name" and a direct /uploads/audio/....mp3 link.
    /// </summary>
    public class TelevisionTunesProvider : IThemeSongProvider
    {
        private static readonly TimeSpan IndexCacheTime = TimeSpan.FromHours(1);

        // One song tile: show name, mp3 path, show, song title (song title may contain escaped quotes).
        private static readonly Regex SongItem = new(
            @"data-show=""(?<show>[^""]*)"".*?playInBar\('(?<mp3>[^']+)','(?:[^'\\]|\\.)*','(?<title>(?:[^'\\]|\\.)*)'",
            RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        private readonly IHttpClientService _http;
        private readonly ILogger<TelevisionTunesProvider> _logger;
        private readonly ConcurrentDictionary<string, (DateTime Time, List<(string Show, string Mp3, string Title)> Songs)> _cache = new();

        public TelevisionTunesProvider(IHttpClientService http, ILogger<TelevisionTunesProvider> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Type => "televisiontunes";

        public string DisplayName => "TelevisionTunes (search by show name)";

        public string UrlHelp => "Base URL of the site, e.g. https://televisiontunes.com";

        public async Task<ThemeSource> FindAsync(ProviderEntry entry, Series series, CancellationToken cancellationToken)
        {
            string baseUrl = (entry.Url ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(series.Name))
            {
                return null;
            }

            var wanted = Variants(series.Name).Select(Normalize).ToList();
            var songs = await GetLetterAsync(baseUrl, Letter(series.Name), cancellationToken).ConfigureAwait(false);
            var matches = songs.Where(s => wanted.Contains(Normalize(s.Show))).ToList();
            if (matches.Count == 0)
            {
                return null;
            }

            // Prefer a "theme"/"opening" over e.g. an ending or a random song from the show.
            int preferred = matches.FindIndex(s => Regex.IsMatch(s.Title, @"\b(main )?theme\b|\bopening\b|\bintro\b", RegexOptions.IgnoreCase));
            var best = matches[Math.Max(preferred, 0)];
            string url = best.Mp3.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? best.Mp3 : baseUrl + best.Mp3;
            _logger.LogDebug("{Provider} match for {SeriesName}: {Show} - {Title}", entry.Name, series.Name, best.Show, best.Title);
            return new ThemeSource(url, ThemeSourceKind.Direct);
        }

        private async Task<List<(string Show, string Mp3, string Title)>> GetLetterAsync(string baseUrl, string letter, CancellationToken ct)
        {
            string key = baseUrl + "|" + letter;
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.Time < IndexCacheTime)
            {
                return hit.Songs;
            }

            string html = await _http.GetStringAsync($"{baseUrl}/browse/{letter}", ct).ConfigureAwait(false) ?? string.Empty;
            var songs = SongItem.Matches(html)
                .Select(m => (WebUtility.HtmlDecode(m.Groups["show"].Value),
                              m.Groups["mp3"].Value,
                              Regex.Unescape(m.Groups["title"].Value)))
                .ToList();
            if (songs.Count > 0)
            {
                _cache[key] = (DateTime.UtcNow, songs);
            }
            else
            {
                _logger.LogWarning("No songs found on {Url}/browse/{Letter} - has the site changed?", baseUrl, letter);
            }

            return songs;
        }

        /// <summary>A–Z page of the title, ignoring a leading "The"/"A"; digits and symbols are on page 0.</summary>
        private static string Letter(string name)
        {
            string t = Regex.Replace(name.Trim(), @"^(the|a)\s+", string.Empty, RegexOptions.IgnoreCase);
            char c = char.ToUpperInvariant(t.FirstOrDefault(char.IsLetterOrDigit));
            return c >= 'A' && c <= 'Z' ? c.ToString() : "0";
        }

        private static IEnumerable<string> Variants(string name)
        {
            yield return name;
            yield return name.Replace("&", "and", StringComparison.Ordinal);
            yield return Regex.Replace(name, @"\s*\(.*?\)", string.Empty);   // without "(2019)" / "(US)"
            if (name.Contains(':', StringComparison.Ordinal))
            {
                yield return name.Split(':')[0];
            }

            if (name.Contains(" - ", StringComparison.Ordinal))
            {
                yield return name.Split(" - ")[0];
            }
        }

        /// <summary>Lower case, letters and digits only ("Avatar: The Last Airbender" == "avatar the last airbender").</summary>
        private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", string.Empty);
    }
}

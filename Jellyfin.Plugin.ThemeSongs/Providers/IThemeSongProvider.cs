using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeSongs.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeSongs.Providers
{
    public enum ThemeSourceKind
    {
        /// <summary>A direct link to an audio file.</summary>
        Direct,

        /// <summary>A video page (YouTube); downloaded with yt-dlp.</summary>
        Video
    }

    public sealed record ThemeSource(string Url, ThemeSourceKind Kind);

    /// <summary>
    /// A provider *type*: knows how to talk to one kind of source. Which sources are used, their
    /// URLs and their order come from the plugin settings (<see cref="ProviderEntry"/>).
    /// </summary>
    public interface IThemeSongProvider
    {
        /// <summary>Key stored in <see cref="ProviderEntry.Type"/>.</summary>
        string Type { get; }

        /// <summary>Shown in the "Add provider" dropdown.</summary>
        string DisplayName { get; }

        /// <summary>Help text for the URL field on the settings page.</summary>
        string UrlHelp { get; }

        Task<ThemeSource> FindAsync(ProviderEntry entry, Series series, CancellationToken cancellationToken);
    }

    public static class ProviderIds
    {
        /// <summary>Fills {tvdb} {tmdb} {imdb} {anidb} {name}; returns null when a needed id is missing.</summary>
        public static string Fill(string template, Series series)
        {
            var values = new Dictionary<string, string>
            {
                ["tvdb"] = series.GetProviderId(MetadataProvider.Tvdb),
                ["tmdb"] = series.GetProviderId(MetadataProvider.Tmdb),
                ["imdb"] = series.GetProviderId(MetadataProvider.Imdb),
                ["anidb"] = series.GetProviderId("AniDB"),
                ["name"] = System.Uri.EscapeDataString(series.Name ?? string.Empty)
            };
            bool missing = false;
            string url = Regex.Replace(template ?? string.Empty, @"\{(\w+)\}", m =>
            {
                if (values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) && !string.IsNullOrEmpty(v))
                {
                    return v;
                }

                missing = true;
                return string.Empty;
            });
            return missing || string.IsNullOrWhiteSpace(url) ? null : url;
        }
    }
}

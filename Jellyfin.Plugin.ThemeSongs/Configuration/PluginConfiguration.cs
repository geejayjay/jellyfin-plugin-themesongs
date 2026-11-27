using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ThemeSongs.Configuration
{
    /// <summary>
    /// One configured theme source. Nothing about the sources is built in: the type only says how
    /// to talk to it, the URL (and everything else) comes from the settings page.
    /// </summary>
    public class ProviderEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Provider type key, see <c>IThemeSongProvider.Type</c> (url-template, televisiontunes, themerrdb).</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Display name (also written to the log / state file as the source).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// url-template: full URL with placeholders {tvdb} {tmdb} {imdb} {anidb} {name}.
        /// televisiontunes / themerrdb: base URL of the site / database.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        /// <summary>Lower number = asked first.</summary>
        public int Priority { get; set; } = 1;
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // Audio processing
        public bool NormalizeAudio { get; set; } = true;

        /// <summary>Target loudness in LUFS (EBU R128 integrated loudness), e.g. -18.</summary>
        public int NormalizeAudioVolume { get; set; } = -18;

        public int FadeInDuration { get; set; } = 3;

        /// <summary>Longer themes (e.g. full songs from YouTube) are cut to this many seconds, with a fade-out. 0 = no limit.</summary>
        public int MaxDurationSeconds { get; set; } = 90;
        public int FadeOutDuration { get; set; } = 3;

        /// <summary>Empty = use Jellyfin's own ffmpeg (Dashboard → Playback → Transcoding).</summary>
        public string FfmpegPath { get; set; } = string.Empty;

        /// <summary>yt-dlp for providers that return YouTube links (themerrdb). Empty = "yt-dlp" from PATH.</summary>
        public string YtDlpPath { get; set; } = string.Empty;

        /// <summary>Configured theme sources, asked in priority order. Empty by default.</summary>
        public List<ProviderEntry> Providers { get; set; } = new();

        // Storage
        /// <summary>
        /// Empty = theme.mp3 is written into the series folder (classic behaviour).
        /// Set (e.g. /var/lib/jellyfin/themesongs) = the real file is kept there and the
        /// series folder only gets a theme.mp3 symlink to it (keeps the audio off the media disks).
        /// </summary>
        public string ThemeStoragePath { get; set; } = string.Empty;

        // Scheduling
        /// <summary>A series no provider had is asked again only after this many days.</summary>
        public int RetryNotFoundDays { get; set; } = 14;
    }
}

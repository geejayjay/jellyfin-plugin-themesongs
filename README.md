<h1 align="center">Jellyfin Theme Songs Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org">Jellyfin Project</a></h3>

<p align="center">
  <a href="https://github.com/geejayjay/jellyfin-plugin-themesongs/actions/workflows/ci.yml">
    <img src="https://github.com/geejayjay/jellyfin-plugin-themesongs/workflows/Build%20%26%20Test/badge.svg" alt="Build Status">
  </a>
  <a href="https://github.com/geejayjay/jellyfin-plugin-themesongs/releases">
    <img src="https://img.shields.io/github/v/release/geejayjay/jellyfin-plugin-themesongs" alt="Release">
  </a>
</p>

<p align="center">
Jellyfin Theme Songs plugin automatically downloads theme songs for your TV show library with configurable providers and audio normalization.
</p>

## ✨ Features

- 🎵 **Automatic theme song downloads** for TV series from **sources you configure** (nothing built in):
  pick a provider type in the *Add Provider* dropdown, fill in its URL and priority.
- 💾 **Optional storage folder + symlinks**: keep the real files on the server's own (SSD) disk and only put a
  `theme.mp3` symlink in each series folder, so theme playback never wakes the media disks.
  One button moves existing `theme.mp3` files there.
- 🧠 **No media disk scan**: which series already have a theme comes from Jellyfin's database; a small state file
  in the plugin's data folder remembers downloads, misses (asked again only after N days, default 14) and themes
  you deleted (never downloaded again automatically).
- 🎚️ **Loudness normalization** (two-pass EBU R128 to your LUFS target), silence trimming and fades,
  using Jellyfin's own FFmpeg.
- 🔒 **Admin-only API**, normal TLS certificate checks, downloads/uploads must really be MP3 (max 20 MB).
- 📅 **Scheduled task** with progress; never runs twice at the same time.

## 📋 Requirements

- Jellyfin 12.4 or newer (.NET 10)
- TVDB ids on your series (for the Plex provider)
- Jellyfin needs write access to the series folders (also with a storage folder: the symlink goes there)

## 🚀 Installation

### From Plugin Repository (Recommended)
1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   ```
   https://raw.githubusercontent.com/geejayjay/jellyfin-plugin-themesongs/master/manifest.json
   ```
3. Go to **Catalog** and search for "Theme Songs"
4. Click **Install** and restart Jellyfin
5. Configure the plugin in **Dashboard → Plugins → Theme Songs**

### From GitHub Releases (Manual)
1. Download the latest `jellyfin-plugin-themesongs-vX.X.X.zip` from [Releases](https://github.com/geejayjay/jellyfin-plugin-themesongs/releases)
2. Extract the ZIP file to your Jellyfin plugins directory:
   - **Windows**: `%ProgramData%\Jellyfin\Server\plugins\Theme Songs\`
   - **Linux**: `/var/lib/jellyfin/plugins/Theme Songs/`
   - **Docker**: `/config/plugins/Theme Songs/`
3. Restart Jellyfin
4. Configure the plugin in **Dashboard → Plugins → Theme Songs**

## ⚙️ Configuration

### Providers
Nothing is hard-coded: add sources under **Theme Song Providers → Add Provider**. Each entry has a type, a name,
a URL, a priority (1 = asked first) and an on/off switch. If a source has nothing (or its download fails), the
next one is tried.

| Type | URL to enter | Example |
| --- | --- | --- |
| Direct MP3 URL (template) | URL with `{tvdb}` `{tmdb}` `{imdb}` `{anidb}` `{name}` | Plex archive: `https://tvthemes.plexapp.com/{tvdb}.mp3` |
| TelevisionTunes | site base URL (A–Z pages at `/browse/<letter>`) | `https://televisiontunes.com` |
| ThemerrDB | database base URL (`/tv_shows/themoviedb/<id>.json`), YouTube links, **needs yt-dlp** | `https://raw.githubusercontent.com/LizardByte/ThemerrDB/gh-pages` |

**Look Up Missing Theme Songs Again** asks the providers right away for every series without a theme
(normally a miss is retried only after *Retry Not Found* days).

### Audio Settings
- **Normalize Audio**: Enable/disable audio normalization
- **Target Loudness**: integrated loudness in LUFS (default -18)
- **FFmpeg Path**: empty = Jellyfin's own FFmpeg

### Storage
- **Theme Storage Folder**: empty = `theme.mp3` in the series folder. Set (e.g. `/var/lib/jellyfin/themesongs`) = real file there, symlink in the series folder. Other machines that see the media folder (SMB, Sonarr) only see a broken link.
- **Retry Not Found After (days)**: default 14

### Usage Options
1. **Manual Download**: Use the "Download Theme Songs" button in plugin configuration
2. **Scheduled Task**: Configure automatic downloads in Dashboard → Scheduled Tasks
3. **API** (admin token): `POST /ThemeSongs/DownloadTVShows[?forceDownload=true]` (202, runs in the background; 409 if running), `POST /ThemeSongs/MoveToStorage`, `GET /ThemeSongs/Status`, `GET|POST|DELETE /ThemeSongs/Series/{id}/Theme`

## 🎯 How It Works

1. **Library Scan**: Plugin scans your TV show library for series without theme songs
2. **Provider Search**: Searches configured providers in priority order using TVDb IDs
3. **Download & Process**: Downloads found theme songs to temporary cache
4. **Audio Processing**: Optionally normalizes audio using FFmpeg
5. **Final Placement**: Moves processed files to series directories as `theme.mp3`





## 🔧 Development

### Build from Source
```sh
# Clone the repository
git clone https://github.com/geejayjay/jellyfin-plugin-themesongs.git
cd jellyfin-plugin-themesongs

# Restore dependencies
dotnet restore

# Build
dotnet build --configuration Release

# Create release package
dotnet publish --configuration Release --output bin
```

### Installation from Build
Place the resulting `Jellyfin.Plugin.ThemeSongs.dll` file in:
- **Windows**: `%ProgramData%\Jellyfin\Server\plugins\Theme Songs\`
- **Linux**: `/var/lib/jellyfin/plugins/Theme Songs/`
- **Docker**: `/config/plugins/Theme Songs/`



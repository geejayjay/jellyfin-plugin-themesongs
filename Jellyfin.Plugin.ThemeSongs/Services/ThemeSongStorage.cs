using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    /// <summary>
    /// Puts a finished theme song in place.
    ///  - No storage folder set: theme.mp3 in the series folder.
    ///  - Storage folder set: the real file is &lt;storage&gt;/&lt;series id&gt;.mp3 and the series folder
    ///    gets a theme.mp3 symlink to it, so playback reads the server's own (SSD) disk.
    /// Every step writes a temporary file next to the target and renames it, so Jellyfin never
    /// sees a half-written theme.
    /// </summary>
    public class ThemeSongStorage
    {
        public const string ThemeFileName = "theme.mp3";
        private readonly ILogger<ThemeSongStorage> _logger;

        public ThemeSongStorage(ILogger<ThemeSongStorage> logger)
        {
            _logger = logger;
        }

        public static string StoragePath =>
            (Plugin.Instance?.Configuration?.ThemeStoragePath ?? string.Empty).Trim();

        public static string LinkPath(Series series) => Path.Combine(series.Path, ThemeFileName);

        public static string StoredPath(Series series) =>
            string.IsNullOrEmpty(StoragePath) ? null : Path.Combine(StoragePath, series.Id.ToString("N") + ".mp3");

        /// <summary>The theme.mp3 in the series folder, or null.</summary>
        public static FileSystemInfo Existing(Series series)
        {
            var info = new FileInfo(LinkPath(series));
            return info.Exists || info.LinkTarget != null ? info : null;
        }

        public async Task InstallAsync(Series series, string sourceFile, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(series.Path) || !Directory.Exists(series.Path))
            {
                throw new DirectoryNotFoundException($"series folder not found: {series.Path}");
            }

            string link = LinkPath(series);
            string stored = StoredPath(series);
            if (stored == null)
            {
                await CopyAtomicAsync(sourceFile, link, ct).ConfigureAwait(false);   // replaces a file or a symlink
                return;
            }

            Directory.CreateDirectory(StoragePath);
            await CopyAtomicAsync(sourceFile, stored, ct).ConfigureAwait(false);
            PointLinkAt(link, stored);
        }

        /// <summary>
        /// Moves an existing real theme.mp3 from the series folder into the storage folder and
        /// leaves a symlink behind. Returns false when there is nothing to move.
        /// </summary>
        public async Task<bool> MoveToStorageAsync(Series series, CancellationToken ct)
        {
            string stored = StoredPath(series);
            var info = new FileInfo(LinkPath(series));
            if (stored == null || !info.Exists || info.LinkTarget != null)
            {
                return false;
            }

            Directory.CreateDirectory(StoragePath);
            await CopyAtomicAsync(info.FullName, stored, ct).ConfigureAwait(false);
            if (new FileInfo(stored).Length != info.Length)
            {
                File.Delete(stored);
                throw new IOException($"copy of {info.FullName} has the wrong size");
            }

            PointLinkAt(info.FullName, stored);
            return true;
        }

        /// <summary>Deletes theme.mp3 (and the stored file it points to, if it's one of ours).</summary>
        public void Delete(Series series)
        {
            var info = new FileInfo(LinkPath(series));
            string target = info.LinkTarget;
            if (info.Exists || target != null)
            {
                info.Delete();
            }

            if (target != null && !string.IsNullOrEmpty(StoragePath) &&
                Path.GetFullPath(target).StartsWith(Path.GetFullPath(StoragePath) + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                File.Exists(target))
            {
                File.Delete(target);
            }
        }

        private void PointLinkAt(string link, string target)
        {
            var current = new FileInfo(link);
            if (current.LinkTarget == target)
            {
                return;
            }

            string tmp = link + ".tmp-link";
            if (File.Exists(tmp) || new FileInfo(tmp).LinkTarget != null)
            {
                File.Delete(tmp);
            }

            File.CreateSymbolicLink(tmp, target);
            File.Move(tmp, link, true);   // rename(2): atomic, replaces the old file/link
            _logger.LogDebug("{Link} -> {Target}", link, target);
        }

        private static async Task CopyAtomicAsync(string source, string target, CancellationToken ct)
        {
            string tmp = target + ".tmp";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                File.Move(tmp, target, true);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
        }
    }
}

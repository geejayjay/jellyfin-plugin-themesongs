using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    public static class ThemeStatus
    {
        /// <summary>Jellyfin already had a theme for the series (found in its database, not by reading the disks).</summary>
        public const string Existing = "existing";
        public const string Downloaded = "downloaded";
        public const string Uploaded = "uploaded";
        /// <summary>No provider had a theme; asked again after RetryNotFoundDays.</summary>
        public const string NotFound = "not-found";
        public const string Failed = "failed";
        /// <summary>Deleted by the user: never downloaded again automatically (upload or force to bring it back).</summary>
        public const string Deleted = "deleted";
    }

    public class ThemeSongEntry
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Source { get; set; }
        public DateTime Checked { get; set; }
    }

    /// <summary>
    /// Small JSON file in the plugin's data folder (on the server's own disk) that remembers
    /// what was done for every series, so the daily run never has to look at the media disks
    /// and doesn't ask the providers again every day for themes they don't have.
    /// </summary>
    public class ThemeSongState
    {
        private readonly object _lock = new();
        private readonly ILogger<ThemeSongState> _logger;
        private Dictionary<string, ThemeSongEntry> _entries;

        public ThemeSongState(ILogger<ThemeSongState> logger)
        {
            _logger = logger;
        }

        private static string FilePath =>
            Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "state.json");

        private Dictionary<string, ThemeSongEntry> Entries
        {
            get
            {
                if (_entries != null)
                {
                    return _entries;
                }

                try
                {
                    _entries = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<Dictionary<string, ThemeSongEntry>>(File.ReadAllText(FilePath))
                        : null;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    _logger.LogWarning("Could not read {Path}, starting fresh: {Message}", FilePath, ex.Message);
                }

                _entries ??= new Dictionary<string, ThemeSongEntry>();
                return _entries;
            }
        }

        public ThemeSongEntry Get(Guid seriesId)
        {
            lock (_lock)
            {
                return Entries.TryGetValue(seriesId.ToString("N"), out var e) ? e : null;
            }
        }

        public void Set(Guid seriesId, string name, string status, string source = null)
        {
            lock (_lock)
            {
                Entries[seriesId.ToString("N")] = new ThemeSongEntry
                {
                    Name = name,
                    Status = status,
                    Source = source,
                    Checked = DateTime.UtcNow
                };
            }
        }

        public Dictionary<string, int> Counts()
        {
            lock (_lock)
            {
                var counts = new Dictionary<string, int>();
                foreach (var e in Entries.Values)
                {
                    counts[e.Status] = counts.GetValueOrDefault(e.Status) + 1;
                }

                return counts;
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, FilePath, true);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning("Could not save {Path}: {Message}", FilePath, ex.Message);
                }
            }
        }
    }
}

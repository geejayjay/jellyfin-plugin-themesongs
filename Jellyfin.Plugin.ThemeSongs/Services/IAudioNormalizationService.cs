using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    public interface IAudioNormalizationService
    {
        /// <summary>
        /// Returns the path of the processed file (inside <paramref name="workDir"/>), or the input
        /// path unchanged when normalization is turned off. Throws when ffmpeg fails.
        /// </summary>
        Task<string> ProcessAsync(string inputFilePath, string workDir, CancellationToken cancellationToken);
    }
}

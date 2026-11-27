using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    /// <summary>
    /// HTTP access through Jellyfin's own HttpClient factory (normal certificate checks,
    /// Jellyfin's proxy settings and user agent).
    /// </summary>
    public class HttpClientService : IHttpClientService
    {
        /// <summary>Largest theme song accepted (a theme is usually 0.5–3 MB).</summary>
        public const long MaxDownloadBytes = 20L * 1024 * 1024;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<HttpClientService> _logger;

        public HttpClientService(IHttpClientFactory httpClientFactory, ILogger<HttpClientService> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private HttpClient Client() => _httpClientFactory.CreateClient(NamedClient.Default);

        private static CancellationTokenSource Timeout(CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            return cts;
        }

        public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = Timeout(cancellationToken);
                using var response = await Client().GetAsync(url, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                }

                _logger.LogDebug("GET {Url} returned {StatusCode}", url, response.StatusCode);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("GET {Url} failed: {Message}", url, ex.Message);
                return null;
            }
        }

        public async Task<bool> HeadRequestAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = Timeout(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await Client().SendAsync(request, cts.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("HEAD {Url} failed: {Message}", url, ex.Message);
                return false;
            }
        }

        public async Task<bool> DownloadFileAsync(string url, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = Timeout(cancellationToken);
                using var response = await Client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Download {Url} returned {StatusCode}", url, response.StatusCode);
                    return false;
                }

                var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Download {Url} is {ContentType}, not audio - skipped", url, type);
                    return false;
                }

                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                {
                    _logger.LogWarning("Download {Url} is too large ({Bytes} bytes) - skipped", url, response.Content.Headers.ContentLength);
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                await using (var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                await using (var target = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxDownloadBytes)
                        {
                            throw new InvalidDataException($"download larger than {MaxDownloadBytes} bytes");
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                    }
                }

                if (!AudioFile.LooksLikeMp3(filePath))
                {
                    _logger.LogWarning("Download {Url} is not an MP3 file - skipped", url);
                    File.Delete(filePath);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Download {Url} failed: {Message}", url, ex.Message);
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException)
                {
                }

                return false;
            }
        }
    }
}

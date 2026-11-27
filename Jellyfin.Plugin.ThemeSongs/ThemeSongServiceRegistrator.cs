using System.IO;
using Jellyfin.Plugin.ThemeSongs.Providers;
using Jellyfin.Plugin.ThemeSongs.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeSongs
{
    /// <summary>Registers the Theme Songs services.</summary>
    public class ThemeSongServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<IHttpClientService, HttpClientService>();
            serviceCollection.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();
            serviceCollection.AddSingleton<ThemeSongState>();
            serviceCollection.AddSingleton<ThemeSongStorage>();

            serviceCollection.AddSingleton<VideoAudioDownloader>();

            // Provider *types*; which sources are used (and their URLs) is configured on the settings page.
            serviceCollection.AddSingleton<IThemeSongProvider, UrlTemplateProvider>();
            serviceCollection.AddSingleton<IThemeSongProvider, TelevisionTunesProvider>();
            serviceCollection.AddSingleton<IThemeSongProvider, ThemerrDbProvider>();

            serviceCollection.AddSingleton<IThemeSongDownloadService>(sp =>
            {
                var serverPaths = sp.GetRequiredService<IServerApplicationPaths>();
                return new ThemeSongDownloadService(
                    sp.GetRequiredService<ILibraryManager>(),
                    sp.GetRequiredService<IProviderManager>(),
                    sp.GetRequiredService<IFileSystem>(),
                    sp.GetRequiredService<IHttpClientService>(),
                    sp.GetRequiredService<IAudioNormalizationService>(),
                    sp.GetServices<IThemeSongProvider>(),
                    sp.GetRequiredService<ThemeSongState>(),
                    sp.GetRequiredService<ThemeSongStorage>(),
                    sp.GetRequiredService<VideoAudioDownloader>(),
                    sp.GetRequiredService<ILogger<ThemeSongDownloadService>>(),
                    Path.Combine(serverPaths.CachePath, "ThemeSongs"));
            });
        }
    }
}

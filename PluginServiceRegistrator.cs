using Jellyfin.Plugin.SubtitleCat.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleCat
{
    /// <summary>
    /// Registers <see cref="SubtitleCatProvider"/> with Jellyfin's DI container
    /// so it shows up as a selectable subtitle source under
    /// Dashboard -&gt; Libraries -&gt; (a library) -&gt; Subtitles.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<ISubtitleProvider, SubtitleCatProvider>();
        }
    }
}

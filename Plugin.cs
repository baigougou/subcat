using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleCat.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleCat
{
    /// <summary>
    /// SubtitleCat plugin entry point.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of <see cref="IApplicationPaths"/>.</param>
        /// <param name="xmlSerializer">Instance of <see cref="IXmlSerializer"/>.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <inheritdoc />
        public override string Name => "SubtitleCat";

        /// <inheritdoc />
        public override string Description => "Search and download subtitles from subtitlecat.com.";

        /// <inheritdoc />
        // Fixed GUID for this plugin. Do not change once published, or existing
        // installs/configuration will be treated as a different plugin.
        public override Guid Id => Guid.Parse("f9a1f5c2-6b1e-4b7a-9b7a-1c9a2f7e2b31");

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
            };
        }
    }
}

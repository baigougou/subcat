using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleCat.Configuration
{
    /// <summary>
    /// Configuration for the SubtitleCat plugin.
    /// SubtitleCat needs no account/API key, so this is intentionally small.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the maximum number of candidate titles (search result rows)
        /// to open and inspect on subtitlecat.com per search. Keep this low to avoid
        /// hammering the upstream site; raise it only if you're missing matches for
        /// titles with ambiguous names.
        /// </summary>
        public int MaxCandidates { get; set; } = 5;

        /// <summary>
        /// Gets or sets the per-request timeout, in seconds, for calls to subtitlecat.com.
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 15;
    }
}

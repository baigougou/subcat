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

        /// <summary>
        /// Gets or sets how many subtitle rows may be offered per language.
        /// Jellyfin files every download as "&lt;video&gt;.&lt;language&gt;.srt" and
        /// appends .0/.1/.2 when that name is taken rather than overwriting, so
        /// offering several rows that all resolve to the same three-letter
        /// language is what litters a media folder with duplicates. 1 keeps only
        /// the best-scoring row per language; raise it to have alternatives to
        /// pick from by hand.
        /// </summary>
        public int MaxPerLanguage { get; set; } = 1;

        /// <summary>
        /// Gets or sets a value indicating whether rows the site's users have
        /// rated down ("Rated bad by users") are discarded before ranking.
        /// </summary>
        public bool ExcludeBadlyRated { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether a result whose *source*
        /// language is the language being requested is preferred over one that
        /// was machine-translated into it.
        ///
        /// subtitlecat labels every search row with the language it was
        /// translated from. A row sourced from the requested language is the
        /// human-made original; a row sourced from any other language has been
        /// run through a translator, which is what leaves the &lt;b&gt; markup
        /// and half-translated lines behind. Rows with no label are unaffected,
        /// and a title that has no original in the requested language still
        /// falls back to its best translation.
        /// </summary>
        public bool PreferRequestedLanguageAsSource { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether a result row must share at
        /// least one distinctive word with the query in order to be considered.
        /// This is what keeps the site's fallback matches - e.g. a
        /// Chinese-titled anime returning 115 unrelated 1979 films because the
        /// query contained "1979" - from being handed to Jellyfin. Turn it off
        /// to see subtitlecat's raw result list.
        /// </summary>
        public bool RequireTitleTokenMatch { get; set; } = true;
    }
}

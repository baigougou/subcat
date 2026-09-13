namespace Jellyfin.Plugin.SubtitleCat.Providers
{
    /// <summary>
    /// One row from a subtitlecat.com search-results page: a single
    /// uploaded "title" (which itself may have subtitles in many languages).
    /// </summary>
    internal sealed class TitleSearchResult
    {
        public TitleSearchResult(string detailUrl, string title)
        {
            DetailUrl = detailUrl;
            Title = title;
        }

        public string DetailUrl { get; }

        public string Title { get; }
    }

    /// <summary>
    /// One language row from a subtitlecat.com title-detail page.
    /// </summary>
    internal sealed class LanguageEntry
    {
        public LanguageEntry(string code, string displayName, string? downloadUrl)
        {
            Code = code;
            DisplayName = displayName;
            DownloadUrl = downloadUrl;
        }

        /// <summary>Gets the subtitlecat language code (the flag &lt;img&gt; alt text, e.g. "zh-CN", "en", "iw").</summary>
        public string Code { get; }

        public string DisplayName { get; }

        /// <summary>Gets the absolute .srt download URL, or null if this language has no subtitle yet (only a "Translate" placeholder).</summary>
        public string? DownloadUrl { get; }

        public bool HasSubtitle => !string.IsNullOrEmpty(DownloadUrl);
    }
}

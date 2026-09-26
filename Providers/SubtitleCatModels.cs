namespace Jellyfin.Plugin.SubtitleCat.Providers
{
    /// <summary>
    /// The thumbs-up/down badge subtitlecat.com renders next to a search-result
    /// title once users have voted on it. Most rows have no votes at all.
    /// </summary>
    internal enum UserRating
    {
        /// <summary>No votes yet - the badge cell is empty.</summary>
        None,

        /// <summary>"Rated bad by users".</summary>
        Bad,

        /// <summary>"Rated good by users".</summary>
        Good,
    }

    /// <summary>
    /// One row from a subtitlecat.com search-results page: a single
    /// uploaded "title" (which itself may have subtitles in many languages).
    ///
    /// The quality signals (rating / downloads / size / language count) all
    /// live in the same row as the link, so they cost nothing extra to read -
    /// they only require looking at the row instead of just the anchor.
    /// </summary>
    internal sealed class TitleSearchResult
    {
        public TitleSearchResult(
            string detailUrl,
            string title,
            UserRating rating = UserRating.None,
            int? downloads = null,
            long? sizeBytes = null,
            int? languageCount = null)
        {
            DetailUrl = detailUrl;
            Title = title;
            Rating = rating;
            Downloads = downloads;
            SizeBytes = sizeBytes;
            LanguageCount = languageCount;
        }

        public string DetailUrl { get; }

        public string Title { get; }

        /// <summary>Gets the site's user-rating badge for this row, if any.</summary>
        public UserRating Rating { get; }

        /// <summary>Gets the row's download counter, or null when the site didn't render one.</summary>
        public int? Downloads { get; }

        /// <summary>Gets the row's subtitle file size in bytes, or null when unparseable.</summary>
        public long? SizeBytes { get; }

        /// <summary>Gets how many languages this title offers, or null when unparseable.</summary>
        public int? LanguageCount { get; }

        public bool IsRatedGood => Rating == UserRating.Good;

        public bool IsRatedBad => Rating == UserRating.Bad;
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

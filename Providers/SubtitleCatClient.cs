using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Providers
{
    /// <summary>
    /// Thin scraping client for subtitlecat.com. There is no public API, so
    /// this parses the same two page types a human browser would hit:
    /// the search-results page and a title's detail page.
    ///
    /// The site's markup isn't guaranteed to stay stable - if subtitlecat
    /// changes its templates this is the only file that should need updates.
    /// </summary>
    internal sealed class SubtitleCatClient
    {
        private const string BaseUrl = "https://www.subtitlecat.com";

        /// <summary>
        /// The first integer in a metric cell. The cell's text is e.g.
        /// "13 downloads" / "13 languages" because the unit is a nested
        /// &lt;span&gt; whose InnerText is concatenated by HtmlAgilityPack.
        /// </summary>
        private static readonly Regex MetricNumber = new(@"(\d+)", RegexOptions.Compiled);

        /// <summary>Matches the "65 KB" / "1.2 MB" form of the Size cell.</summary>
        private static readonly Regex SizePattern = new(
            @"([\d.]+)\s*(TB|GB|MB|KB|B)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Matches the source-language label subtitlecat prints after a row's
        /// title, e.g. "(translated from Chinese)". Note that this text sits
        /// in the surrounding &lt;td&gt;, *outside* the &lt;a&gt; element.
        /// </summary>
        private static readonly Regex SourceLanguagePattern = new(
            @"\(\s*translated\s+from\s+(?<lang>[^)]+?)\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public SubtitleCatClient(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Runs a title search and returns the candidate rows, in the order
        /// subtitlecat returned them (which is roughly relevance/recency).
        ///
        /// Each row also carries the site's own quality signals - a
        /// thumbs-up/down user rating, a download counter, the subtitle file
        /// size, how many languages the title offers, and the language the row
        /// was translated *from*. They live in the same &lt;tr&gt; as the link,
        /// so reading them costs no extra request; they're what lets the
        /// provider rank "the same film in three uploads" instead of trusting
        /// the site's row order.
        /// </summary>
        public async Task<IReadOnlyList<TitleSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var url = $"{BaseUrl}/index.php?search={Uri.EscapeDataString(query)}";
            var html = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                return Array.Empty<TitleSearchResult>();
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<TitleSearchResult>();

            // NOTE: the search-results page renders its rows with *relative*
            // hrefs ("subs/1330/foo.html"), whereas the detail page uses
            // root-absolute ones ("/subs/815/foo.srt"). Matching on "subs/"
            // (no leading slash) covers both forms. Requiring "/subs/" matches
            // nothing on the search page, which makes every search silently
            // return zero candidates.
            var links = doc.DocumentNode.SelectNodes("//a[contains(@href,'subs/') and contains(@href,'.html')]");
            if (links is null)
            {
                _logger.LogWarning(
                    "subtitlecat.com search page for {Url} contained no subtitle links - the site markup may have changed.",
                    url);
                return results;
            }

            foreach (var link in links)
            {
                var href = link.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrEmpty(href) || href.Contains("list_all.php", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = HtmlEntity.DeEntitize(link.InnerText)?.Trim();
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }

                var absolute = ResolveUrl(href);
                if (results.Any(r => string.Equals(r.DetailUrl, absolute, StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // dedupe (the same row can appear more than once in the table markup)
                }

                var (downloads, sizeBytes, languageCount) = ParseRowMetrics(link);

                results.Add(new TitleSearchResult(
                    absolute,
                    title,
                    ParseRating(link),
                    downloads,
                    sizeBytes,
                    languageCount,
                    ParseSourceLanguage(link)));
            }

            return results;
        }

        /// <summary>
        /// Reads the part of a row the picker cares about. The search page
        /// is a plain table:
        ///   &lt;tr&gt;
        ///     &lt;td&gt;&lt;a href="subs/949/x.html"&gt;Title&lt;/a&gt; (translated from Chinese)&lt;/td&gt;
        ///     &lt;td class="sub-table__stars"&gt;&amp;nbsp;|&lt;span title="Rated good by users"&gt;..&lt;/span&gt;&lt;/td&gt;
        ///     &lt;td class="sub-table__metric"&gt;...Size...&lt;/td&gt;
        ///     &lt;td class="sub-table__metric"&gt;...Downloads...&lt;/td&gt;
        ///     &lt;td class="sub-table__metric"&gt;...Languages...&lt;/td&gt;
        ///   &lt;/tr&gt;
        /// Absolutely nothing here is required for a row to be usable - a
        /// markup change must degrade ranking, not break searching, hence
        /// the null defaults.
        /// </summary>
        private static (int? Downloads, long? SizeBytes, int? LanguageCount) ParseRowMetrics(HtmlNode link)
        {
            int? downloads = null;
            long? sizeBytes = null;
            int? languageCount = null;

            var row = link.Ancestors("tr").FirstOrDefault();
            if (row is null)
            {
                return (downloads, sizeBytes, languageCount);
            }

            var metrics = row.SelectNodes(".//td[contains(@class,'sub-table__metric')]");
            if (metrics is null)
            {
                return (downloads, sizeBytes, languageCount);
            }

            foreach (var metric in metrics)
            {
                var label = metric.SelectSingleNode(".//span[contains(@class,'sub-table__metric-label')]")?.InnerText
                            ?? string.Empty;
                var value = metric.SelectSingleNode(".//span[contains(@class,'sub-table__metric-value')]")?.InnerText
                            ?? string.Empty;

                if (label.Contains("Size", StringComparison.OrdinalIgnoreCase))
                {
                    sizeBytes = ParseSize(value);
                }
                else if (label.Contains("Download", StringComparison.OrdinalIgnoreCase))
                {
                    downloads = ParseLeadingInt(value);
                }
                else if (label.Contains("Language", StringComparison.OrdinalIgnoreCase))
                {
                    languageCount = ParseLeadingInt(value);
                }
            }

            return (downloads, sizeBytes, languageCount);
        }

        /// <summary>
        /// Reads the language a row was translated from, e.g.
        ///   &lt;td&gt;&lt;a href="subs/1029/x.html"&gt;x&lt;/a&gt; (translated from Chinese)&lt;/td&gt;
        ///
        /// The label is a bare text node *outside* the anchor, so it is
        /// invisible to link.InnerText - which is exactly why the original
        /// ranking could not tell a Chinese original from an English subtitle
        /// that had been machine-translated into Chinese. Reading the
        /// enclosing cell instead makes the distinction available for free.
        ///
        /// Returns null when the row carries no such label; callers must treat
        /// null as "unknown", not as "different language".
        /// </summary>
        private static string? ParseSourceLanguage(HtmlNode link)
        {
            // The <td> that holds the anchor; the label is a sibling text node.
            var cell = link.ParentNode;
            if (cell is null)
            {
                return null;
            }

            var text = HtmlEntity.DeEntitize(cell.InnerText);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = SourceLanguagePattern.Match(text);
            return match.Success ? match.Groups["lang"].Value.Trim() : null;
        }

        /// <summary>
        /// Matches the rating badge on the "Rated good by users" title
        /// attribute rather than the glyph, so swapping the emoji (or the
        /// underlying icon) doesn't silently turn every rated row into an
        /// unrated one.
        /// </summary>
        private static UserRating ParseRating(HtmlNode link)
        {
            var row = link.Ancestors("tr").FirstOrDefault();
            var badge = row?.SelectSingleNode(".//td[contains(@class,'sub-table__stars')]//*[@title]");
            var tooltip = badge?.GetAttributeValue("title", string.Empty) ?? string.Empty;

            if (tooltip.Contains("good", StringComparison.OrdinalIgnoreCase))
            {
                return UserRating.Good;
            }

            if (tooltip.Contains("bad", StringComparison.OrdinalIgnoreCase))
            {
                return UserRating.Bad;
            }

            return UserRating.None;
        }

        private static int? ParseLeadingInt(string value)
        {
            var match = MetricNumber.Match(value);
            return match.Success
                   && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static long? ParseSize(string value)
        {
            var match = SizePattern.Match(value);
            if (!match.Success
                || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                return null;
            }

            var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
            {
                "TB" => 1024L * 1024 * 1024 * 1024,
                "GB" => 1024L * 1024 * 1024,
                "MB" => 1024L * 1024,
                "KB" => 1024L,
                _ => 1L,
            };

            return (long)(amount * multiplier);
        }

        /// <summary>
        /// Loads a title's detail page and returns every language row found on it.
        /// </summary>
        public async Task<IReadOnlyList<LanguageEntry>> GetLanguagesAsync(string detailUrl, CancellationToken cancellationToken)
        {
            var html = await GetStringAsync(detailUrl, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                return Array.Empty<LanguageEntry>();
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var entries = new List<LanguageEntry>();

            // Each language row is anchored by its flag <img>. The language's
            // display name and (if available) a "Download" link to a .srt
            // file follow it in document order, before the *next* flag image.
            var flags = doc.DocumentNode.SelectNodes("//img[contains(@src,'/flags/')]");
            if (flags is null)
            {
                return entries;
            }

            foreach (var flag in flags)
            {
                var code = flag.GetAttributeValue("alt", string.Empty).Trim();
                if (string.IsNullOrEmpty(code))
                {
                    continue;
                }

                string displayName = string.Empty;
                string? downloadUrl = null;

                // Look at the next handful of nodes in document order. Stop
                // early if we hit another flag image - that means we've
                // walked into the next language's row without finding a
                // download link, i.e. this language is "Translate"-only.
                // "node()" (not "*") so this also picks up the plain text
                // nodes carrying the language's display name - "*" alone
                // would only match elements and skip text entirely.
                var following = flag.SelectNodes("following::node()[position()<=20]");
                if (following != null)
                {
                    foreach (var node in following)
                    {
                        if (string.Equals(node.Name, "img", StringComparison.OrdinalIgnoreCase)
                            && node.GetAttributeValue("src", string.Empty).Contains("/flags/", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        if (string.Equals(node.Name, "a", StringComparison.OrdinalIgnoreCase))
                        {
                            var href = node.GetAttributeValue("href", string.Empty);
                            if (href.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = ResolveUrl(href);
                                break;
                            }

                            // "javascript:vote(...)" thumbs-up/down links - ignore.
                            continue;
                        }

                        if (string.IsNullOrEmpty(displayName) && string.Equals(node.Name, "#text", StringComparison.OrdinalIgnoreCase))
                        {
                            var text = HtmlEntity.DeEntitize(node.InnerText)?.Trim();
                            if (!string.IsNullOrEmpty(text) && !string.Equals(text, "Translate", StringComparison.OrdinalIgnoreCase))
                            {
                                displayName = text;
                            }
                        }
                    }
                }

                entries.Add(new LanguageEntry(code, string.IsNullOrEmpty(displayName) ? code : displayName, downloadUrl));
            }

            return entries;
        }

        /// <summary>
        /// Downloads the raw bytes of a .srt file.
        /// </summary>
        public async Task<byte[]?> DownloadSubtitleAsync(string absoluteSrtUrl, CancellationToken cancellationToken)
        {
            // Defence in depth: a non-http URL reaching this point means URL
            // resolution went wrong upstream (see ResolveUrl). Bail out with a
            // readable warning instead of letting HttpClient throw
            // NotSupportedException from deep inside its handler stack.
            if (!absoluteSrtUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !absoluteSrtUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "subtitlecat.com: refusing to download non-http subtitle url {Url}",
                    absoluteSrtUrl);
                return null;
            }

            using var response = await SendAsync(absoluteSrtUrl, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            // Guard against the "download" link silently 200'ing an HTML
            // error/login page instead of a real .srt payload.
            if (LooksLikeHtml(bytes))
            {
                _logger.LogWarning("subtitlecat.com returned HTML instead of a subtitle for {Url}", absoluteSrtUrl);
                return null;
            }

            return bytes;
        }

        private static bool LooksLikeHtml(byte[] bytes)
        {
            var probeLength = Math.Min(bytes.Length, 256);
            var probe = Encoding.UTF8.GetString(bytes, 0, probeLength).TrimStart();
            return probe.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                   || probe.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveUrl(string href)
        {
            // NOTE: an href from the *detail* page is root-absolute
            // ("/subs/766/foo.zh-zh-CN.srt"). On Unix, Uri.TryCreate with
            // UriKind.Absolute *succeeds* for such a path and reports it as a
            // local file URI - "file:///subs/766/foo.zh-zh-CN.srt" - so trusting
            // any "absolute" URI here silently rewrites a web path into a
            // local one and the download dies with:
            //   System.NotSupportedException: The 'file' scheme is not supported.
            // Only a genuine http(s) URL may be returned as-is; everything else
            // is joined onto the site root by hand.
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            {
                if (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
                {
                    return absolute.ToString();
                }

                if (!href.StartsWith('/'))
                {
                    // e.g. "javascript:vote(...)" or "mailto:..." - nothing we can fetch.
                    return href;
                }
            }

            if (href.StartsWith("//", StringComparison.Ordinal))
            {
                // Protocol-relative: inherit the site's own scheme.
                return "https:" + href;
            }

            return href.StartsWith('/') ? BaseUrl + href : BaseUrl + "/" + href;
        }

        private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Without this the caller only sees "0 candidates" and has no
                // way to tell a blocked/error response from an empty result set.
                _logger.LogWarning(
                    "subtitlecat.com returned HTTP {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // A plain UA - the goal is just to look like a normal browser
                // fetch, not to spoof anything specific.
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 Jellyfin-SubtitleCat-Plugin");

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Request to subtitlecat.com failed for {Url}", url);
                return null;
            }
        }
    }
}

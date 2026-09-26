using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleCat.Configuration;
using Jellyfin.Plugin.SubtitleCat.Util;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleCat.Providers
{
    /// <summary>
    /// Subtitle provider backed by subtitlecat.com.
    ///
    /// NOTE for anyone building this against a different Jellyfin server
    /// version than the one pinned in the .csproj: if this file fails to
    /// compile because a property below doesn't exist on
    /// SubtitleSearchRequest / RemoteSubtitleInfo / SubtitleResponse, that
    /// almost always means the SDK version moved a field name slightly -
    /// let your IDE's autocomplete show you the current member names on
    /// those three types and adjust accordingly. The scraping logic in
    /// SubtitleCatClient does not depend on the Jellyfin SDK at all and
    /// should not need any changes.
    /// </summary>
    public class SubtitleCatProvider : ISubtitleProvider
    {
        /// <summary>
        /// A standalone 4-digit year. The lookarounds keep it from matching
        /// inside a longer number, so the "1080" of "1080p" is not a year.
        /// </summary>
        private static readonly Regex YearPattern = new(
            @"(?<!\d)(18\d{2}|19\d{2}|20\d{2})(?!\d)",
            RegexOptions.Compiled);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SubtitleCatProvider> _logger;

        public SubtitleCatProvider(IHttpClientFactory httpClientFactory, ILogger<SubtitleCatProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "SubtitleCat";

        /// <inheritdoc />
        public IEnumerable<VideoContentType> SupportedMediaTypes { get; } =
            new[] { VideoContentType.Movie, VideoContentType.Episode };

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "SubtitleCat: Search called. Name={Name}, SeriesName={SeriesName}, Season={Season}, Episode={Episode}, Year={Year}, Language={Language}",
                request.Name,
                request.SeriesName,
                request.ParentIndexNumber,
                request.IndexNumber,
                request.ProductionYear,
                request.Language);

            var queries = SearchQueryBuilder.Create(
                request.Name,
                request.MediaPath,
                request.SeriesName,
                request.ParentIndexNumber,
                request.IndexNumber,
                request.ProductionYear);

            var mediaCode = MediaCodeExtractor.Extract(request.MediaPath) ?? MediaCodeExtractor.Extract(request.Name);
            _logger.LogInformation(
                "SubtitleCat: extracted media code = {MediaCode}; query plan = {Queries}",
                mediaCode ?? "<none>",
                string.Join(" | ", queries));

            if (queries.Count == 0)
            {
                _logger.LogWarning("SubtitleCat: Search returned no query.");
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var client = CreateClient();
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var maxCandidates = Math.Max(1, config.MaxCandidates);
            var maxPerLanguage = Math.Max(1, config.MaxPerLanguage);
            var results = new List<RemoteSubtitleInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var perLanguage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                for (var queryIndex = 0; queryIndex < queries.Count; queryIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var query = queries[queryIndex];
                    var isCodeQuery = mediaCode != null && string.Equals(query, mediaCode, StringComparison.OrdinalIgnoreCase);

                    _logger.LogInformation(
                        "SubtitleCat: Search query #{Index}/{Total} ({Type}) = \"{Query}\"",
                        queryIndex + 1,
                        queries.Count,
                        isCodeQuery ? "MEDIA_CODE" : "TITLE",
                        query);

                    var candidates = await client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "SubtitleCat: SearchAsync returned {Count} candidates for \"{Query}\"",
                        candidates.Count,
                        query);

                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    var ranked = RankCandidates(candidates, query, mediaCode, request.ProductionYear, request.Language, config)
                        .Take(maxCandidates)
                        .ToList();

                    if (ranked.Count == 0)
                    {
                        // The site answered with rows, but not one of them shares
                        // a distinctive word with what we asked for. subtitlecat
                        // degrades to matching *something* - in practice the year -
                        // and will happily return 115 films from 1979 for a query
                        // that was really the Chinese title of an anime movie.
                        // Handing the first of those to Jellyfin is how an
                        // unrelated 1979 film ends up as the subtitle track.
                        _logger.LogWarning(
                            "SubtitleCat: subtitlecat.com returned {Count} row(s) for \"{Query}\" but none of them matched the title; ignoring them (the site most likely matched only the year).",
                            candidates.Count,
                            query);
                        continue;
                    }

                    var queryResultCountBefore = results.Count;

                    foreach (var candidate in ranked)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var candidateCode = MediaCodeExtractor.Extract(candidate.Title);
                        var isExactCodeMatch = !string.IsNullOrWhiteSpace(mediaCode)
                            && string.Equals(candidateCode, mediaCode, StringComparison.OrdinalIgnoreCase);

                        _logger.LogDebug(
                            "SubtitleCat: candidate title=\"{Title}\", url={Url}, code={Code}",
                            candidate.Title,
                            candidate.DetailUrl,
                            candidateCode ?? "<none>");

                        IReadOnlyList<LanguageEntry> languages;
                        try
                        {
                            languages = await client.GetLanguagesAsync(candidate.DetailUrl, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                        {
                            _logger.LogWarning(ex, "SubtitleCat: failed to load detail page {Url}", candidate.DetailUrl);
                            continue;
                        }

                        _logger.LogDebug(
                            "SubtitleCat: detail {Url} offered {Count} language(s) with a downloadable subtitle: {Languages}",
                            candidate.DetailUrl,
                            languages.Count(l => l.HasSubtitle),
                            string.Join(", ", languages.Where(l => l.HasSubtitle).Select(l => l.Code)));

                        foreach (var lang in languages)
                        {
                            if (!lang.HasSubtitle)
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(request.Language) && !LanguageMap.Matches(lang.Code, request.Language))
                            {
                                continue;
                            }

                            var threeLetter = LanguageMap.ToThreeLetter(lang.Code) ?? request.Language ?? lang.Code;
                            var token = EncodeToken(lang.DownloadUrl!, threeLetter);

                            if (!seen.Add(token))
                            {
                                continue;
                            }

                            // Jellyfin never overwrites a subtitle: it saves as
                            // "<video>.<language>.srt" and falls back to .0/.1/.2
                            // when that exists (SubtitleManager.TrySaveToFiles).
                            // Handing it five rows that all resolve to "zho" is
                            // therefore what produced ABW-276.zho.0.srt through
                            // .zho.3.srt next to the video. Offer only the
                            // best-ranked few per language.
                            perLanguage.TryGetValue(threeLetter, out var alreadyOffered);
                            if (alreadyOffered >= maxPerLanguage)
                            {
                                continue;
                            }

                            perLanguage[threeLetter] = alreadyOffered + 1;

                            results.Add(new RemoteSubtitleInfo
                            {
                                Id = token,
                                ProviderName = Name,
                                // The bare page title carries no language information, and one
                                // title usually has several languages on offer - without the
                                // language the picker shows indistinguishable duplicates
                                // (e.g. two identical rows for zh-CN and zh-TW).
                                Name = BuildDisplayName(candidate, lang),
                                Format = "srt",
                                ThreeLetterISOLanguageName = threeLetter,
                                IsHashMatch = isExactCodeMatch,
                                Forced = false,
                            });
                        }

                        // Candidates are already ordered best-first, so once the
                        // requested language has been served there is nothing
                        // left to learn from opening more detail pages. Without
                        // this the task opened up to MaxCandidates pages per
                        // item - the reason "Download missing subtitles" spent
                        // four hours walking a library.
                        if (results.Count - queryResultCountBefore >= maxPerLanguage)
                        {
                            _logger.LogDebug(
                                "SubtitleCat: query #{Index} produced {Count} result(s); stopping candidate inspection.",
                                queryIndex + 1,
                                results.Count - queryResultCountBefore);
                            break;
                        }
                    }

                    var added = results.Count - queryResultCountBefore;
                    _logger.LogInformation(
                        "SubtitleCat: query #{Index} added {Added} subtitle result(s); total={TotalResults}",
                        queryIndex + 1,
                        added,
                        results.Count);

                    // The remaining queries are fallbacks for the case where
                    // this one found nothing. Once a result for the requested
                    // language is in hand they can only cost time and produce
                    // near-duplicates of the same subtitle.
                    if (added > 0 && !string.IsNullOrEmpty(request.Language))
                    {
                        _logger.LogInformation(
                            "SubtitleCat: already have a candidate for {Language}; skipping the remaining fallback queries.",
                            request.Language);
                        break;
                    }

                    // A successful exact media-code match is enough to stop.
                    // If the code search found candidates but none had the
                    // requested language, continue with the title fallback.
                    if (isCodeQuery && ranked.Any(c =>
                            string.Equals(
                                MediaCodeExtractor.Extract(c.Title),
                                mediaCode,
                                StringComparison.OrdinalIgnoreCase))
                        && added > 0)
                    {
                        _logger.LogInformation("SubtitleCat: exact media-code match found; skipping title fallback.");
                        break;
                    }
                }

                _logger.LogInformation("SubtitleCat: Search completed. Returning {Count} subtitle candidates.", results.Count);
                return results;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("SubtitleCat: Search cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleCat: Search failed.");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            _logger.LogInformation("SubtitleCat: GetSubtitles called.");

            var token = DecodeToken(id);
            _logger.LogInformation("SubtitleCat: downloading subtitle. Language={Language}, Url={Url}", token.Language, token.Url);

            var client = CreateClient();

            var bytes = await client.DownloadSubtitleAsync(token.Url, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                throw new InvalidOperationException($"SubtitleCat: could not download subtitle from {token.Url}");
            }

            _logger.LogInformation("SubtitleCat: subtitle download completed. Bytes={Bytes}", bytes.Length);

            return new SubtitleResponse
            {
                Format = "srt",
                Language = token.Language,
                Stream = new MemoryStream(bytes),
            };
        }

        private SubtitleCatClient CreateClient()
        {
            var httpClient = _httpClientFactory.CreateClient(nameof(SubtitleCatProvider));
            var timeoutSeconds = Plugin.Instance?.Configuration.RequestTimeoutSeconds ?? 15;
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
            return new SubtitleCatClient(httpClient, _logger);
        }

        /// <summary>
        /// Scores and filters the search-result rows. Three independent things
        /// matter here:
        ///
        /// * Relevance - is the row actually the title we asked for?
        ///   subtitlecat's own search is a loose bag-of-words match, so
        ///   "&lt;chinese title&gt; 1979" returns every 1979 film in the index.
        ///   A row that shares no distinctive word with the query is dropped
        ///   outright while RequireTitleTokenMatch is on; the rows that
        ///   survive are the ones the site genuinely recognised.
        /// * Original vs translation - is the row's subtitle the language the
        ///   caller asked for, or a machine translation *into* it? The site
        ///   labels every row with the language it was translated from, and
        ///   that label is the single most reliable signal on the page: a
        ///   translated row is the one that arrives full of &lt;b&gt; markup
        ///   and half-translated sentences. This is scored per row rather than
        ///   filtered, so a title with no original in the requested language
        ///   still falls back to the best translation instead of nothing.
        /// * Quality - among rows that *are* the right title, the one the site
        ///   happens to list first is not the best one. The site's own signals
        ///   (user rating, download count, file size) break that tie.
        ///
        /// The previous version did none of this: it scored on a single
        /// substring test and handed the caller the site's row order.
        /// </summary>
        private static IReadOnlyList<TitleSearchResult> RankCandidates(
            IReadOnlyList<TitleSearchResult> candidates,
            string query,
            string? mediaCode,
            int? year,
            string? requestedLanguage,
            PluginConfiguration config)
        {
            var normalizedQuery = Normalize(query);
            var queryTokens = DistinctiveTokens(normalizedQuery, year);
            var scored = new List<(TitleSearchResult Result, int Score)>(candidates.Count);

            foreach (var candidate in candidates)
            {
                if (config.ExcludeBadlyRated && candidate.IsRatedBad)
                {
                    continue;
                }

                var normalizedTitle = Normalize(candidate.Title);
                var candidateCode = MediaCodeExtractor.Extract(candidate.Title);
                var codeMatch = !string.IsNullOrWhiteSpace(mediaCode)
                    && string.Equals(candidateCode, mediaCode, StringComparison.OrdinalIgnoreCase);

                var tokenMatches = queryTokens.Count(t => normalizedTitle.Contains(t, StringComparison.Ordinal));
                if (config.RequireTitleTokenMatch && queryTokens.Count > 0 && tokenMatches == 0)
                {
                    continue;
                }

                var score = 0;
                if (codeMatch)
                {
                    score += 10000;
                }

                score += tokenMatches * 100;
                if (queryTokens.Count > 0 && tokenMatches == queryTokens.Count)
                {
                    score += 250; // every distinctive word of the query is present
                }

                if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal))
                {
                    score += 500;
                }

                if (candidate.IsRatedGood)
                {
                    score += 400;
                }

                // Original vs machine translation. Only scored when the row
                // actually carries a source-language label and the caller said
                // which language it wants - an unlabelled row must not be
                // punished for missing information.
                if (config.PreferRequestedLanguageAsSource
                    && !string.IsNullOrWhiteSpace(requestedLanguage)
                    && !string.IsNullOrWhiteSpace(candidate.SourceLanguage))
                {
                    // Deliberately larger than the whole rest of the tail
                    // (rating + tokens + phrase + size + downloads add up to
                    // roughly 1400) so a translated row can never outrank an
                    // original by being more popular, and deliberately smaller
                    // than a media-code match (+10000) so this can never
                    // promote a row that is a different title altogether.
                    score += LanguageMap.EnglishNameMatches(candidate.SourceLanguage, requestedLanguage)
                        ? 3000
                        : -1500;
                }

                if (candidate.Downloads is int downloads)
                {
                    // Log-scaled: 10 downloads should beat 1, and 2000 should
                    // not beat 10 by 200x.
                    score += Math.Min(60, (int)(Math.Log10(downloads + 1) * 25));
                }

                if (candidate.SizeBytes is long size)
                {
                    // A few KB is a stub or an error page; a feature-length
                    // .srt is tens to hundreds of KB.
                    if (size < 8 * 1024)
                    {
                        score -= 60;
                    }
                    else if (size <= 512 * 1024)
                    {
                        score += 15;
                    }
                }

                if (year.HasValue && ContainsOtherYear(normalizedTitle, year.Value))
                {
                    // Right title, wrong release year - usually a remake.
                    score -= 800;
                }

                scored.Add((candidate, score));
            }

            return scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Result.Downloads ?? 0)
                .ThenBy(x => x.Result.Title.Length)
                .Select(x => x.Result)
                .ToList();
        }

        /// <summary>
        /// The words a row must share with the query to be considered the same
        /// title. The year is deliberately excluded: it is the token
        /// subtitlecat matches most loosely, and "contains 1979" is exactly
        /// the non-signal that produced the wrong-subtitle downloads.
        /// </summary>
        private static IReadOnlyList<string> DistinctiveTokens(string normalizedQuery, int? year)
        {
            var yearToken = year?.ToString(CultureInfo.InvariantCulture);
            var tokens = new List<string>();

            foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 2
                    && !string.Equals(token, yearToken, StringComparison.Ordinal)
                    && !tokens.Contains(token, StringComparer.Ordinal))
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        private static bool ContainsOtherYear(string normalizedTitle, int year)
        {
            foreach (Match match in YearPattern.Matches(normalizedTitle))
            {
                if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var found)
                    && found != year)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The picker shows only this string, so it has to carry what a human
        /// would otherwise have to open subtitlecat.com to compare: which
        /// language, how the site's users rated it, how often it has been
        /// downloaded and how large it is.
        /// </summary>
        private static string BuildDisplayName(TitleSearchResult candidate, LanguageEntry language)
        {
            var sb = new StringBuilder(candidate.Title);
            sb.Append(" [").Append(language.DisplayName).Append(']');

            if (candidate.IsRatedGood)
            {
                sb.Append(" · rated good");
            }

            if (candidate.Downloads is int downloads && downloads > 0)
            {
                sb.Append(" · ").Append(downloads.ToString(CultureInfo.InvariantCulture)).Append(" downloads");
            }

            if (candidate.SizeBytes is long size && size > 0)
            {
                sb.Append(" · ").Append(Math.Max(1, size / 1024).ToString(CultureInfo.InvariantCulture)).Append(" KB");
            }

            return sb.ToString();
        }

        private static string Normalize(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
            }

            return sb.ToString().Trim();
        }

        private static string EncodeToken(string url, string language)
        {
            var payload = JsonSerializer.Serialize(new SubtitleToken(url, language));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        }

        private static SubtitleToken DecodeToken(string id)
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(id));
            return JsonSerializer.Deserialize<SubtitleToken>(json)
                   ?? throw new InvalidOperationException("SubtitleCat: malformed subtitle id.");
        }

        private sealed record SubtitleToken(string Url, string Language);
    }
}

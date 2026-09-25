using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
            var maxCandidates = Math.Max(1, Plugin.Instance?.Configuration.MaxCandidates ?? 5);
            var results = new List<RemoteSubtitleInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    var ranked = RankCandidates(candidates, query, mediaCode)
                        .Take(maxCandidates)
                        .ToList();

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

                            if (seen.Add(token))
                            {
                                results.Add(new RemoteSubtitleInfo
                                {
                                    Id = token,
                                    ProviderName = Name,
                                    // The bare page title carries no language information, and one
                                    // title usually has several languages on offer - without the
                                    // language the picker shows indistinguishable duplicates
                                    // (e.g. two identical rows for zh-CN and zh-TW).
                                    Name = $"{candidate.Title} [{lang.DisplayName}]",
                                    Format = "srt",
                                    ThreeLetterISOLanguageName = threeLetter,
                                    IsHashMatch = isExactCodeMatch,
                                    Forced = false,
                                });
                            }
                        }
                    }

                    var added = results.Count - queryResultCountBefore;
                    _logger.LogInformation(
                        "SubtitleCat: query #{Index} added {Added} subtitle result(s); total={TotalResults}",
                        queryIndex + 1,
                        added,
                        results.Count);

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
        /// Cheap relevance ranking: normalize both strings (lowercase, strip
        /// punctuation/whitespace) and sort candidates by how much of the
        /// query's normalized text appears as a contiguous run in the
        /// title. This is deliberately simple - subtitlecat's own search
        /// already does the heavy lifting; this just avoids opening
        /// obviously-unrelated rows first when the result list is long.
        /// </summary>
        private static IEnumerable<TitleSearchResult> RankCandidates(
            IReadOnlyList<TitleSearchResult> candidates,
            string query,
            string? mediaCode)
        {
            return candidates
                .Select(c =>
                {
                    var candidateCode = MediaCodeExtractor.Extract(c.Title);
                    var codeMatch = !string.IsNullOrWhiteSpace(mediaCode)
                        && string.Equals(candidateCode, mediaCode, StringComparison.OrdinalIgnoreCase);

                    var score = codeMatch
                        ? 10000
                        : SimilarityScore(Normalize(c.Title), Normalize(query));

                    return (Result: c, Score: score);
                })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Result);
        }

        private static int SimilarityScore(string title, string query)
        {
            if (title.Contains(query, StringComparison.Ordinal))
            {
                return 1000 - Math.Abs(title.Length - query.Length);
            }

            var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return queryTokens.Count(t => title.Contains(t, StringComparison.Ordinal));
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

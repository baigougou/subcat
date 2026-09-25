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

            var query = BuildQuery(request);
            _logger.LogInformation("SubtitleCat: Search query = \"{Query}\"", query);

            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.LogWarning("SubtitleCat: Search returned no query; Jellyfin did not provide a searchable title/episode.");
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var client = CreateClient();
            var maxCandidates = Math.Max(1, Plugin.Instance?.Configuration.MaxCandidates ?? 5);

            try
            {
                var candidates = await client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("SubtitleCat: SearchAsync returned {Count} candidates for \"{Query}\"", candidates.Count, query);

                if (candidates.Count == 0)
                {
                    _logger.LogInformation("SubtitleCat: no search results for \"{Query}\"", query);
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                var ranked = RankByTitleSimilarity(candidates, query).Take(maxCandidates).ToList();

                var results = new List<RemoteSubtitleInfo>();
                foreach (var candidate in ranked)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogDebug("SubtitleCat: loading languages from {Url}", candidate.DetailUrl);

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

                        results.Add(new RemoteSubtitleInfo
                        {
                            Id = token,
                            ProviderName = Name,
                            Name = candidate.Title,
                            Format = "srt",
                            ThreeLetterISOLanguageName = threeLetter,
                            IsHashMatch = false,
                            Forced = false,
                        });
                    }
                }

                _logger.LogInformation("SubtitleCat: Search completed. Returning {Count} subtitle candidates for \"{Query}\"", results.Count, query);
                return results;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("SubtitleCat: Search cancelled for \"{Query}\"", query);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleCat: Search failed for \"{Query}\"", query);
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

        private static string BuildQuery(SubtitleSearchRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.SeriesName) && request.ParentIndexNumber.HasValue && request.IndexNumber.HasValue)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} S{1:D2}E{2:D2}",
                    request.SeriesName,
                    request.ParentIndexNumber.Value,
                    request.IndexNumber.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                return request.ProductionYear.HasValue
                    ? $"{request.Name} {request.ProductionYear.Value}"
                    : request.Name!;
            }

            return string.Empty;
        }

        /// <summary>
        /// Cheap relevance ranking: normalize both strings (lowercase, strip
        /// punctuation/whitespace) and sort candidates by how much of the
        /// query's normalized text appears as a contiguous run in the
        /// title. This is deliberately simple - subtitlecat's own search
        /// already does the heavy lifting; this just avoids opening
        /// obviously-unrelated rows first when the result list is long.
        /// </summary>
        private static IEnumerable<TitleSearchResult> RankByTitleSimilarity(IReadOnlyList<TitleSearchResult> candidates, string query)
        {
            var normalizedQuery = Normalize(query);

            return candidates
                .Select(c => (Result: c, Score: SimilarityScore(Normalize(c.Title), normalizedQuery)))
                .OrderByDescending(t => t.Score)
                .Select(t => t.Result);
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

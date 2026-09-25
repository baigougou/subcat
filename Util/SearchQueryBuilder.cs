using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleCat.Util
{
    internal static class SearchQueryBuilder
    {
        public static IReadOnlyList<string> Create(string? name, string? mediaPath, string? seriesName, int? season, int? episode, int? year)
        {
            var queries = new List<string>();
            var fileName = string.IsNullOrWhiteSpace(mediaPath) ? null : Path.GetFileNameWithoutExtension(mediaPath);
            var rankingTitle = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : fileName?.Trim();

            var mediaCode = MediaCodeExtractor.Extract(fileName) ?? MediaCodeExtractor.Extract(rankingTitle);

            if (!string.IsNullOrWhiteSpace(mediaCode))
            {
                queries.Add(mediaCode);
            }

            if (!string.IsNullOrWhiteSpace(seriesName) && season.HasValue && episode.HasValue)
            {
                queries.Add($"{seriesName.Trim()} S{season.Value:D2}E{episode.Value:D2}");
            }
            else if (!string.IsNullOrWhiteSpace(rankingTitle))
            {
                var titleQuery = year.HasValue ? $"{rankingTitle} {year.Value}" : rankingTitle;
                queries.Add(titleQuery);
            }

            if (queries.Count == 0 && !string.IsNullOrWhiteSpace(fileName))
            {
                queries.Add(fileName);
            }

            return queries.Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

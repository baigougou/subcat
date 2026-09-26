using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleCat.Util
{
    internal static class MediaCodeExtractor
    {
        private static readonly Regex SeparatedCode = new(
            @"(?<![A-Z0-9])([A-Z]{2,8})[-_ ]?(\d{2,6})(?![A-Z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Fc2Code = new(
            @"(?<![A-Z0-9])FC2[-_ ]?PPV[-_ ]?(\d{4,8})(?![A-Z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string? Extract(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = Path.GetFileNameWithoutExtension(value.Trim());

            var fc2 = Fc2Code.Match(text);
            if (fc2.Success)
            {
                return $"FC2-PPV-{fc2.Groups[1].Value}";
            }

            var match = SeparatedCode.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var prefix = match.Groups[1].Value.ToUpperInvariant();
            if (prefix is "EP" or "E" or "S")
            {
                return null;
            }

            return $"{prefix}-{match.Groups[2].Value}";
        }
    }
}

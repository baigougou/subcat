using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.SubtitleCat.Util
{
    /// <summary>
    /// subtitlecat.com labels every language row with a short code (the "alt"
    /// attribute on the flag &lt;img&gt;). Those codes are mostly Google
    /// Translate's language codes, which mostly-but-not-perfectly line up
    /// with ISO 639-1. This class turns a subtitlecat code into the
    /// ISO 639-2 (three-letter) code Jellyfin expects on
    /// RemoteSubtitleInfo.ThreeLetterISOLanguageName, and back the other way
    /// so we can filter search results down to the language Jellyfin asked for.
    ///
    /// Where .NET's own culture database can resolve a code we lean on
    /// CultureInfo instead of hand-maintaining the whole ISO 639 table -
    /// it's more likely to be correct and it's one less thing to keep in sync.
    /// A handful of codes need an override first because they're old Google
    /// codes ("iw" for Hebrew), regional variants subtitlecat treats as their
    /// own language ("zh-CN"/"zh-TW", "pt"/"pt-BR"), or languages .NET's
    /// culture table doesn't ship at all (in which case we fall back to a
    /// hand-picked ISO 639-2 code where one exists).
    /// </summary>
    public static class LanguageMap
    {
        // subtitlecat code -> a .NET-resolvable culture name to try first.
        private static readonly Dictionary<string, string> CultureOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["iw"] = "he",
            ["jw"] = "jv",
            ["zh-CN"] = "zh-Hans",
            ["zh-TW"] = "zh-Hant",
            ["pt"] = "pt-PT",
            ["pt-BR"] = "pt-BR",
            ["es-419"] = "es-MX",
            ["sr-ME"] = "sr-Latn-ME",
            ["sh"] = "sr-Latn",
            ["ny"] = "ny",
            ["tl"] = "fil",
        };

        // subtitlecat code -> ISO 639-2 code, for languages .NET's culture
        // table typically doesn't include at all. Best-effort; if one of
        // these is wrong for your Jellyfin version, it only affects that one
        // language's automatic-language-match filtering, not the plugin as a
        // whole - the raw subtitle is still listed and downloadable.
        private static readonly Dictionary<string, string> ManualIso6392 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ak"] = "aka",
            ["bem"] = "bem",
            ["bh"] = "bih",
            ["ceb"] = "ceb",
            ["chr"] = "chr",
            ["crs"] = "crp",
            ["ee"] = "ewe",
            ["gaa"] = "gaa",
            ["gn"] = "grn",
            ["ha"] = "hau",
            ["haw"] = "haw",
            ["hmn"] = "hmn",
            ["ia"] = "ina",
            ["ig"] = "ibo",
            ["kg"] = "kon",
            ["kri"] = "kri",
            ["loz"] = "loz",
            ["lua"] = "lua",
            ["mfe"] = "mfe",
            ["mo"] = "mol",
            ["ny"] = "nya",
            ["nyn"] = "nyn",
            ["pcm"] = "pcm",
            ["qu"] = "que",
            ["rn"] = "run",
            ["st"] = "sot",
            ["tn"] = "tsn",
            ["to"] = "ton",
            ["tum"] = "tum",
            ["tw"] = "twi",
            ["lg"] = "lug",
            ["ach"] = "ach",
            ["nso"] = "nso",
        };

        /// <summary>
        /// Resolves a subtitlecat language code to an ISO 639-2 (three-letter)
        /// code. Returns null if no mapping could be determined at all.
        /// </summary>
        public static string? ToThreeLetter(string subtitleCatCode)
        {
            if (string.IsNullOrWhiteSpace(subtitleCatCode))
            {
                return null;
            }

            var candidate = CultureOverrides.TryGetValue(subtitleCatCode, out var overrideName)
                ? overrideName
                : subtitleCatCode;

            try
            {
                var culture = CultureInfo.GetCultureInfo(candidate);
                if (!string.IsNullOrWhiteSpace(culture.ThreeLetterISOLanguageName))
                {
                    return culture.ThreeLetterISOLanguageName;
                }
            }
            catch (CultureNotFoundException)
            {
                // fall through to manual table
            }

            return ManualIso6392.TryGetValue(subtitleCatCode, out var manual) ? manual : null;
        }

        /// <summary>
        /// Returns true if the given subtitlecat language code corresponds to
        /// the ISO 639-1/639-2 language Jellyfin is asking for. Accepts
        /// either a two- or three-letter code in <paramref name="requested"/>.
        /// </summary>
        public static bool Matches(string subtitleCatCode, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                return false;
            }

            if (string.Equals(subtitleCatCode, requested, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var three = ToThreeLetter(subtitleCatCode);
            if (three is null)
            {
                return false;
            }

            if (string.Equals(three, requested, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // requested might be a two-letter code; try resolving it to three
            // letters the same way and compare again.
            try
            {
                var requestedCulture = CultureInfo.GetCultureInfo(requested);
                return string.Equals(three, requestedCulture.ThreeLetterISOLanguageName, StringComparison.OrdinalIgnoreCase);
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
        }
    }
}

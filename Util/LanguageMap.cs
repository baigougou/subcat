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

        // Language names subtitlecat prints in "(translated from ...)" that
        // .NET's own culture table either names differently or does not carry
        // as a language at all. Everything else is resolved from the culture
        // table, so this stays short on purpose.
        private static readonly Dictionary<string, string> EnglishNameOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Chinese Simplified"] = "zho",
            ["Chinese Traditional"] = "zho",
            ["Mandarin Chinese"] = "zho",
            ["Cantonese"] = "zho",
            ["Farsi"] = "fas",
            ["Tagalog"] = "fil",
        };

        // Built once from .NET's culture database: English language name
        // ("Chinese", "Korean") -> ISO 639-2. subtitlecat labels a row's
        // *source* language by name, not by code, so this is the only way to
        // compare it with what Jellyfin asked for.
        private static readonly Lazy<Dictionary<string, string>> EnglishNameToIso6392 =
            new(BuildEnglishNameMap);

        // ISO 639-2 -> ISO 639-1, so a source-language match can still be
        // compared when Jellyfin asks with a two-letter code ("zh").
        private static readonly Lazy<Dictionary<string, string>> Iso6392ToTwoLetter =
            new(BuildTwoLetterMap);

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

        /// <summary>
        /// Resolves the English language name subtitlecat appends to a search
        /// row - the "Chinese" of "(translated from Chinese)" - to an ISO 639-2
        /// (three-letter) code. Returns null when the name is unknown, which
        /// callers must treat as "no information" rather than "no match".
        /// </summary>
        public static string? ToThreeLetterFromEnglishName(string? englishName)
        {
            if (string.IsNullOrWhiteSpace(englishName))
            {
                return null;
            }

            var name = englishName.Trim();
            if (EnglishNameToIso6392.Value.TryGetValue(name, out var three))
            {
                return three;
            }

            // "Chinese (Simplified)" and friends: fall back to the bare name.
            var bracket = name.IndexOf('(');
            if (bracket > 0
                && EnglishNameToIso6392.Value.TryGetValue(name.Substring(0, bracket).Trim(), out var baseThree))
            {
                return baseThree;
            }

            return null;
        }

        /// <summary>
        /// Returns true when a subtitlecat source-language name ("Chinese")
        /// denotes the same language Jellyfin asked for. Accepts a two- or
        /// three-letter code in <paramref name="requested"/>, or a culture
        /// name such as "zh-CN".
        ///
        /// This is what lets a caller tell an original subtitle apart from a
        /// machine translation: a row whose source language is the language
        /// being requested is the human-made original, anything else has been
        /// run through a translator.
        /// </summary>
        public static bool EnglishNameMatches(string? englishName, string? requested)
        {
            if (string.IsNullOrWhiteSpace(englishName) || string.IsNullOrWhiteSpace(requested))
            {
                return false;
            }

            var three = ToThreeLetterFromEnglishName(englishName);
            if (three is null)
            {
                return false;
            }

            if (string.Equals(three, requested, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // requested may be two-letter ("zh"), so bring the source language
            // down to two letters as well and reuse the code path above.
            return Iso6392ToTwoLetter.Value.TryGetValue(three, out var twoLetter)
                && Matches(twoLetter, requested);
        }

        private static Dictionary<string, string> BuildEnglishNameMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Neutral cultures first: their EnglishName is the bare language
            // name ("Chinese", "English"), which is exactly what subtitlecat
            // prints. Regional cultures then fill in the names that are only
            // available that way, without overwriting the bare ones.
            AddCultureNames(map, CultureInfo.GetCultures(CultureTypes.NeutralCultures));
            AddCultureNames(map, CultureInfo.GetCultures(CultureTypes.AllCultures));

            foreach (var pair in EnglishNameOverrides)
            {
                map[pair.Key] = pair.Value;
            }

            return map;
        }

        private static void AddCultureNames(Dictionary<string, string> map, CultureInfo[] cultures)
        {
            foreach (var culture in cultures)
            {
                var three = culture.ThreeLetterISOLanguageName;
                if (string.IsNullOrWhiteSpace(three) || three.Length != 3)
                {
                    continue;
                }

                var english = culture.EnglishName;
                if (string.IsNullOrWhiteSpace(english))
                {
                    continue;
                }

                AddCultureName(map, english, three);

                // "Chinese (Simplified)" should also answer to plain "Chinese".
                var bracket = english.IndexOf('(');
                if (bracket > 0)
                {
                    AddCultureName(map, english.Substring(0, bracket).Trim(), three);
                }
            }
        }

        private static void AddCultureName(Dictionary<string, string> map, string name, string three)
        {
            if (string.IsNullOrWhiteSpace(name) || map.ContainsKey(name))
            {
                return;
            }

            map[name] = three;
        }

        private static Dictionary<string, string> BuildTwoLetterMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                var three = culture.ThreeLetterISOLanguageName;
                var two = culture.TwoLetterISOLanguageName;
                if (string.IsNullOrWhiteSpace(three) || three.Length != 3
                    || string.IsNullOrWhiteSpace(two) || two.Length != 2)
                {
                    continue;
                }

                if (!map.ContainsKey(three))
                {
                    map[three] = two;
                }
            }

            return map;
        }
    }
}

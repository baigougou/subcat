# Jellyfin.Plugin.SubtitleCat

A Jellyfin subtitle provider for [subtitlecat.com](https://www.subtitlecat.com). No account,
login, or API key needed — it scrapes the same search and download pages a browser would use.

> **Status.** Search and download work: the included workflow builds the plugin (`dotnet build`
> against `Jellyfin.Controller` 10.10.*-*, targeting `net8.0`, ABI `10.10.0.0`), and builds
> 1.0.3.0 and 1.0.4.0 were installed into a live Jellyfin 10.10 server and used for real
> searches and downloads.
>
> 1.0.5.0 and 1.0.6.0 change **which** result gets downloaded, not how the download happens.
> That selection logic was verified by replaying real search pages captured from subtitlecat.com
> through the same rules re-implemented in Python. Treat the ranking numbers below as
> tuned-but-unproven on your library, and read "Known risk areas" for the two problems these
> releases deliberately do **not** fix.

## What it does

- Adds "SubtitleCat" as a selectable subtitle source in Jellyfin's per-library subtitle settings,
  for both movies and TV episodes.
- Searches `subtitlecat.com/index.php?search=...` with the title (and season/episode for TV),
  then opens the top few matching result pages and returns the subtitles available in the
  language Jellyfin asked for.
- Reads each row's own quality signals — the site's user rating (thumbs up / thumbs down), the
  download counter, the file size, the language count, and the language the row was *translated
  from* — from the same `<tr>` as the link, so gathering them costs no extra requests.
- Downloads the matched `.srt` file when you pick a result from Jellyfin's "Edit subtitles" UI.

## How a result is chosen (1.0.6.0)

subtitlecat's own search is a loose bag-of-words match, and it is happy to answer a query it did
not really understand. Searching for a Chinese-titled anime plus its year, for example, returns
every film from that year — the intended title among them, but not necessarily first. Earlier
builds took the first row the site returned; this is what changed:

1. **Relevance gate.** A result row must share at least one *distinctive* word with the query.
   Years are explicitly excluded from the comparison, because "1979" matches hundreds of
   unrelated films. If subtitlecat returns rows that match nothing but the year, the query is
   discarded and logged — nothing is downloaded, instead of the wrong thing.
2. **Drop rows rated bad by the site's users** (5.7% of rows are rated *good*, 0.7% *bad*).
3. **Prefer the original over a machine translation.** Every row is labelled with the language it
   was translated from: `<a href="subs/1029/abp-984-4k.html">abp-984-4k</a> (translated from
   Chinese)`. A row whose source language *is* the language being requested is the human-made
   original; every other row was run through a translator. That difference is large and visible —
   measured on one title, the Chinese-sourced rows carried **zero** `<b>` tags and no foreign
   script residue, while rows sourced from English carried ~1,800 `<b>` tags and Korean Hangul
   left over from the pivot language. Source language matching the request scores +3000, anything
   else −1500. Rows with no label are not penalised, so a title with no original in the requested
   language still falls back to its best translation.
4. **Rank: relevance first, then quality.** An exact media-code match (e.g. `ABF-043` in both the
   query and the title) dominates. Otherwise: number of distinctive words matched, whole-query
   containment, then user rating, then download count on a log scale, then file size — sub-8 KB
   files are treated as placeholders, and a *different* year in the title is penalised.
5. **One result per language by default** (`MaxPerLanguage`). Jellyfin never overwrites a
   subtitle file: it writes `<video>.<lang>.srt` and falls back to `<video>.<lang>.0.srt`,
   `.1.srt`, … when that name is taken. Offering several rows that all resolve to `zho` is what
   litters a media folder with numbered duplicates.
6. **Stop early.** Once the requested language is served, remaining candidate pages and fallback
   queries are skipped. Without this, a missing-subtitles scan opens up to `MaxCandidates` detail
   pages per item even after it already has the answer.
7. **Show the signals.** Each picker row carries what you would otherwise have to open
   subtitlecat.com to compare, e.g.
   `Inception.2010.1080p.BrRip.x264.YIFY [Chinese (Simplified)] · rated good · 2223 downloads · 132 KB`

## Build without installing anything locally

If you don't have (or don't want) a .NET SDK on your own machine, `.github/workflows/build.yml`
lets GitHub build it for you for free:

1. Create a new **public** repository on GitHub (private also works, but Actions minutes are
   metered on private repos — public repos get unlimited free minutes).
2. Push this folder's contents to that repository (via the GitHub web UI's "upload files", or
   `git init && git add . && git commit -m init && git push`).
3. Go to the repo's **Actions** tab. A workflow run should start automatically on the push; if it
   doesn't, click **Build plugin → Run workflow**.
4. Once it finishes (green check, a minute or two), open that run and download the
   **SubtitleCat-plugin** artifact at the bottom of the page. It's a zip containing exactly what
   goes in your Jellyfin `plugins` folder: `Jellyfin.Plugin.SubtitleCat.dll`,
   `HtmlAgilityPack.dll`, and `meta.json`.
5. Unzip it into a folder named `SubtitleCat_1.0.5.0` under your Jellyfin `plugins` directory (see
   the path table under "Install" below) and restart Jellyfin.

If the build fails, open the failed step's log — it'll be one of the two things described in
"If the build fails on a Jellyfin SDK type" below, and the log will point at the exact line.

## Project layout

```
Jellyfin.Plugin.SubtitleCat.csproj   - targets net8.0, references Jellyfin.Controller 10.10.*-* + HtmlAgilityPack
Plugin.cs                            - plugin entry point (BasePlugin<PluginConfiguration>), fixed GUID
PluginServiceRegistrator.cs          - registers SubtitleCatProvider as an ISubtitleProvider
Configuration/
  PluginConfiguration.cs             - MaxCandidates, RequestTimeoutSeconds, MaxPerLanguage,
                                       ExcludeBadlyRated, RequireTitleTokenMatch,
                                       PreferRequestedLanguageAsSource
  configPage.html                    - minimal settings page (no credentials needed)
Providers/
  SubtitleCatProvider.cs             - ISubtitleProvider implementation (the Jellyfin-facing part)
  SubtitleCatClient.cs               - HTTP + HTML parsing against subtitlecat.com (no Jellyfin deps)
  SubtitleCatModels.cs               - small internal DTOs used between the two files above
Util/
  LanguageMap.cs                     - subtitlecat language code <-> ISO 639-2 mapping, and
                                       English language name ("Chinese") -> ISO 639-2
build.yaml                           - plugin manifest metadata (name, GUID, version, targetAbi, changelog)
meta.json                            - copied into the install folder so Jellyfin can read plugin metadata
```

`SubtitleCatClient` and `LanguageMap` don't reference any Jellyfin types at all — if subtitlecat
ever changes its page markup, or you want to reuse this against Emby instead, those two files are
the only ones that should need touching.

## Build

Requires the .NET 8 SDK.

```bash
dotnet restore Jellyfin.Plugin.SubtitleCat.csproj
dotnet build Jellyfin.Plugin.SubtitleCat.csproj -c Release
```

Output:

```
bin/Release/net8.0/Jellyfin.Plugin.SubtitleCat.dll
bin/Release/net8.0/HtmlAgilityPack.dll
```

### If the build fails on a Jellyfin SDK type

The `.csproj` pins `Jellyfin.Controller` to `10.10.*-*`, matching the `targetAbi` in `build.yaml`.
If you're running a different Jellyfin server version (10.9, 10.11, ...), change both of those
first and re-restore.

If it still fails to compile on a specific member (e.g. a property on `SubtitleSearchRequest`,
`RemoteSubtitleInfo`, or `SubtitleResponse` in `Providers/SubtitleCatProvider.cs`), that means the
SDK you're building against renamed or moved something. This is normal — Jellyfin's plugin ABI
shifts slightly between versions. Fix: open that one file in an IDE with the actual SDK installed,
let autocomplete show you the real member names on those three types, and adjust. Nothing else in
the plugin needs to change for this.

For an authoritative, always-current reference for the exact interface shape, compare against:

- https://github.com/jellyfin/jellyfin-plugin-template
- https://github.com/jellyfin/jellyfin-plugin-opensubtitles

## Install

1. Build (above), or download a pre-built zip if you made one via `jprm`.
2. Create a folder named `SubtitleCat_1.0.5.0` inside your Jellyfin `plugins` directory:

   | Install type       | Path                                         |
   |---------------------|-----------------------------------------------|
   | Docker              | `/config/plugins/`                            |
   | Linux (package)     | `/var/lib/jellyfin/plugins/`                  |
   | Windows (service)   | `%ProgramData%\Jellyfin\Server\plugins\`      |
   | macOS               | `~/Library/Application Support/jellyfin/plugins/` |

3. Copy in `Jellyfin.Plugin.SubtitleCat.dll`, `HtmlAgilityPack.dll`, and `meta.json`.
4. Restart Jellyfin.
5. Go to **Dashboard → Libraries → (select a library) → Subtitles**, and enable **SubtitleCat** as
   a subtitle download source (order it however you like relative to OpenSubtitles etc.).
6. Use **Edit subtitles** on a movie/episode, or let Jellyfin's scheduled subtitle-download task
   pick it up automatically.

Upgrading from an older build: the plugin GUID is unchanged, so you can drop the new folder in
next to the old one and remove the old `SubtitleCat_<old version>` folder afterwards. Your
configuration is kept, and newly added settings get their documented defaults.

## Configuration

**Dashboard → Plugins → SubtitleCat**:

- **Max candidate titles per search** (default 5) — how many subtitlecat.com search-result rows
  to open and inspect per search. Lower = faster and gentler on the site; raise it if you're
  missing matches on ambiguously-named titles.
- **Request timeout** (default 15s).
- **Max results per language** (default 1) — how many rows may be offered for the same language.
  1 keeps only the best-scoring row and is the main defence against `.0/.1/.2` duplicate files;
  raise it if you want alternatives to compare by hand.
- **Exclude badly rated results** (default on) — discard rows the site's users have rated down.
- **Require a distinctive title word** (default on) — require each row to share a non-year word
  with the query. Turning this off restores subtitlecat's raw, unfiltered result list (and with
  it the old behaviour of sometimes downloading an unrelated film of the same year).
- **Prefer subtitles written in the requested language** (default on) — rank a row whose *source*
  language is the language being requested (i.e. the human-made original) above rows that were
  machine-translated into it. Turn it off to go back to treating every row that offers the
  language as equally good.

## Known risk areas / limitations

- **Non-English titles are a weak spot.** subtitlecat indexes English titles. If your library
  metadata is Chinese (or any non-English) and no original title is available to the plugin,
  searches for e.g. `红猪 1992` or `罗生门 1950` simply return nothing useful — the provider has no
  title-translation step, and Jellyfin's `SubtitleSearchRequest` does not carry an English name.
  The relevance gate's guarantee is that this case yields **zero** results rather than a wrong
  film. For
  Chinese-language subtitles specifically, prefer a Chinese-focused source (MoviePilot's subtitle
  module, SubHD, shooter.cn) and keep this provider for English-titled content.
- **Jellyfin's numbering is not a plugin bug.** `<video>.<lang>.0.srt`, `.1.srt`, … appear when
  Jellyfin is asked for the same language more than once, because it never overwrites. Besides
  `MaxPerLanguage` (plugin side), check **Dashboard → Libraries → (library) → Subtitles → subtitle
  download languages**: if the list contains several equivalent Chinese entries (`zh`, `zh-CN`,
  `zh-TW`, `chi`, `zho`), Jellyfin downloads once per entry. Keep one.
- **HTML scraping, not an API.** subtitlecat.com has no public API. `SubtitleCatClient` parses the
  live site's HTML (result rows, the quality-signal cells, and the flag `<img>` → language name →
  "Download" link on detail pages). If the site's template changes, parsing may silently return
  zero results, or return results with no quality signals (the parser degrades to "no signal"
  rather than throwing). There's nothing to configure for this — if it stops finding subtitles,
  the fix is updating the XPath queries in `SubtitleCatClient.cs` to match the new markup.
- **Language code mapping is best-effort.** subtitlecat labels languages with old-ish Google
  Translate codes (`iw` for Hebrew, `jw` for Javanese, `zh-CN`/`zh-TW` as separate entries, etc).
  `Util/LanguageMap.cs` leans on .NET's own `CultureInfo` table where possible and only
  hand-maps the languages .NET doesn't ship a culture for. `zh`, `zh-CN` and `zh-TW` all resolve
  to the ISO 639-2 code `zho`, which is why a Chinese download lands on the same file name. A
  handful of rarer languages may map to an approximate or missing code — the subtitle is still
  downloadable, it just may not show up when Jellyfin filters strictly by language.
- **The original-vs-translation rule is evidence from a small sample.** Rule 3 above is the
  strongest quality signal available, but its weighting was tuned on a handful of titles, and the
  `(translated from X)` label is only useful when subtitlecat printed one — a row without the
  label is treated as "unknown" and neither gains nor loses points. If you find a title where it
  picks a worse row, set `PreferRequestedLanguageAsSource` to off in the plugin settings; that
  removes the term entirely without touching anything else. A useful sanity check is that a
  genuine original in Chinese will usually carry no `<b>` tags at all, while a machine-translated
  one typically carries hundreds.
- **Overlap with other providers is not de-duplicated.** If you also run OpenSubtitles/SubBuzz/
  Bazarr, the same subtitle may be offered twice from different sources — that's normal and
  harmless.
- **Respect the upstream site.** `MaxCandidates` exists specifically to avoid hammering
  subtitlecat.com; don't set it very high on a library with thousands of items using scheduled
  subtitle downloads.
- **No tests in the repository.** The ranking rules were validated outside the repo against saved
  copies of the live pages, so behaviour changes are currently caught by reading the code. If you
  want a suite, the natural seam is `SubtitleCatClient` — it takes an `HttpClient` and returns
  plain DTOs, so it can be tested by feeding it saved HTML fixtures without touching the network
  or the Jellyfin SDK.

## Version history

Kept in `build.yaml` (`changelog:`) — that is also what Jellyfin reads when the plugin is served
from a repository:

- **1.0.6.0** — prefer originals over machine translations: each row's source language (the
  `(translated from Chinese)` label, which sits outside the link element and was previously
  unread) is compared with the requested language, and a row sourced from the requested language
  is ranked above one that was translated into it. Fixes the case where the highest-downloaded
  row was also the most machine-translated one.
- **1.0.5.0** — rank and filter results by subtitlecat's own quality signals (rating, downloads,
  size) instead of row order; require a distinctive title word so same-year unrelated films are
  no longer downloaded; drop badly-rated rows; one result per language; stop scanning once the
  requested language is served; show rating/downloads/size on each picker row.
- **1.0.4.0** — fix downloads: detail-page links are root-absolute (`/subs/766/x.zh-zh-CN.srt`)
  and were being resolved to local `file:///subs/...` URIs, so every download failed with
  "The 'file' scheme is not supported"; also show the language name on each result.
- **1.0.3.0** — fix search: result hrefs are relative (`subs/<id>/<name>.html`) but the XPath
  required a leading slash, so every search matched 0 nodes and silently returned nothing.
- **1.0.0.0** — initial release.

## License

MIT — do whatever you like with it.

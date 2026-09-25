# Jellyfin.Plugin.SubtitleCat

A Jellyfin subtitle provider for [subtitlecat.com](https://www.subtitlecat.com). No account,
login, or API key needed — it scrapes the same search and download pages a browser would use.

> **This was written and reviewed carefully, but has not been compiled or run against a live
> Jellyfin server** (the environment that produced it has no access to NuGet or a Jellyfin
> instance to test against). Build it once (locally, or via the included GitHub Actions
> workflow — see "Build without installing anything locally" below) before relying on it. See
> "Known risk areas" further down for exactly where a mismatch is most likely to show up, and how
> to fix it in a few minutes if it does.

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
5. Unzip it into a folder named `SubtitleCat_1.0.4.0` under your Jellyfin `plugins` directory (see
   the path table under "Install" below) and restart Jellyfin.

If the build fails, open the failed step's log — it'll be one of the two things described in
"If the build fails on a Jellyfin SDK type" below, and the log will point at the exact line.

## What it does

- Adds "SubtitleCat" as a selectable subtitle source in Jellyfin's per-library subtitle settings,
  for both movies and TV episodes.
- Searches `subtitlecat.com/index.php?search=...` with the title (and season/episode for TV),
  opens the top few matching result pages, and returns any subtitle available in the language
  Jellyfin asked for.
- Downloads the matched `.srt` file when you pick a result from Jellyfin's "Edit subtitles" UI.

## Project layout

```
Jellyfin.Plugin.SubtitleCat.csproj   - targets net8.0, references Jellyfin.Controller + HtmlAgilityPack
Plugin.cs                           - plugin entry point (BasePlugin<PluginConfiguration>)
PluginServiceRegistrator.cs         - registers SubtitleCatProvider as an ISubtitleProvider
Configuration/
  PluginConfiguration.cs            - MaxCandidates, RequestTimeoutSeconds
  configPage.html                   - minimal settings page (no credentials needed)
Providers/
  SubtitleCatProvider.cs            - ISubtitleProvider implementation (the Jellyfin-facing part)
  SubtitleCatClient.cs              - HTTP + HTML parsing against subtitlecat.com (no Jellyfin deps)
  SubtitleCatModels.cs              - small internal DTOs used between the two files above
Util/
  LanguageMap.cs                    - subtitlecat language code <-> ISO 639-2 mapping
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

The `.csproj` pins `Jellyfin.Controller` to `10.11.*-*`. If you're running a different Jellyfin
server version (10.9, 10.10, ...), change that version string first and re-restore.

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
2. Create a folder named `SubtitleCat_1.0.4.0` inside your Jellyfin `plugins` directory:

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

## Configuration

**Dashboard → Plugins → SubtitleCat**:

- **Max candidate titles per search** (default 5) — how many subtitlecat.com search-result rows
  to open and inspect per search. Lower = faster and gentler on the site; raise it if you're
  missing matches on ambiguously-named titles.
- **Request timeout** (default 15s).

## Known risk areas / limitations

- **HTML scraping, not an API.** subtitlecat.com has no public API. `SubtitleCatClient` parses
  the live site's HTML structure (flag `<img>` → language name → optional "Download" link). If the
  site's template changes, parsing may silently return zero results. There's nothing to configure
  for this — if it stops finding subtitles, the fix is updating the XPath queries in
  `SubtitleCatClient.cs` to match the new markup.
- **Language code mapping is best-effort.** subtitlecat labels languages with old-ish Google
  Translate codes (`iw` for Hebrew, `jw` for Javanese, `zh-CN`/`zh-TW` as separate entries, etc).
  `Util/LanguageMap.cs` leans on .NET's own `CultureInfo` table where possible and only
  hand-maps the languages .NET doesn't ship a culture for. A handful of rarer languages may map
  to an approximate or missing ISO 639-2 code — the subtitle is still downloadable, it just may
  not show up when Jellyfin filters strictly by language.
- **No de-duplication against other providers.** If you also run OpenSubtitles/SubBuzz/Bazarr,
  you may see the same subtitle offered twice from different sources — that's normal and harmless.
- **Respect the upstream site.** `MaxCandidates` exists specifically to avoid hammering
  subtitlecat.com with requests on every search; don't set it very high on a library with
  thousands of items using scheduled subtitle downloads.
- **No tests.** Given the sandbox this was built in has no access to a Jellyfin server or
  subtitlecat.com's live markup, there's no automated test suite. If you want one, the natural
  seam is `SubtitleCatClient` — it takes an `HttpClient` and returns plain DTOs, so it can be
  tested by feeding it saved HTML fixtures without touching the network or the Jellyfin SDK.

## License

MIT — do whatever you like with it.

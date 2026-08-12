# MediaFlow for Jellyfin

MediaFlow is a Jellyfin server plugin that automates the path from **qBittorrent download → TMDb identification → hardlink import → Jellyfin library scan**.

It works at the **individual file level**, so completed episodes can be imported without waiting for an entire season pack to finish.

> Current development target: **MediaFlow 0.1.21 · Jellyfin 10.11.11 · .NET 9**

## What MediaFlow does

```text
qBittorrent
    ↓
completed video file
    ↓
context-aware parser
    ↓
TMDb resolver
    ↓
automatic match or Needs Review
    ↓
hardlink into Jellyfin library
    ↓
Jellyfin library scan
```

### Core features

- Monitors qBittorrent through its Web API.
- Processes movies and TV torrents by configured qBittorrent categories.
- Works per file instead of waiting for the whole torrent.
- Parses noisy release names using the filename, torrent name and parent folders.
- Detects title, year, season and episode signals.
- Does not trust embedded MKV/MP4 title metadata for identification.
- Searches TMDb using original, translated and alternative titles.
- Searches both with and without a parsed year when useful.
- Validates that a requested TV episode actually exists in a candidate series.
- Uses score + score-gap rules before accepting an automatic match.
- Creates **hardlinks only**; there is no silent copy fallback.
- Queues a Jellyfin library scan after a successful import.
- Persists import state so work survives Jellyfin restarts.
- Supports dry-run testing before enabling live imports.
- Can keep existing torrents as a baseline so only newly added torrents are automated.
- Optional strict sequential downloading for TV episodes.

## Jellyfin admin interface

MediaFlow has its own administration page inside the Jellyfin dashboard.

The interface contains:

- **Overview** — worker state, counters and recent activity.
- **Torrents** — media cards with qBittorrent progress, TMDb artwork/identity, per-file or per-episode state and torrent-level actions.
- **Needs Review** — manual TMDb candidate selection.
- **History** — imported, failed, ignored and review state.
- **Logs** — persistent structured MediaFlow events for imports, failures, review, reconcile and admin actions.
- **Settings** — qBittorrent, TMDb, paths, worker and safety configuration.

Connection indicators are shown for:

- MediaFlow worker;
- qBittorrent;
- TMDb.

### Torrent cards and episode details

The Torrents page can lazily load detailed information for a torrent. MediaFlow now resolves a **torrent-level TMDb identity before the first video file has finished downloading**, so a newly added movie or season pack can receive its localized title, year and poster almost immediately. The original release name remains visible below it.

For TV torrents, the card title stays clean (`Тед Лассо (2020)`), while detected seasons are shown as separate season chips next to the torrent status, for example `Сезон 1`, `Сезон 2`, `Сезон 3`. Compact Overview cards show up to three season chips and collapse the remainder into a `+N` chip. The same torrent-level identity is reused when individual episodes finish, avoiding a full TMDb search for every episode.

Expanding a card shows eligible video files and, for TV releases, parsed `SxxExx` information together with:

- qBittorrent file progress and priority;
- MediaFlow state;
- source/destination presence;
- hardlink health;
- TMDb id where available.

### Structured logs

MediaFlow keeps a bounded structured JSONL event log in its plugin data directory. The admin Logs page can filter and search the latest events and clear this log without touching MediaFlow import state.

The structured log is intentionally focused on MediaFlow actions rather than duplicating the entire Jellyfin server log.

## Torrent management

MediaFlow can manage qBittorrent, its own state and the Jellyfin destination independently.

### Reconcile

**Reconcile** compares the selected torrent against MediaFlow state and the Jellyfin library.

It reports:

- eligible qBittorrent video files;
- completed files;
- tracked MediaFlow files;
- healthy imported hardlinks;
- missing Jellyfin destinations;
- destinations that are no longer the same hardlink;
- library-only files where the qBittorrent source no longer exists;
- completed qBittorrent files not yet tracked by MediaFlow;
- failed, Needs Review and ignored entries.

Reconcile is non-destructive.

### Reprocess

**Reprocess / Repeat search** is the normal recovery action when a torrent needs to be identified and imported again.

It:

- removes the per-torrent baseline;
- clears `Failed`, `NeedsReview` and `Ignored` entries;
- keeps healthy `Imported` hardlinks;
- releases stale imported state if the destination is missing or no longer points to the same filesystem object;
- lets the worker perform TMDb matching and import again.

This makes retry/reprocess idempotent for already-correct hardlinks.

### Delete from Jellyfin

**Delete from Jellyfin** removes only MediaFlow-managed destination video files for the selected torrent.

It does **not** remove the qBittorrent torrent or its source data.

Safety behavior:

- deletion is limited to configured Movies/Shows library roots;
- if the source still exists, MediaFlow verifies that source and destination are the same hardlink before deleting;
- a conflicting destination is not overwritten or removed;
- the torrent is placed into a per-torrent baseline after deletion so the worker does not immediately recreate the file;
- use **Reprocess** when you want MediaFlow to search and import it again.

### Delete from qBittorrent

Two separate actions are available:

- **Delete from qBittorrent** — removes the torrent job but keeps downloaded source files.
- **Delete from qBittorrent + source files** — removes the torrent job and asks qBittorrent to delete its source data.

Existing Jellyfin hardlinks are not deleted by either qBittorrent action.

## Needs Review

When the automatic resolver cannot choose a TMDb result confidently, the file is stored as `NeedsReview`.

The admin UI can show candidate results with:

- poster;
- title;
- year;
- TMDb ID;
- resolver score;
- scoring reason.

From the review page you can:

- choose one of the suggested TMDb candidates;
- search TMDb manually with a different query;
- approve a result and import the file;
- ignore the file;
- retry the analysis later.

## TMDb matching

Release metadata is treated as evidence, not absolute truth.

For example:

```text
The.Last.of.Us.2022.S01E01.2160p.WEB-DL.mkv
```

The resolver can:

1. extract title signals from the file and torrent names;
2. detect `S01E01` as a strong TV signal;
3. treat `2022` as a weighted hint rather than a hard requirement;
4. search TMDb with and without the year;
5. compare translated, original and alternative titles;
6. verify that season 1 episode 1 exists;
7. prefer the correct series even when the release year is misleading.

An automatic match must satisfy both the configured minimum score and the minimum gap from the second-best candidate.

## Strict sequential TV downloads

When enabled for the configured TV category, MediaFlow can control qBittorrent file priorities for recognizable episode files.

Conceptually:

```text
next incomplete episode   → Maximum priority
other episodes             → Normal priority (remain selected)
```

All episode files remain selected in qBittorrent so torrent-level progress stays correct. MediaFlow gives the next incomplete episode Maximum priority, keeps the others at Normal priority, and enables qBittorrent sequential download. When the active episode finishes, the next episode becomes the Maximum-priority episode on a later worker cycle.

This mode is intended for season packs where you want to start watching before the entire torrent has downloaded.

## Hardlink requirement

MediaFlow intentionally uses hardlinks instead of copying media.

The download source and Jellyfin destination must therefore be on the **same filesystem**.

Example:

```text
/data/torrents/movie
/data/torrents/tv

/data/media/movie
/data/media/tv
```

Hardlinks provide two directory entries for the same underlying file, so the torrent can continue seeding while Jellyfin sees an independently organized library path without duplicating the media payload.

If a destination already exists, MediaFlow will not blindly overwrite it.

For retry/reprocess operations, an existing destination is considered healthy only when it resolves to the same filesystem object as the source.

## Installation

### Recommended: Jellyfin plugin repository

In Jellyfin:

1. Open **Dashboard → Plugins → Repositories**.
2. Add:

```text
https://raw.githubusercontent.com/RomanKrike/Jellyfin.Plugin.MediaFlow/main/manifest.json
```

3. Open the plugin catalogue.
4. Install **MediaFlow**.
5. Restart Jellyfin.

### Manual installation

Download the plugin ZIP from the GitHub Releases page, extract `Jellyfin.Plugin.MediaFlow.dll` into a MediaFlow plugin directory and restart Jellyfin.

Typical Linux location:

```text
/var/lib/jellyfin/plugins/MediaFlow/
```

The exact plugin directory depends on how Jellyfin is installed.

## Initial configuration

A safe first setup is:

1. Create qBittorrent categories for movies and TV, for example:
   - `movie`
   - `tv`
2. Configure the qBittorrent URL and credentials.
3. Configure path mapping if qBittorrent and Jellyfin see the download share under different paths.
4. Configure the Jellyfin movie and TV library roots.
5. Add a TMDb API v3 key.
6. Set the TMDb language and fallback language.
7. Enable **Dry Run**.
8. Test one known torrent.
9. Keep **Baseline existing torrents on first live run** enabled unless you intentionally want old torrents processed.
10. Disable Dry Run and enable the worker.

### Path mapping example

qBittorrent may report:

```text
/downloads/tv/Silo/Silo.S03E01.mkv
```

while Jellyfin sees the same storage as:

```text
/mnt/media/torrents/tv/Silo/Silo.S03E01.mkv
```

Configure:

```text
qBittorrent path prefix: /downloads
Local/Jellyfin prefix:   /mnt/media/torrents
```

If both applications see exactly the same path, the prefixes can be identical or left unused according to your setup.

## Safety model

MediaFlow is intentionally conservative around destructive or ambiguous operations.

It will not silently:

- copy a file when a hardlink cannot be created;
- overwrite an unrelated destination;
- assume two equal-size files are the same hardlink;
- automatically approve a low-confidence TMDb match;
- delete a conflicting Jellyfin destination during library cleanup.

Recommended workflow for an uncertain situation:

```text
Reconcile
    ↓
inspect the result
    ↓
Reprocess / Needs Review
    ↓
approve only when the identity is clear
```

## Runtime state

MediaFlow keeps persistent state in the Jellyfin plugin data area.

Typical statuses include:

- `Imported`
- `NeedsReview`
- `Failed`
- `Ignored`
- `Baseline`

State is stored separately from qBittorrent itself, which allows MediaFlow to recover from restarts and safely distinguish already processed files from new work.

## Dry Run

Dry Run is intended for parser/resolver validation before live automation.

In Dry Run MediaFlow does not perform live import operations such as:

- hardlink creation;
- Jellyfin library scans caused by imports;
- persistent live import state changes;
- qBittorrent sequential-priority changes.

Use a torrent hash or a unique torrent-name fragment to limit the test scope.

## Build from source

Requirements:

- .NET SDK 9.x;
- the Jellyfin package version configured by the repository.

Build:

```bash
dotnet restore Jellyfin.Plugin.MediaFlow/Jellyfin.Plugin.MediaFlow.csproj
dotnet publish Jellyfin.Plugin.MediaFlow/Jellyfin.Plugin.MediaFlow.csproj \
  -c Release \
  -p:JellyfinVersion=10.11.11
```

The repository also contains a GitHub Actions workflow that builds the plugin, creates versioned releases and updates `manifest.json`.

## Current limitations

MediaFlow is still under active development.

Known areas for further work include:

- sidecar subtitle import;
- special handling for multi-episode files such as `S01E01E02`;
- more absolute-number/anime parsing rules;
- additional parser regression cases from real-world releases;
- persistent user aliases/overrides for difficult titles;
- richer torrent-level TMDb artwork and media metadata in the dashboard;
- further UI polish and diagnostics.

## Project goals

MediaFlow is intentionally focused on one workflow:

> **download media with qBittorrent, identify it reliably, organize it without duplicating storage, and make it appear in Jellyfin with as little manual work as possible.**

It is not intended to replace qBittorrent or Jellyfin. It connects them and owns the import/reconciliation layer between them.

## Localization

MediaFlow keeps user-facing admin UI strings in separate embedded JSON resources:

```text
Jellyfin.Plugin.MediaFlow/Localization/
├── en-US.json
└── ru-RU.json
```

The dashboard loads the language resource through the authenticated MediaFlow admin API and follows the Jellyfin/browser culture. Internal state identifiers such as `Imported`, `Failed`, `NeedsReview`, and `Baseline` stay language-neutral and are translated only for display.

To add another language, copy `en-US.json`, rename it to the target culture (for example `de-DE.json`) and translate the values while keeping the keys unchanged.

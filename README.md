# Jellyfin MediaFlow v0.1

Experimental Jellyfin server plugin that monitors qBittorrent and imports **individually completed video files** without waiting for the whole torrent.

Pipeline:

`qBittorrent file progress == 1 -> context-aware parser -> TMDb candidate resolver -> hardlink -> Jellyfin library scan`

## What v0.1 does

- polls qBittorrent Web API;
- works per file, so episode 1 can be imported while episode 2+ are still downloading;
- optional strict episode ordering through qBittorrent file priorities;
- parses title/year/season/episode from filename + torrent name + parent folder;
- deliberately does **not trust embedded MKV/MP4 title/year metadata** for identification;
- searches TMDb with and without the parsed year;
- uses original/translated/alternative TMDb titles;
- validates that the requested TV episode actually exists in the TMDb candidate;
- scores candidates and requires both a minimum score and a gap from candidate #2;
- creates hardlinks only (no silent copy fallback and no overwrite on destination collision);
- queues a Jellyfin library scan after imports;
- persists imported / failed / needs-review state in the plugin data folder.

## Important v0.1 limitations

- `NeedsReview` is currently logged and stored in `state.json`; there is no review UI yet.
- Multi-episode files such as `S01E01E02` are not handled specially yet.
- Specials / absolute anime numbering need more parser rules.
- Sidecar subtitles are not hardlinked yet.
- Only qBittorrent monitoring is implemented; filesystem fallback scanning can be added later.
- This source was generated without a .NET SDK in the build environment, so it has **not been compile-tested here**. Use the included GitHub Action or `build.sh`; if Jellyfin changed an interface in your exact build, adjust against that exact package version.

## 1. Match the Jellyfin version

Jellyfin requires plugin `Jellyfin.Controller` and `Jellyfin.Model` package versions to match the server version.

Check your server version, then build with it:

```bash
./build.sh 10.11.11
```

Or edit `Directory.Build.props`.

The current default in this repository is `10.11.11`.

## 2. Build

Requirements:

- .NET SDK 9.x

```bash
dotnet restore Jellyfin.Plugin.MediaFlow/Jellyfin.Plugin.MediaFlow.csproj
dotnet publish Jellyfin.Plugin.MediaFlow/Jellyfin.Plugin.MediaFlow.csproj -c Release -p:JellyfinVersion=10.11.11
```

Output:

```text
Jellyfin.Plugin.MediaFlow/bin/Release/net9.0/publish/Jellyfin.Plugin.MediaFlow.dll
```

Alternatively push the repository to GitHub and run the included `build` workflow.

## 3. Install

Create a plugin folder, for example on a standard Linux Jellyfin install:

```bash
mkdir -p /var/lib/jellyfin/plugins/MediaFlow
cp Jellyfin.Plugin.MediaFlow.dll /var/lib/jellyfin/plugins/MediaFlow/
```

Then restart Jellyfin.

For Docker, copy/mount the DLL into the Jellyfin config plugin directory used by your container.

## 4. qBittorrent category

For safety v0.1 defaults to processing only torrents in category:

```text
mediaflow
```

Create this category in qBittorrent and assign media torrents to it.

You can clear the category in plugin settings to process all torrents, but that is not recommended initially.

## 5. Path mapping

qBittorrent and Jellyfin may see the same physical download directory under different paths.

Example:

qBittorrent reports:

```text
/downloads/complete/Fallout/Fallout.S01E01.mkv
```

Jellyfin sees the same filesystem as:

```text
/media-downloads/complete/Fallout/Fallout.S01E01.mkv
```

Configure:

```text
qBittorrent path prefix: /downloads
Local/Jellyfin path prefix: /media-downloads
```

If both see `/downloads`, either leave both mapping fields empty or set both to `/downloads`.

## 6. Hardlink requirement

The source download and destination library **must be on the same filesystem**.

Example desired layout:

```text
/data/downloads
/data/media/Movies
/data/media/Shows
```

where `/data` is one filesystem/dataset/volume.

MediaFlow intentionally does not fall back to copying because a silent fallback can unexpectedly double disk usage.

## 7. TMDb matching behavior

Example dirty release:

```text
The.Last.of.Us.2022.S01E01.2160p.WEB-DL.mkv
```

The resolver:

1. extracts title signals from the filename, torrent title and parent folders;
2. records `2022` only as a weighted hint;
3. detects `S01E01` as a strong TV signal;
4. searches TMDb with `2022` and without a year;
5. compares original and alternative/translated titles;
6. checks whether candidate season 1 episode 1 exists;
7. can therefore prefer `The Last of Us (2023)` despite the incorrect release year.

A high first score is not sufficient by itself: candidate #1 must also beat candidate #2 by `MinimumScoreGap`.

## 8. Strict sequential episodes

When enabled, for a multi-file torrent MediaFlow sorts recognizable episode video files by season/episode and applies:

```text
next incomplete episode -> priority 7 (maximum)
later incomplete episodes -> priority 0 (do not download)
```

Once the next episode reaches `progress == 1`, it is imported and the following episode becomes active on the next polling cycle.

Only video files are reprioritized in v0.1.

## State file

Runtime state is stored under the MediaFlow plugin data directory as:

```text
state.json
```

Statuses:

- `Imported`
- `NeedsReview`
- `Failed`

Failed jobs retry after the configured delay. Needs-review jobs are not automatically retried in v0.1.

## Recommended next work

1. compile against the exact server version and fix any API compatibility issue;
2. test with 20-50 real torrent naming patterns;
3. improve parser regression tests from real bad releases;
4. add Needs Review REST endpoint + small Jellyfin admin UI;
5. add subtitle sidecars;
6. add user corrections/aliases so resolver remembers manual matches.

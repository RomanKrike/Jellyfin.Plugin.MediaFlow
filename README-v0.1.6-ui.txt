MediaFlow v0.1.6 UI update

Main additions:
- Native Jellyfin admin sidebar entry using PluginPageInfo.EnableInMainMenu.
- MediaFlow Control Center with Overview / Torrents / Needs Review / History / Settings tabs.
- Admin-only REST API (RequiresElevation).
- Safe "Reprocess" action: removes torrent baseline and Failed/NeedsReview states while preserving Imported entries.
- Dangerous full reset remains available behind a confirmation dialog.
- Retry individual Failed/NeedsReview state without editing state.json manually.
- qBittorrent torrent progress/state summary.

Safety notes:
- Global __mediaflow_baseline_v1 is never removed by the UI actions.
- Reprocess preserves Imported file entries, reducing duplicate hardlink attempts.
- Full Reset removes Imported state too; use only when you intentionally want a complete reset.

Build first. Do not publish a release until GitHub Actions is green.

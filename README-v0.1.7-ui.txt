MediaFlow v0.1.7 UI visual/lifecycle fix

Changes:
- Moves MediaFlow CSS into the dynamically mounted configuration-page body so Jellyfin Web keeps it.
- Normalizes custom tab/action buttons so browser-native white buttons do not appear.
- Adds a robust initial load fallback in addition to pageshow/viewshow.
- Keeps the existing v0.1.6 admin API, Retry/Reprocess/Reset logic, and settings.
- Bumps plugin version to 0.1.7 so the existing GitHub Action can publish the release automatically.

After install/restart, do an Empty Cache and Hard Reload in the browser once if Jellyfin cached the old configuration page.

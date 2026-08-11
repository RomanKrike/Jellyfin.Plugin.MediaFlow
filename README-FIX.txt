MediaFlow v0.1.6 UI + release automation fix

Fixes:
- MediaFlowAdminController now returns a typed MediaFlowTorrentRow instead of object,
  fixing the AddedOn / MediaFlowStatus compile errors.
- ImportStateStore uses a non-null local dictionary after loading, removing the nullable warning.
- GitHub Actions updated to Node 24 based actions/checkout@v6 + actions/setup-dotnet@v5.
- build.yml now automatically creates a GitHub Release and updates manifest.json when
  Directory.Build.props contains a version not yet present in manifest.json.

Release flow:
1. Change <Version> / AssemblyVersion / FileVersion in Directory.Build.props.
2. Push to main.
3. Build runs.
4. If successful and version is new: tag -> ZIP -> MD5 -> GitHub Release -> manifest.json update.
5. If the version already exists in manifest.json: only the build runs; no duplicate release.

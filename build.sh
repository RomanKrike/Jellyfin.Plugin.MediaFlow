#!/usr/bin/env bash
set -euo pipefail
VERSION="${1:-10.11.11}"
dotnet publish Jellyfin.Plugin.MediaFlow/Jellyfin.Plugin.MediaFlow.csproj -c Release -p:JellyfinVersion="$VERSION"
echo
echo "Build complete for Jellyfin $VERSION"
echo "DLL: Jellyfin.Plugin.MediaFlow/bin/Release/net9.0/publish/Jellyfin.Plugin.MediaFlow.dll"

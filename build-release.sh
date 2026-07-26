#!/usr/bin/env bash
#
# Manual release: build the plugin, package it as a Jellyfin-installable zip,
# compute the MD5 checksum, and update manifest.json. Then upload the zip to a
# GitHub Release and commit manifest.json.
#
# Usage:
#   REPO="youruser/yourrepo" ./build-release.sh 1.5.0.0
#
# Requires: dotnet 9 SDK, jq, zip, md5sum.

set -euo pipefail

GUID="b9f8e1a2-3c4d-4e5f-8a7b-1c2d3e4f5a6b"
NAME="JellyTuber"
TARGET_ABI="10.11.6.0"
PROJECT="Jellyfin.Plugin.JellyTuber.csproj"
DESC="Index YouTube channels and playlists via the YouTube Data API, stream on demand with yt-dlp."

VERSION="${1:-$(jq -r .version meta.json)}"
REPO="${REPO:-YOUR_GH_USER/jellyfin-jellytuber}"
TAG="${TAG:-v${VERSION}}"

echo ">> Building $NAME $VERSION"
dotnet publish "$PROJECT" -c Release -o publish

rm -rf stage && mkdir -p stage
cp publish/Jellyfin.Plugin.JellyTuber.dll stage/
jq --arg v "$VERSION" '.version=$v' meta.json > stage/meta.json

ZIP="jellytuber-${VERSION}.zip"
rm -f "$ZIP"
( cd stage && zip -r "../$ZIP" . >/dev/null )

CHK=$(md5sum "$ZIP" | cut -d' ' -f1)
URL="https://github.com/${REPO}/releases/download/${TAG}/${ZIP}"
TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo "   zip:      $ZIP"
echo "   checksum: $CHK"
echo "   url:      $URL"

[ -f manifest.json ] || echo '[]' > manifest.json
NEWVER=$(jq -n --arg v "$VERSION" --arg t "$TARGET_ABI" --arg u "$URL" --arg c "$CHK" --arg ts "$TS" \
  '{version:$v, targetAbi:$t, sourceUrl:$u, checksum:$c, timestamp:$ts, changelog:("Release " + $v)}')
jq --arg guid "$GUID" --arg name "$NAME" --arg desc "$DESC" --argjson nv "$NEWVER" '
  (map(.guid) | index($guid)) as $i
  | if $i == null
    then . + [{guid:$guid, name:$name, description:$desc, overview:$desc, owner:"self", category:"Channels", versions:[$nv]}]
    else .[$i].versions |= ([$nv] + (map(select(.version != $nv.version)))) end
' manifest.json > manifest.tmp && mv manifest.tmp manifest.json

echo ">> manifest.json updated."
echo
echo "Next steps:"
echo "  1) Create a GitHub Release tagged $TAG and upload $ZIP as an asset"
echo "     gh release create $TAG $ZIP --title $TAG --notes \"Release $VERSION\""
echo "  2) Commit the manifest:"
echo "     git add manifest.json && git commit -m \"release $VERSION\" && git push"

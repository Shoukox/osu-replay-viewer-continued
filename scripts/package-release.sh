#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <win-x64|linux-x64> <vMAJOR.MINOR.PATCH>" >&2
  exit 2
fi

runtime="$1"
version="$2"

if [[ ! "$runtime" =~ ^(win-x64|linux-x64)$ ]]; then
  echo "Unsupported runtime '$runtime'." >&2
  exit 2
fi

if [[ ! "$version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]]; then
  echo "Invalid version '$version'." >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/osu-replay-viewer/osu-replay-viewer.csproj"
release_root="$repo_root/.release"
publish_dir="$release_root/publish/$runtime"
archive_base="osu-replay-viewer-${version}-${runtime}"

rm -rf "$publish_dir"
mkdir -p "$publish_dir"

echo "Restoring for $runtime..."
dotnet restore "$project" --runtime "$runtime"

echo "Publishing $project for $runtime..."
dotnet publish "$project" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  --no-restore \
  --output "$publish_dir" \
  -p:DebugType=None \
  -p:DebugSymbols=false

if [[ "$runtime" == "linux-x64" ]]; then
  # Keep Linux independent from the host distribution. FFmpeg 9 removed the
  # -vsync option used by the recorder, so ship the matching static FFmpeg 8.1
  # build directly in the application-local ffmpeg directory.
  ffmpeg_archive="ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz"
  ffmpeg_url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$ffmpeg_archive"
  ffmpeg_checksums_url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256"
  ffmpeg_tmp="$(mktemp -d)"
  trap 'rm -rf "$ffmpeg_tmp"' EXIT

  echo "Downloading bundled Linux FFmpeg..."
  curl --fail --location --retry 3 --silent --show-error \
    "$ffmpeg_url" -o "$ffmpeg_tmp/$ffmpeg_archive"
  curl --fail --location --retry 3 --silent --show-error \
    "$ffmpeg_checksums_url" -o "$ffmpeg_tmp/checksums.sha256"
  ffmpeg_sha256="$(awk -v archive="$ffmpeg_archive" '$2 == archive { print $1; exit }' \
    "$ffmpeg_tmp/checksums.sha256")"
  if [[ ! "$ffmpeg_sha256" =~ ^[[:xdigit:]]{64}$ ]]; then
    echo "No valid SHA-256 checksum found for $ffmpeg_archive." >&2
    exit 1
  fi
  printf '%s  %s\n' "$ffmpeg_sha256" "$ffmpeg_tmp/$ffmpeg_archive" | sha256sum --check --status

  tar -xJf "$ffmpeg_tmp/$ffmpeg_archive" -C "$ffmpeg_tmp"
  ffmpeg_source="$ffmpeg_tmp/ffmpeg-n8.1-latest-linux64-gpl-8.1/bin/ffmpeg"
  if [[ ! -f "$ffmpeg_source" ]]; then
    echo "Bundled FFmpeg archive did not contain bin/ffmpeg." >&2
    exit 1
  fi

  # The project source contains the legacy Windows FFmpeg files. Do not put
  # those files in the Linux package; the local directory should be
  # unambiguous on every platform.
  rm -rf "$publish_dir/ffmpeg"
  mkdir -p "$publish_dir/ffmpeg"
  install -m 0755 "$ffmpeg_source" "$publish_dir/ffmpeg/ffmpeg"
  install -m 0644 \
    "$ffmpeg_tmp/ffmpeg-n8.1-latest-linux64-gpl-8.1/LICENSE.txt" \
    "$publish_dir/ffmpeg/LICENSE.txt"
fi

cat > "$publish_dir/RELEASE-INFO.txt" <<EOF
osu-replay-viewer
version=$version
runtime=$runtime
commit=${GITHUB_SHA:-local}
EOF

archive="$release_root/$archive_base"

if [[ "$runtime" == "win-x64" ]]; then
  archive+=".zip"
  python3 - "$publish_dir" "$archive" <<'PY'
import pathlib
import sys
import zipfile

source = pathlib.Path(sys.argv[1])
archive = pathlib.Path(sys.argv[2])

with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as output:
    for path in source.rglob("*"):
        if path.is_file():
            output.write(path, path.relative_to(source).as_posix())
PY
else
  archive+=".tar.gz"
  tar -czf "$archive" -C "$publish_dir" .
fi

sha256sum "$archive" > "$archive.sha256"

echo "Created $archive"
echo "Created $archive.sha256"

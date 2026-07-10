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

# The Linux build downloads a suitable FFmpeg at first run. The repository's
# checked-in ffmpeg directory contains legacy Windows DLLs and is not useful
# in a Linux archive.
if [[ "$runtime" == "linux-x64" ]]; then
  rm -rf "$publish_dir/ffmpeg"
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

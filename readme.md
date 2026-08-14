# osu! Replay Viewer Continued
_Based on osu!lazer_

Fork of [nahkd123](https://github.com/nahkd123)'s [osu-replay-viewer](https://github.com/nahkd123/osu-replay-viewer)

This replay viewer allow you to view imported replays (yes you have to import them in osu!lazer
client) without launching the actual game, and you can also render replays to video files, thanks
to FFmpeg.

This project aims to make replay viewer without modifying the official game code or write entire
thing from scratch, but uses components from the game instead. Because of this, it's much more easy
to upgrade to make UI matches with actual game

> This project somewhat implemented [this](https://github.com/ppy/osu/discussions/12986) idea (except
  we're running outside the official client)

## Features
- View downloaded replays (now with custom skins support)
- Download replays (if you can log in)
- Render replays to video file (FFmpeg is bootstrapped automatically when needed)

## Basic Usage
- [ ] TODO

## Requirements
- [.NET 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- OpenGL ES 3.0 compatible device
- Internet access on the first recording run, unless FFmpeg is already installed

## FFmpeg bootstrap
Windows and Linux release packages always use the application-local FFmpeg:
`ffmpeg/ffmpeg.exe` on Windows and `ffmpeg/ffmpeg` on Linux. They never select
the system `PATH`, user cache, or a manually configured external executable.

On other supported platforms, the application looks for FFmpeg in this order:

1. The executable configured in `ffmpeg_options.ffmpeg_executable`.
2. An application-local `ffmpeg` directory.
3. The per-user FFmpeg cache.
4. The system `PATH`.

If no usable executable is found on platforms that support bootstrap downloads,
a GPL FFmpeg 8.1 build is downloaded and verified with SHA-256 checksums. Windows
and Linux x64/arm64 builds are downloaded from
[BtbN FFmpeg Builds](https://github.com/BtbN/FFmpeg-Builds/releases); macOS Intel
uses the [evermeet.cx build](https://evermeet.cx/ffmpeg/). macOS ARM and other
architectures require a system FFmpeg or a manually configured executable.

The default backend is the portable FFmpeg process (`Pipe`). The legacy native
`Binding` backend remains available for compatible Windows installations with the
checked-in FFmpeg libraries, but is automatically replaced by `Pipe` on unsupported
operating systems.

The resolved encoder is selected from the capabilities of the actual FFmpeg binary:
hardware H.264 encoders are preferred when available, with `libx264` as the fallback.
Set `use_cuda_if_possible` to `false` to skip NVIDIA NVENC detection.

The default is 60 FPS, which is usually the best balance between smooth motion
and render time. Higher values such as 120 or 240 FPS remain available when the
GPU and CPU have enough headroom.

The relevant config section is generated and migrated automatically:

```json
{
  "record_options": {
  "fps": 60,
  "resolution": "1280x720",
  "renderer": "Legacy"
  },
  "ffmpeg_options": {
  "mode": "Pipe",
  "libraries_path": "",
  "ffmpeg_executable": "auto",
  "auto_download": true,
  "download_version": "n8.1",
  "cache_directory": "",
  "allow_encoder_fallback": true,
  "video_encoder": "auto",
  "video_encoder_preset": "auto",
  "video_encoder_bitrate": "10M",
  "use_cuda_if_possible": true
  }
}
```

Set `auto_download` to `false` to require a preinstalled FFmpeg. `cache_directory`
can point to a custom per-user cache location; leave it empty for the OS default.

> For the best encoding speed, you can install FFmpeg with hardware acceleration. To actually use
  hardware acceleration, see [hardware acceleration](#hardware-acceleration)

## Command Line arguments
> You can view all command line arguments by running the executable without arguments

Output of ``osu-replay-viewer --help``:
```
Usage:
  dotnet run osu-replay-viewer [options...]
  osu-replay-viewer [options...]

  --yes
    Always Yes
    Always answer yes to all prompts. Similar to 'command | yes'

  --mod-override           <<Mod Name/acronyms:AC>>
    Alternatives: -MOD
    Mod Override
    Override Mod(s). You can use 'no-mod' or 'acronyms:NM' to clear all mods

  --query                  <Keyword>
    Alternatives: -q
    Query
    Query data (Eg: find something in help index or query replays)

  --osu-mode
    Alternatives: -osu
    osu!lazer mode
    Use osu!lazer data (songs, skins, replays)

  --import-beatmap         <path/to/File.osz>
    Alternatives: -osz
    Import beatmap
    Import beatmap from file

  --list
    Alternatives: -list, -l
    List Replays
    List all local replays

  --view                   <Type (local/online/file/auto)> <Score GUID/Beatmap ID (auto)/File.osr>
    Alternatives: -view, -i
    View Replay
    Select a replay to view. This options must be always present (excluding -list options)

  --help
    Alternatives: -h
    Help Index
    View help with details

  --config                 </path/to/config.json>
    Alternatives: -c
    osu-replay-viewer config path
    Use config from file

  --record
    Alternatives: -R
    Record Mode
    Switch to record mode

  --record-output          <Output = osu-replay.mp4>
    Alternatives: -O
    Record Output
    Set record output

  --experimental           <Flag>
    Alternatives: -experimental
    Experimental Toggle
    Toggle experimental feature

  --overlay-override       <true/false>
    Alternatives: -overlay
    Override Overlay Options
    Control the visiblity of player overlay

  --skin                   <Type (import/select)> <Skin name/File.osk>
    Alternatives: -skin, -s
    Select Skin
    Select a skin to use in replay

  --list-skin
    Alternatives: --list-skins, -lskins, -lskin
    List Skins
    List all available skins
```

## Build
To build this project, you need:

- .NET 8.0 SDK
- Git

Clone this repository (``git clone``), then build it with ``dotnet build -c Release`` command.

## Releases through GitHub Actions

The repository publishes only `win-x64` and `linux-x64` Release packages. A
normal commit does not create a release. To publish a version, make a commit
whose subject is exactly in this form:

```text
release: v1.2.3
```

Then push it to `master`:

```bash
git add .
git commit -m "release: v1.2.3"
git push origin master
```

GitHub Actions detects the marker, builds self-contained packages, and creates
the `v1.2.3` GitHub Release attached to that commit. The release contains:

- `osu-replay-viewer-v1.2.3-win-x64.zip`;
- `osu-replay-viewer-v1.2.3-linux-x64.tar.gz`;
- a SHA-256 checksum file for each package.

The Linux archive contains a static GPL FFmpeg 8.1 build under `ffmpeg/ffmpeg`.
Linux always uses that application-local executable and never selects the host
system FFmpeg. This keeps the recording arguments compatible with the release
and avoids depending on the distribution's FFmpeg version.
For a release without creating a marker commit, use **Actions → Build and
publish release → Run workflow** and enter a version such as `v1.2.3`.

You can also build and run directly, using ``dotnet run osu-replay-viewer``

## Troubleshooting
### "No corresponding beatmap for the score could be found"
You need to import the beatmap to your current osu!lazer installation (works best with ranked maps).

## Tips
### Hardware Acceleration
To use hardware acceleration, you need:
- FFmpeg with hardware acceleration
- Compatible hardware (Intel, AMD or NVIDIA GPUs)
- Driver

Set ``video_encoder`` config option to ``h264_<qsv/amf/nvenc/videotoolbox>`` or ``hevc_<qsv/amf/nvenc/videotoolbox>`` to
enable hardware encoding.

Here is the table for hardware encoders:
| Vendor | Encoder           | Codec | Note     |
|--------|-------------------|-------|----------|
| any    | libx264           | H.264 | Uses CPU |
| Intel  | h264_qsv          | H.264 |          |
| AMD    | h264_amf          | H.264 |          |
| NVIDIA | h264_nvenc        | H.264 |          |
| Apple  | h264_videotoolbox | H.264 |          |
| any    | libx265           | HEVC  | Uses CPU |
| Intel  | hevc_qsv          | HEVC  |          |
| AMD    | hevc_amf          | HEVC  |          |
| NVIDIA | hevc_nvenc        | HEVC  |          |
| Apple  | hevc_videotoolbox | HEVC  |          |

## Planned
This is the list of stuffs that I want to changes. It can be planned features or just revamp the code.

- Live Graphs (Live PP, accuracy or difficulty)
- Split CLI system to seperate project (if you're willing to use it)
- Change the project name

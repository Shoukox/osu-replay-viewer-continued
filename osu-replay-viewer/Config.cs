using System;
using System.Drawing;
using Newtonsoft.Json;
using System.IO;
using Newtonsoft.Json.Converters;
using osu_replay_renderer_netcore.CustomHosts;
using osu_replay_renderer_netcore.CustomHosts.Record;

namespace osu_replay_renderer_netcore;

public enum FFmpegMode
{
    Pipe,
    Binding
}

public class GameSettings
{
    [JsonProperty("skip_intro")] public bool SkipIntro = false;
    [JsonProperty("background_dim")] public double BackgroundDim = 0.75;
    [JsonProperty("show_storyboard_or_video")] public bool ShowStoryboard = true;
    [JsonProperty("use_beatmap_hitsounds")] public bool BeatmapHitsounds = false;
    [JsonProperty("use_beatmap_skin")] public bool BeatmapSkin = false;
    [JsonProperty("use_beatmap_colors")] public bool BeatmapColors = false;
    [JsonProperty("music_volume")] public double VolumeMusic = 0.6;
    [JsonProperty("effects_volume")] public double VolumeEffects = 0.6;
    [JsonProperty("master_volume")] public double VolumeMaster = 0.6;
    [JsonProperty("cursor_size")] public double CursorSize = 1.0;
    [JsonProperty("scroll_speed")] public double ScrollSpeed = 25.0;
    [JsonProperty("scroll_direction")] public string ScrollDirection = "down";
}

public class Config
{
    // Zero means that the field was not present in a pre-bootstrap config.
    // New instances are upgraded to the current version by ReadFromFile().
    [JsonProperty("config_version")] public int Version;

    public class RecordOptionsObject
    {
        // 60 fps is the practical default; higher values remain opt-in.
        [JsonProperty("fps")] public int FrameRate = 60;
        [JsonProperty("resolution")] public string Resolution = "1280x720";
        [JsonProperty("renderer")] public GlRenderer Renderer = GlRenderer.Legacy;
    }
    [JsonProperty("record_options")] public RecordOptionsObject RecordOptions = new();

    public class FFmpegOptionsObject
    {
        // Pipe is portable and does not depend on FFmpeg.AutoGen's legacy
        // native library names. Binding remains available for old Windows
        // installations that explicitly keep the compatible libraries.
        [JsonProperty("mode")] public FFmpegMode Mode = FFmpegMode.Pipe;
        [JsonProperty("libraries_path")] public string LibrariesPath = "";
        [JsonProperty("ffmpeg_executable")] public string Executable = "auto";
        [JsonProperty("auto_download")] public bool AutoDownload = true;
        [JsonProperty("download_version")] public string DownloadVersion = "n8.1";
        [JsonProperty("cache_directory")] public string CacheDirectory = "";
        [JsonProperty("allow_encoder_fallback")] public bool AllowEncoderFallback = true;
        [JsonProperty("video_encoder")] public string VideoEncoder = "auto";
        [JsonProperty("video_encoder_preset")] public string VideoEncoderPreset = "auto";
        [JsonProperty("video_encoder_bitrate")] public string VideoEncoderBitrate = "10M";
        [JsonProperty("use_cuda_if_possible")] public bool UseCudaIfPossible = true;
    }
    [JsonProperty("ffmpeg_options")] public FFmpegOptionsObject FFmpegOptions = new();

    public class OutputOptionsObject
    {
        [JsonProperty("pixel_format")] public PixelFormatMode PixelFormat = PixelFormatMode.RGB;
        [JsonProperty("yuv_color_space")] public ColorSpaceMode ColorSpace = ColorSpaceMode.BT709;
    }
    [JsonProperty("output_options")] public OutputOptionsObject OutputOptions = new();

    [JsonProperty("game_settings")] public GameSettings GameSettings = new();

    private Config() { }

    public static Config ReadFromFile(string file)
    {
        Config res;
        if (File.Exists(file))
        {
            var configText = File.ReadAllText(file);
            res = JsonConvert.DeserializeObject<Config>(configText, new StringEnumConverter())
                  ?? throw new InvalidDataException($"Config file '{file}' did not contain a valid JSON object.");
        }
        else
        {
            res = new Config();
        }

        res.MigrateLegacyConfig();

        res.SaveToFile(file); // update schema
        return res;
    }

    private void MigrateLegacyConfig()
    {
        FFmpegOptions ??= new FFmpegOptionsObject();

        if (Version >= 2)
            return;

        // Old configs selected the native binding by default. It is tied to
        // the checked-in Windows FFmpeg 4.x DLLs and cannot work with the
        // downloaded cross-platform FFmpeg builds.
        if (!OperatingSystem.IsWindows() && FFmpegOptions.Mode == FFmpegMode.Binding)
        {
            FFmpegOptions.Mode = FFmpegMode.Pipe;
            FFmpegOptions.LibrariesPath = "";
        }

        if (string.Equals(FFmpegOptions.Executable, "ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FFmpegOptions.Executable, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            FFmpegOptions.Executable = "auto";
        }

        Version = 2;
    }

    public void SaveToFile(string file)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var config = JsonConvert.SerializeObject(this, Formatting.Indented, new StringEnumConverter());
        File.WriteAllText(file, config);
    }
}

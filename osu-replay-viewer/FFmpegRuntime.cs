using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Drawing;

namespace osu_replay_renderer_netcore;

/// <summary>
/// Resolved FFmpeg installation used by the current process.
/// </summary>
public sealed record FFmpegRuntimeInfo(
    string Executable,
    string LibrariesPath,
    FFmpegMode Mode,
    string Encoder,
    string Preset,
    bool HasRubberbandFilter,
    bool IsDownloaded,
    string Platform);

/// <summary>
/// Finds a usable FFmpeg executable, downloads a compatible static build when
/// necessary, and chooses an encoder supported by that executable and host OS.
/// </summary>
public static class FFmpegRuntimeResolver
{
    private const string BtbNBaseUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
    private const string BtbNChecksumsUrl = BtbNBaseUrl + "checksums.sha256";
    private const string DefaultDownloadVersion = "n8.1";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly object DownloadLock = new();

    public static FFmpegRuntimeInfo Resolve(Config.FFmpegOptionsObject options, Size? outputSize = null)
    {
        AssetSpec asset = TryGetAsset(options.DownloadVersion, out AssetSpec resolvedAsset)
            ? resolvedAsset
            : null;

        string executable = ResolveExecutable(options, asset);
        FFmpegCapabilities capabilities = GetCapabilities(executable);

        Size probeSize = NormalizeProbeSize(outputSize ?? new Size(1280, 720));
        string encoder = ResolveEncoder(options, executable, capabilities, probeSize);
        string preset = ResolvePreset(options.VideoEncoderPreset, encoder);

        FFmpegMode mode = options.Mode;
        string librariesPath = mode == FFmpegMode.Binding
            ? ResolveLibrariesPath(options.LibrariesPath)
            : null;
        if (mode == FFmpegMode.Binding && (!OperatingSystem.IsWindows() || librariesPath is null))
        {
            Console.WriteLine(
                "[FFmpeg] The native binding is only available with the legacy libraries on Windows; using the pipe backend.");
            mode = FFmpegMode.Pipe;
            librariesPath = null;
        }

        Console.WriteLine($"[FFmpeg] Executable: {executable}");
        Console.WriteLine($"[FFmpeg] Platform: {GetPlatformIdentifier()}, mode: {mode}, encoder: {encoder}");
        if (!capabilities.HasRubberbandFilter)
        {
            Console.WriteLine(
                "[FFmpeg] Warning: the selected FFmpeg has no rubberband filter; pitch/speed audio processing may fail.");
        }

        return new FFmpegRuntimeInfo(
            executable,
            librariesPath,
            mode,
            encoder,
            preset,
            capabilities.HasRubberbandFilter,
            asset is not null && IsPathInsideCache(executable, GetCacheRoot(options)),
            GetPlatformIdentifier());
    }

    private static string ResolveExecutable(Config.FFmpegOptionsObject options, AssetSpec asset)
    {
        // Windows and Linux releases ship a compatible FFmpeg under the
        // application directory. Do not let a system FFmpeg silently
        // override it: the recording argument contract is tied to the bundle.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            return ResolveBundledExecutable();

        string configured = options.Executable?.Trim() ?? string.Empty;
        bool automatic = string.IsNullOrWhiteSpace(configured) ||
                         configured.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                         configured.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
                         configured.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);

        if (!automatic)
        {
            if (TryProbeExecutable(configured, out _))
                return configured;

            if (!options.AutoDownload)
            {
                throw new InvalidOperationException(
                    $"Configured FFmpeg executable '{configured}' was not found or could not be started.");
            }

            Console.WriteLine(
                $"[FFmpeg] Configured executable '{configured}' is unavailable; falling back to automatic discovery.");
        }

        var candidates = new List<string>();
        candidates.AddRange(GetApplicationExecutables());

        if (asset is not null)
        {
            string cached = FindCachedExecutable(options, asset);
            if (cached is not null)
                candidates.Add(cached);
        }

        candidates.Add(GetExecutableName());

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryProbeExecutable(candidate, out _))
            {
                Console.WriteLine($"[FFmpeg] Using existing executable '{candidate}'.");
                return candidate;
            }
        }

        if (!options.AutoDownload)
        {
            throw new InvalidOperationException(
                "FFmpeg was not found. Install FFmpeg and add it to PATH, set ffmpeg_executable, " +
                "or enable ffmpeg_options.auto_download.");
        }

        if (asset is null)
        {
            throw new InvalidOperationException(
                $"Automatic FFmpeg download is not available for {GetPlatformIdentifier()} with architecture " +
                $"{RuntimeInformation.ProcessArchitecture}. Install FFmpeg and set ffmpeg_executable manually.");
        }

        return DownloadAndInstall(options, asset);
    }

    private static string ResolveBundledExecutable()
    {
        string bundledDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

        foreach (string candidate in GetApplicationExecutables()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                MakeExecutableOnUnix(candidate);
            }
            catch
            {
                // Probe below reports the actionable failure if permissions
                // cannot be repaired.
            }

            if (TryProbeExecutable(candidate, out _))
            {
                string resolved = Path.GetFullPath(candidate);
                Console.WriteLine($"[FFmpeg] Bundled executable: {resolved}");
                return resolved;
            }
        }

        throw new InvalidOperationException(
            $"{GetPlatformIdentifier()} requires the bundled FFmpeg under '{bundledDirectory}'. " +
            "The release is incomplete or the bundled executable cannot be started.");
    }

    private static string ResolveEncoder(
        Config.FFmpegOptionsObject options,
        string executable,
        FFmpegCapabilities capabilities,
        Size probeSize)
    {
        string requested = options.VideoEncoder?.Trim() ?? string.Empty;
        bool automatic = string.IsNullOrWhiteSpace(requested) ||
                         requested.Equals("auto", StringComparison.OrdinalIgnoreCase);

        IEnumerable<string> candidates = automatic
            ? GetPreferredEncoders(options)
            : new[] { requested };

        foreach (string candidate in candidates.Concat(new[] { "libx264" })
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!capabilities.Encoders.Contains(candidate))
                continue;

            if (TryProbeEncoder(executable, candidate, probeSize))
                return candidate;
        }

        if (!options.AllowEncoderFallback && !automatic)
        {
            throw new InvalidOperationException(
                $"FFmpeg encoder '{requested}' is not available or cannot be initialized on this machine.");
        }

        throw new InvalidOperationException(
            "FFmpeg has no usable H.264 encoder. Install a GPL build containing libx264 or configure another encoder.");
    }

    private static IEnumerable<string> GetPreferredEncoders(Config.FFmpegOptionsObject options)
    {
        if (options.UseCudaIfPossible && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            yield return "h264_nvenc";

        if (OperatingSystem.IsWindows())
        {
            yield return "h264_amf";
            yield return "h264_qsv";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "h264_qsv";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "h264_videotoolbox";
        }

        yield return "libx264";
    }

    private static string ResolvePreset(string configuredPreset, string encoder)
    {
        string preset = configuredPreset?.Trim() ?? string.Empty;
        bool automatic = string.IsNullOrWhiteSpace(preset) ||
                         preset.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                         preset.Equals("p1", StringComparison.OrdinalIgnoreCase) &&
                         !encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);

        if (!automatic)
            return preset;

        return encoder switch
        {
            "h264_nvenc" or "hevc_nvenc" => "p1",
            "h264_amf" or "hevc_amf" => "speed",
            _ => "medium"
        };
    }

    private static FFmpegCapabilities GetCapabilities(string executable)
    {
        ProcessResult encodersResult = RunProcess(executable, "-hide_banner", "-encoders");
        if (encodersResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not inspect FFmpeg encoders at '{executable}'.{Environment.NewLine}{encodersResult.Error}");
        }

        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (encodersResult.Output + Environment.NewLine + encodersResult.Error).Split('\n'))
        {
            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length >= 1 && parts[1].Length > 0)
                encoders.Add(parts[1]);
        }

        ProcessResult filtersResult = RunProcess(executable, "-hide_banner", "-filters");
        bool hasRubberband = filtersResult.ExitCode == 0 &&
                             (filtersResult.Output + Environment.NewLine + filtersResult.Error).Split('\n').Any(line =>
                                 line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                     .Any(part => part.Equals("rubberband", StringComparison.OrdinalIgnoreCase)));

        return new FFmpegCapabilities(encoders, hasRubberband);
    }

    private static bool TryProbeEncoder(string executable, string encoder, Size probeSize)
    {
        ProcessResult result = RunProcess(
            executable,
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"color=c=black:s={probeSize.Width}x{probeSize.Height}:r=1",
            "-frames:v", "2",
            "-an",
            "-c:v", encoder,
            "-pix_fmt", "yuv420p",
            "-f", "null",
            "-");

        if (result.ExitCode == 0)
            return true;

        Console.WriteLine($"[FFmpeg] Encoder '{encoder}' is unavailable: {FirstLine(result.Error)}");
        return false;
    }

    private static Size NormalizeProbeSize(Size size)
    {
        int width = Math.Max(128, size.Width);
        int height = Math.Max(128, size.Height);

        // All supported recording paths eventually produce 4:2:0 H.264.
        // Keep the probe dimensions valid even when a custom config uses an
        // odd or very small recording surface.
        if ((width & 1) != 0)
            width++;
        if ((height & 1) != 0)
            height++;

        return new Size(width, height);
    }

    private static bool TryProbeExecutable(string executable, out ProcessResult result)
    {
        try
        {
            result = RunProcess(executable, "-hide_banner", "-version");
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          UnauthorizedAccessException or ArgumentException or TimeoutException)
        {
            result = new ProcessResult(-1, string.Empty, exception.Message);
            return false;
        }
    }

    private static string DownloadAndInstall(Config.FFmpegOptionsObject options, AssetSpec asset)
    {
        lock (DownloadLock)
        {
            string installDirectory = Path.Combine(GetCacheRoot(options), GetDownloadVersion(options.DownloadVersion), asset.CacheKey);
            string existing = FindExecutableInDirectory(installDirectory);
            if (existing is not null && TryProbeExecutable(existing, out _))
                return existing;

            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);
            string archivePath = Path.Combine(
                Path.GetDirectoryName(installDirectory)!,
                $"{asset.ArchiveName}.{Guid.NewGuid():N}.download");
            string stagingDirectory = installDirectory + $".staging-{Guid.NewGuid():N}";

            try
            {
                Console.WriteLine($"[FFmpeg] Downloading {asset.ArchiveName} for {asset.Platform}...");
                DownloadFile(asset.DownloadUrl, archivePath);
                VerifyChecksumIfAvailable(asset, archivePath);

                Directory.CreateDirectory(stagingDirectory);
                ExtractArchive(asset, archivePath, stagingDirectory);

                string extractedExecutable = FindExecutableInDirectory(stagingDirectory);
                if (extractedExecutable is null)
                    throw new InvalidDataException(
                        $"The downloaded FFmpeg archive did not contain {GetExecutableName()}.");

                if (Directory.Exists(installDirectory))
                    Directory.Delete(installDirectory, recursive: true);

                Directory.Move(stagingDirectory, installDirectory);
                string installedExecutable = FindExecutableInDirectory(installDirectory)!;
                MakeExecutableOnUnix(installedExecutable);
                if (!TryProbeExecutable(installedExecutable, out ProcessResult probe))
                {
                    throw new InvalidOperationException(
                        $"Downloaded FFmpeg could not be started.{Environment.NewLine}{probe.Error}");
                }

                Console.WriteLine($"[FFmpeg] Installed to '{installedExecutable}'.");
                return installedExecutable;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[FFmpeg] Download failed: {exception.Message}");
                throw new InvalidOperationException(
                    "FFmpeg is required for recording. Install it manually or fix the automatic download and retry.",
                    exception);
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    private static void DownloadFile(Uri url, string destination)
    {
        using HttpResponseMessage response = HttpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();
        response.EnsureSuccessStatusCode();

        using Stream input = response.Content.ReadAsStream();
        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void VerifyChecksumIfAvailable(AssetSpec asset, string archivePath)
    {
        if (asset.ChecksumUrl is null)
            return;

        string checksums = HttpClient.GetStringAsync(asset.ChecksumUrl).GetAwaiter().GetResult();
        string expected = checksums
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Where(parts => Path.GetFileName(parts[1].TrimStart('*'))
                .Equals(asset.ArchiveName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidDataException($"No checksum was published for {asset.ArchiveName}.");

        using FileStream stream = File.OpenRead(archivePath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Checksum mismatch for {asset.ArchiveName}. Expected {expected}, got {actual}.");
        }
    }

    private static void ExtractArchive(AssetSpec asset, string archivePath, string destination)
    {
        if (asset.IsZip)
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The FFmpeg archive contains an unsafe path.");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            return;
        }

        ProcessResult result = RunProcess(
            "tar",
            "-xJf",
            archivePath,
            "-C",
            destination);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not extract the FFmpeg archive with tar.{Environment.NewLine}{result.Error}");
        }
    }

    private static string FindCachedExecutable(Config.FFmpegOptionsObject options, AssetSpec asset) =>
        FindExecutableInDirectory(Path.Combine(GetCacheRoot(options), GetDownloadVersion(options.DownloadVersion), asset.CacheKey));

    private static string FindExecutableInDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        string name = GetExecutableName();
        return Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string ResolveLibrariesPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        string[] candidates = Path.IsPathRooted(configuredPath)
            ? new[] { configuredPath }
            : new[]
            {
                configuredPath,
                Path.Combine(AppContext.BaseDirectory, configuredPath)
            };

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(candidate))
                continue;

            bool hasCodec = Directory.EnumerateFiles(candidate, "avcodec-*", SearchOption.TopDirectoryOnly).Any();
            bool hasFormat = Directory.EnumerateFiles(candidate, "avformat-*", SearchOption.TopDirectoryOnly).Any();
            if (hasCodec && hasFormat)
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> GetApplicationExecutables()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        string executableName = GetExecutableName();
        if (File.Exists(Path.Combine(directory, executableName)))
            yield return Path.Combine(directory, executableName);

        if (Directory.Exists(directory))
        {
            foreach (string executable in Directory.EnumerateFiles(directory, executableName, SearchOption.AllDirectories))
                yield return executable;
        }
    }

    private static string GetExecutableName() => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    private static string GetPlatformIdentifier()
    {
        string os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : "unknown";

        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"{os}-{architecture}";
    }

    private static string GetCacheRoot(Config.FFmpegOptionsObject options)
    {
        if (!string.IsNullOrWhiteSpace(options.CacheDirectory))
        {
            string custom = options.CacheDirectory.Trim();
            if (custom.StartsWith("~/", StringComparison.Ordinal) || custom.StartsWith("~\\", StringComparison.Ordinal))
            {
                custom = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), custom[2..]);
            }

            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(custom));
        }

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        }
        else
        {
            root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        return Path.Combine(root, "osu-replay-viewer", "ffmpeg");
    }

    private static bool IsPathInsideCache(string path, string cacheRoot)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetAsset(string version, out AssetSpec asset)
    {
        string selectedVersion = GetDownloadVersion(version);
        string platform = GetPlatformIdentifier();

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            if (selectedVersion is not ("master" or "n8.1" or "n7.1"))
            {
                asset = null;
                return false;
            }

            asset = new AssetSpec(
                "macos-x64-latest",
                "ffmpeg-macos-x64-latest.zip",
                new Uri("https://evermeet.cx/ffmpeg/getrelease/zip"),
                ChecksumUrl: null,
                IsZip: true,
                platform);
            return true;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            asset = null;
            return false;
        }

        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "64",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        if (architecture is null)
        {
            asset = null;
            return false;
        }

        string suffix = selectedVersion.ToLowerInvariant() switch
        {
            "master" => "master-latest",
            "n8.1" or "8.1" => "n8.1-latest",
            "n7.1" or "7.1" => "n7.1-latest",
            _ => null
        };
        if (suffix is null)
        {
            asset = null;
            return false;
        }

        string rid = OperatingSystem.IsWindows() ? $"win{architecture}" : $"linux{architecture}";
        string extension = OperatingSystem.IsWindows() ? "zip" : "tar.xz";
        string versionSuffix = selectedVersion.ToLowerInvariant() switch
        {
            "master" => string.Empty,
            "n8.1" or "8.1" => "-8.1",
            "n7.1" or "7.1" => "-7.1",
            _ => null
        };
        string archiveName = $"ffmpeg-{suffix}-{rid}-gpl{versionSuffix}.{extension}";
        asset = new AssetSpec(
            $"{rid}-{selectedVersion.ToLowerInvariant()}-gpl",
            archiveName,
            new Uri(BtbNBaseUrl + archiveName),
            new Uri(BtbNChecksumsUrl),
            IsZip: OperatingSystem.IsWindows(),
            platform);
        return true;
    }

    private static string GetDownloadVersion(string version)
    {
        return (version ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" => DefaultDownloadVersion,
            "8.1" => "n8.1",
            "7.1" => "n7.1",
            "n8.1" => "n8.1",
            "n7.1" => "n7.1",
            "master" => "master",
            _ => version.Trim()
        };
    }

    private static ProcessResult RunProcess(string executable, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"FFmpeg process '{executable}' did not exit in time.");
        }

        Task.WaitAll(outputTask, errorTask);
        return new ProcessResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static string FirstLine(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown error";

    private static void MakeExecutableOnUnix(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("osu-replay-viewer/ffmpeg-bootstrap");
        return client;
    }

    private sealed record AssetSpec(
        string CacheKey,
        string ArchiveName,
        Uri DownloadUrl,
        Uri ChecksumUrl,
        bool IsZip,
        string Platform);

    private sealed record FFmpegCapabilities(
        IReadOnlySet<string> Encoders,
        bool HasRubberbandFilter);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

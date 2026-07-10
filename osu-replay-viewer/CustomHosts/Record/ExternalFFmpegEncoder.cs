using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public class ExternalFFmpegEncoder : EncoderBase
    {
        private Process FFmpeg { get; set; }
        private Stream InputStream { get; set; }
        private readonly StringBuilder errorBuilder = new();
        private readonly object errorLock = new();

        private IReadOnlyList<string> FFmpegArgumentList
        {
            get
            {
                string pixFmt = Config.PixelFormat switch
                {
                    PixelFormatMode.YUV420 => "yuv420p",
                    PixelFormatMode.YUV444 => "yuv444p",
                    PixelFormatMode.NV12 => "nv12",
                    _ => "rgb24"
                };

                string outputPixFmt = Config.PixelFormat switch
                {
                    PixelFormatMode.YUV420 => "yuv420p",
                    PixelFormatMode.YUV444 => "yuv444p",
                    PixelFormatMode.NV12 => "nv12",
                    _ => "yuv420p" // RGB input gets converted to yuv420p by FFmpeg
                };

                var args = new List<string>
                {
                    "-y",
                    "-f", "rawvideo",
                    "-pix_fmt", pixFmt,
                    "-s", $"{Config.Resolution.Width}x{Config.Resolution.Height}",
                    "-framerate", Config.FPS.ToString(CultureInfo.InvariantCulture),
                    "-i", "pipe:",
                    "-c:v", Config.Encoder
                };

                if (Config.PixelFormat == PixelFormatMode.RGB)
                {
                    args.Add("-vf");
                    args.Add("vflip");
                }

                int cqr = 28;
                switch (Config.Encoder)
                {
                    case "h264_nvenc":
                        args.AddRange(new[] { "-rc", "vbr", "-cq", cqr.ToString(), "-b:v", "0" });
                        break;
                    case "libx264":
                    case "h264_amf":
                    case "h264_qsv":
                    case "h264_videotoolbox":
                        args.AddRange(new[] { "-crf", cqr.ToString() });
                        break;
                }

                if (Config.PixelFormat != PixelFormatMode.RGB)
                {
                    switch (Config.ColorSpace)
                    {
                        case ColorSpaceMode.BT601:
                            args.AddRange(new[] { "-colorspace", "bt470bg", "-color_primaries", "bt470bg", "-color_trc", "gamma22", "-color_range", "pc" });
                            break;
                        case ColorSpaceMode.BT709:
                            args.AddRange(new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "pc" });
                            break;
                    }
                }

                args.AddRange(new[] { "-pix_fmt", outputPixFmt });

                if (!string.IsNullOrWhiteSpace(Config.Preset))
                    args.AddRange(new[] { "-preset", Config.Preset });

                // The input is already a fixed-step stream. Make the output
                // CFR contract explicit so no muxer/player is allowed to
                // reinterpret the raw-video timestamps.
                args.AddRange(new[]
                {
                    "-fps_mode", "cfr",
                    "-r", Config.FPS.ToString(CultureInfo.InvariantCulture)
                });

                if (Path.GetExtension(Config.OutputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(Config.OutputPath).Equals(".mov", StringComparison.OrdinalIgnoreCase))
                {
                    args.AddRange(new[] { "-movflags", "+faststart" });
                }

                args.Add(Config.OutputPath);
                return args;
            }
        }

        public override bool CanWrite => InputStream is not null && InputStream.CanWrite;

        public ExternalFFmpegEncoder(EncoderConfig config) : base(config) { }

        protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
        {
            InputStream.Write(frame);
        }

        protected override void _finishInternal()
        {
            if (FFmpeg == null)
                return;

            Exception closeException = null;

            try
            {
                InputStream?.Flush();
                InputStream?.Dispose();
            }
            catch (Exception ex)
            {
                closeException = ex;
            }
            finally
            {
                InputStream = null;
            }

            FFmpeg.WaitForExit();

            int exitCode = FFmpeg.ExitCode;
            FFmpeg.CancelErrorRead();
            FFmpeg.Dispose();
            FFmpeg = null;

            if (closeException != null)
                throw new IOException("Failed while closing FFmpeg video input stream.", closeException);

            if (exitCode != 0)
            {
                string errorLog;
                lock (errorLock)
                    errorLog = errorBuilder.ToString();

                throw new InvalidOperationException(
                    $"FFmpeg video encoder exited with code {exitCode}.{Environment.NewLine}{errorLog}");
            }
        }

        protected override void _startInternal()
        {
            if (FFmpeg != null)
                throw new InvalidOperationException("Video encoder has already been started.");

            var args = FFmpegArgumentList;
            Console.WriteLine("Starting FFmpeg process with arguments: " + string.Join(" ", args));

            lock (errorLock)
                errorBuilder.Clear();

            FFmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = Config.FFmpegExec,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            foreach (var arg in args)
                FFmpeg.StartInfo.ArgumentList.Add(arg);

            FFmpeg.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    lock (errorLock)
                        errorBuilder.AppendLine(e.Data);
                }
            };

            try
            {
                FFmpeg.Start();
                FFmpeg.BeginErrorReadLine();
                InputStream = FFmpeg.StandardInput.BaseStream;
            }
            catch
            {
                FFmpeg?.Dispose();
                FFmpeg = null;
                InputStream = null;
                throw;
            }
        }
    }
}

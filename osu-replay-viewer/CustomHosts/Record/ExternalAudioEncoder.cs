using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public class ExternalAudioEncoder : IDisposable
    {
        public string OutputPath { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }

        private Process FFmpeg { get; set; }
        private Stream InputStream { get; set; }
        private readonly StringBuilder errorBuilder = new();
        private readonly object errorLock = new();

        private string FFmpegExec = "ffmpeg";

        public ExternalAudioEncoder(string outputPath, int sampleRate, int channels, string ffmpegExec = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path cannot be null or empty.", nameof(outputPath));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be positive.");

            OutputPath = outputPath;
            SampleRate = sampleRate;
            Channels = channels;

            if (!string.IsNullOrWhiteSpace(ffmpegExec))
                FFmpegExec = ffmpegExec;
        }

        public void Start()
        {
            if (FFmpeg != null)
                throw new InvalidOperationException("Audio encoder has already been started.");

            string args = $"-y -f s16le -ar {SampleRate} -ac {Channels} -i pipe: -c:a aac -b:a 192k \"{OutputPath}\"";

            Console.WriteLine("Starting Audio FFmpeg process with arguments: " + args);

            lock (errorLock)
                errorBuilder.Clear();

            FFmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = FFmpegExec,
                    Arguments = args,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

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

        public void Write(float[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            EnsureStarted();

            byte[] buffer = new byte[data.Length * sizeof(short)];
            for (int i = 0; i < data.Length; i++)
            {
                short sample = (short)(Math.Clamp(data[i], -1f, 1f) * short.MaxValue);
                buffer[i * 2] = (byte)(sample & 0xff);
                buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }

            WritePcm16(buffer);
        }

        public void Write(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            WritePcm16(data);
        }

        private void WritePcm16(byte[] data)
        {
            EnsureStarted();

            int bytesPerFrame = Channels * sizeof(short);
            if (data.Length % bytesPerFrame != 0)
            {
                throw new ArgumentException(
                    $"PCM buffer length ({data.Length}) is not aligned to {bytesPerFrame}-byte audio frames for {Channels} channel(s).",
                    nameof(data));
            }

            InputStream.Write(data, 0, data.Length);
        }

        public void Finish()
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
                throw new IOException("Failed while closing FFmpeg audio input stream.", closeException);

            if (exitCode != 0)
            {
                string errorLog;
                lock (errorLock)
                    errorLog = errorBuilder.ToString();

                throw new InvalidOperationException(
                    $"FFmpeg audio encoder exited with code {exitCode}.{Environment.NewLine}{errorLog}");
            }
        }

        private void EnsureStarted()
        {
            if (FFmpeg == null || InputStream == null)
                throw new InvalidOperationException("Audio encoder has not been started or has already been finished.");
        }

        public void Dispose()
        {
            if (FFmpeg == null && InputStream == null)
                return;

            Finish();
        }
    }
}

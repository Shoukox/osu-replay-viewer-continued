using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public unsafe class FFmpegAutoGenEncoder : EncoderBase
    {
        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private SwsContext* _swsContext;
        private AVStream* _videoStream;
        private AVFrame* _frame;

        private int _pts;
        private long _bitrate;
        private byte[] _pixelBuffer;

        public override bool CanWrite =>
            _formatContext != null &&
            ((_formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) != 0 || _formatContext->pb != null);

        public FFmpegAutoGenEncoder(EncoderConfig config) : base(config)
        {
            if (!string.IsNullOrWhiteSpace(Config.FFmpegPath))
            {
                string ffmpegPath = Path.IsPathRooted(Config.FFmpegPath)
                    ? Config.FFmpegPath
                    : Path.Combine(AppContext.BaseDirectory, Config.FFmpegPath);

                ffmpeg.RootPath = ffmpegPath;
            }
        }

        protected override void _startInternal()
        {
            ffmpeg.avformat_network_init();

            if (Config.PixelFormat == PixelFormatMode.RGB)
            {
                int bufferSize = Config.Resolution.Width * Config.Resolution.Height * 3;
                _pixelBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            }

            fixed (AVFormatContext** ctx = &_formatContext)
            {
                ThrowIfError(
                    ffmpeg.avformat_alloc_output_context2(ctx, null, null, Config.OutputPath),
                    "Could not create output context");
            }

            if (_formatContext == null)
                throw new InvalidOperationException("Could not create output context");

            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(Config.Encoder);
            if (codec == null)
                throw new InvalidOperationException($"Codec '{Config.Encoder}' not found");

            _videoStream = ffmpeg.avformat_new_stream(_formatContext, codec);
            if (_videoStream == null)
                throw new InvalidOperationException("Failed to create video stream");

            _videoStream->id = (int)_formatContext->nb_streams - 1;

            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
                throw new InvalidOperationException("Failed to allocate codec context");

            _bitrate = ParseBitrate(Config.Bitrate);

            _codecContext->bit_rate = _bitrate;
            _codecContext->width = Config.Resolution.Width;
            _codecContext->height = Config.Resolution.Height;
            _codecContext->time_base = new AVRational { num = 1, den = Config.FPS };
            _codecContext->framerate = new AVRational { num = Config.FPS, den = 1 };
            _codecContext->pix_fmt = GetAVPixelFormat(Config.PixelFormat);
            _codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;

            _videoStream->time_base = _codecContext->time_base;

            if ((_formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            SetColorMetadata();

            Console.WriteLine(
                $"[FFmpegAutoGenEncoder] Pixel format: {Config.PixelFormat} -> {_codecContext->pix_fmt}, Color space: {Config.ColorSpace}");

            AVDictionary* opts = null;

            try
            {
                foreach (var pair in CreateEncoderOptions())
                {
                    ffmpeg.av_dict_set(&opts, pair.Key, pair.Value, 0);
                }

                ThrowIfError(
                    ffmpeg.avcodec_open2(_codecContext, codec, &opts),
                    "Failed to open codec");
            }
            finally
            {
                ffmpeg.av_dict_free(&opts);
            }

            ThrowIfError(
                ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecContext),
                "Failed to copy codec parameters to stream");

            _frame = ffmpeg.av_frame_alloc();
            if (_frame == null)
                throw new InvalidOperationException("Failed to allocate frame");

            _frame->format = (int)_codecContext->pix_fmt;
            _frame->width = _codecContext->width;
            _frame->height = _codecContext->height;

            ThrowIfError(
                ffmpeg.av_frame_get_buffer(_frame, 32),
                "Failed to allocate frame buffer");

            if (Config.PixelFormat == PixelFormatMode.RGB)
            {
                _swsContext = ffmpeg.sws_getContext(
                    Config.Resolution.Width,
                    Config.Resolution.Height,
                    AVPixelFormat.AV_PIX_FMT_RGB24,
                    Config.Resolution.Width,
                    Config.Resolution.Height,
                    AVPixelFormat.AV_PIX_FMT_YUV420P,
                    ffmpeg.SWS_POINT,
                    null,
                    null,
                    null);

                if (_swsContext == null)
                    throw new InvalidOperationException("Failed to create SWS context");
            }

            if ((_formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfError(
                    ffmpeg.avio_open(&_formatContext->pb, Config.OutputPath, ffmpeg.AVIO_FLAG_WRITE),
                    "Could not open output file");
            }

            ThrowIfError(
                ffmpeg.avformat_write_header(_formatContext, null),
                "Failed to write output header");
        }

        protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
        {
            ValidateFrameSize(frame);

            ThrowIfError(
                ffmpeg.av_frame_make_writable(_frame),
                "Frame is not writable");

            fixed (byte* framePtr = frame)
            {
                switch (Config.PixelFormat)
                {
                    case PixelFormatMode.RGB:
                        ConvertRgbToYuv420(framePtr, frame.Length);
                        break;

                    case PixelFormatMode.YUV420:
                        CopyYUV420P(framePtr);
                        break;

                    case PixelFormatMode.YUV444:
                        CopyYUV444P(framePtr);
                        break;

                    case PixelFormatMode.NV12:
                        CopyNV12(framePtr);
                        break;

                    default:
                        throw new NotSupportedException($"Unsupported pixel format: {Config.PixelFormat}");
                }

                _frame->pts = _pts++;

                ThrowIfError(
                    ffmpeg.avcodec_send_frame(_codecContext, _frame),
                    "Failed to send frame to encoder");

                ReceiveAndWritePackets();
            }
        }

        protected override void _finishInternal()
        {
            try
            {
                FlushEncoder();

                if (_formatContext != null)
                {
                    ThrowIfError(
                        ffmpeg.av_write_trailer(_formatContext),
                        "Failed to write output trailer");
                }
            }
            finally
            {
                Cleanup();
            }
        }

        private void ConvertRgbToYuv420(byte* framePtr, int frameLength)
        {
            if (_pixelBuffer == null)
                throw new InvalidOperationException("RGB pixel buffer was not allocated");

            if (_swsContext == null)
                throw new InvalidOperationException("SWS context was not created");

            fixed (byte* srcPtr = _pixelBuffer)
            {
                Buffer.MemoryCopy(framePtr, srcPtr, _pixelBuffer.Length, frameLength);

                int srcStride = Config.Resolution.Width * 3;

                byte_ptrArray4 srcData = new byte_ptrArray4();
                int_array4 srcLinesize = new int_array4();

                // Vertical flip: start at last row and use negative stride.
                srcData[0] = srcPtr + (Config.Resolution.Height - 1) * srcStride;
                srcLinesize[0] = -srcStride;

                int scaledHeight = ffmpeg.sws_scale(
                    _swsContext,
                    srcData,
                    srcLinesize,
                    0,
                    Config.Resolution.Height,
                    _frame->data,
                    _frame->linesize);

                if (scaledHeight != Config.Resolution.Height)
                {
                    throw new InvalidOperationException(
                        $"sws_scale failed. Expected {Config.Resolution.Height} lines, got {scaledHeight}.");
                }
            }
        }

        private void CopyYUV420P(byte* srcPtr)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;

            int ySize = width * height;
            int uvSize = width * height / 4;

            byte* ySrc = srcPtr;
            byte* uSrc = srcPtr + ySize;
            byte* vSrc = srcPtr + ySize + uvSize;

            CopyPlane(ySrc, _frame->data[0], width, height, width, _frame->linesize[0]);
            CopyPlane(uSrc, _frame->data[1], width / 2, height / 2, width / 2, _frame->linesize[1]);
            CopyPlane(vSrc, _frame->data[2], width / 2, height / 2, width / 2, _frame->linesize[2]);
        }

        private void CopyYUV444P(byte* srcPtr)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;

            int planeSize = width * height;

            byte* ySrc = srcPtr;
            byte* uSrc = srcPtr + planeSize;
            byte* vSrc = srcPtr + planeSize * 2;

            CopyPlane(ySrc, _frame->data[0], width, height, width, _frame->linesize[0]);
            CopyPlane(uSrc, _frame->data[1], width, height, width, _frame->linesize[1]);
            CopyPlane(vSrc, _frame->data[2], width, height, width, _frame->linesize[2]);
        }

        private void CopyNV12(byte* srcPtr)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;

            int ySize = width * height;

            byte* ySrc = srcPtr;
            byte* uvSrc = srcPtr + ySize;

            CopyPlane(ySrc, _frame->data[0], width, height, width, _frame->linesize[0]);
            CopyPlane(uvSrc, _frame->data[1], width, height / 2, width, _frame->linesize[1]);
        }

        private static void CopyPlane(
            byte* src,
            byte* dst,
            int rowBytes,
            int height,
            int srcStride,
            int dstStride)
        {
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    src + y * srcStride,
                    dst + y * dstStride,
                    dstStride,
                    rowBytes);
            }
        }

        private void ReceiveAndWritePackets()
        {
            AVPacket* packet = ffmpeg.av_packet_alloc();
            if (packet == null)
                throw new InvalidOperationException("Failed to allocate packet");

            try
            {
                while (true)
                {
                    int ret = ffmpeg.avcodec_receive_packet(_codecContext, packet);

                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;

                    ThrowIfError(ret, "Error while receiving encoded packet");

                    ffmpeg.av_packet_rescale_ts(
                        packet,
                        _codecContext->time_base,
                        _videoStream->time_base);

                    packet->stream_index = _videoStream->index;

                    ThrowIfError(
                        ffmpeg.av_interleaved_write_frame(_formatContext, packet),
                        "Failed to write encoded packet");

                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }
        }

        private void FlushEncoder()
        {
            if (_codecContext == null || _formatContext == null || _videoStream == null)
                return;

            int sendRet = ffmpeg.avcodec_send_frame(_codecContext, null);

            if (sendRet < 0 && sendRet != ffmpeg.AVERROR_EOF)
            {
                ThrowIfError(sendRet, "Failed to flush encoder");
            }

            AVPacket* packet = ffmpeg.av_packet_alloc();
            if (packet == null)
                throw new InvalidOperationException("Failed to allocate flush packet");

            try
            {
                while (true)
                {
                    int ret = ffmpeg.avcodec_receive_packet(_codecContext, packet);

                    if (ret == ffmpeg.AVERROR_EOF || ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        break;

                    ThrowIfError(ret, "Error while flushing encoder");

                    ffmpeg.av_packet_rescale_ts(
                        packet,
                        _codecContext->time_base,
                        _videoStream->time_base);

                    packet->stream_index = _videoStream->index;

                    ThrowIfError(
                        ffmpeg.av_interleaved_write_frame(_formatContext, packet),
                        "Failed to write flushed packet");

                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }
        }

        private void Cleanup()
        {
            if (_codecContext != null)
            {
                fixed (AVCodecContext** ctx = &_codecContext)
                {
                    ffmpeg.avcodec_free_context(ctx);
                }
            }

            if (_frame != null)
            {
                fixed (AVFrame** frame = &_frame)
                {
                    ffmpeg.av_frame_free(frame);
                }
            }

            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
            }

            if (_formatContext != null)
            {
                if (_formatContext->pb != null)
                {
                    ffmpeg.avio_closep(&_formatContext->pb);
                }

                ffmpeg.avformat_free_context(_formatContext);
            }

            if (_pixelBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_pixelBuffer);
                _pixelBuffer = null;
            }

            _formatContext = null;
            _codecContext = null;
            _swsContext = null;
            _videoStream = null;
            _frame = null;
            _pts = 0;
        }

        private void ValidateFrameSize(ReadOnlySpan<byte> frame)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;

            int expectedSize = Config.PixelFormat switch
            {
                PixelFormatMode.RGB => width * height * 3,
                PixelFormatMode.YUV420 => width * height * 3 / 2,
                PixelFormatMode.NV12 => width * height * 3 / 2,
                PixelFormatMode.YUV444 => width * height * 3,
                _ => throw new NotSupportedException($"Unsupported pixel format: {Config.PixelFormat}")
            };

            if (frame.Length < expectedSize)
            {
                throw new ArgumentException(
                    $"Frame is too small. Expected at least {expectedSize} bytes, got {frame.Length} bytes.");
            }
        }

        private void SetColorMetadata()
        {
            // RGB input is converted to YUV420P, so the encoded output is still YUV.
            if (Config.ColorSpace == ColorSpaceMode.BT601)
            {
                _codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT470BG;
                _codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT470BG;
                _codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22;
            }
            else
            {
                _codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                _codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                _codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            }

            // Your original code used full range JPEG.
            // Keep this only if your YUV shader really produces full-range YUV.
            _codecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        }

        private Dictionary<string, string> CreateEncoderOptions()
        {
            var dict = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(Config.Preset))
            {
                dict["preset"] = Config.Preset;
            }

            switch (Config.Encoder)
            {
                case "h264_nvenc":
                    dict["rc"] = "constqp";
                    dict["qp"] = "21";
                    break;

                case "libx264":
                    dict["crf"] = "21";
                    break;

                case "h264_amf":
                case "h264_qsv":
                case "h264_videotoolbox":
                    dict["crf"] = "21";
                    break;
            }

            return dict;
        }

        private AVPixelFormat GetAVPixelFormat(PixelFormatMode mode)
        {
            return mode switch
            {
                PixelFormatMode.RGB => AVPixelFormat.AV_PIX_FMT_YUV420P,
                PixelFormatMode.YUV420 => AVPixelFormat.AV_PIX_FMT_YUV420P,
                PixelFormatMode.YUV444 => AVPixelFormat.AV_PIX_FMT_YUV444P,
                PixelFormatMode.NV12 => AVPixelFormat.AV_PIX_FMT_NV12,
                _ => AVPixelFormat.AV_PIX_FMT_YUV420P
            };
        }

        private long ParseBitrate(string bitrateStr)
        {
            if (string.IsNullOrWhiteSpace(bitrateStr))
                return 10_000_000;

            bitrateStr = bitrateStr
                .Trim()
                .ToUpperInvariant()
                .TrimEnd('B');

            long multiplier = 1;

            if (bitrateStr.EndsWith("K"))
            {
                multiplier = 1_000;
                bitrateStr = bitrateStr.TrimEnd('K');
            }
            else if (bitrateStr.EndsWith("M"))
            {
                multiplier = 1_000_000;
                bitrateStr = bitrateStr.TrimEnd('M');
            }
            else if (bitrateStr.EndsWith("G"))
            {
                multiplier = 1_000_000_000;
                bitrateStr = bitrateStr.TrimEnd('G');
            }

            return long.TryParse(bitrateStr, out long value)
                ? value * multiplier
                : 10_000_000;
        }

        private static void ThrowIfError(int error, string message)
        {
            if (error >= 0)
                return;

            const int bufferSize = 1024;
            byte* buffer = stackalloc byte[bufferSize];

            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);

            string ffmpegError = Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "Unknown FFmpeg error";

            throw new InvalidOperationException($"{message}: {ffmpegError}");
        }
    }
}
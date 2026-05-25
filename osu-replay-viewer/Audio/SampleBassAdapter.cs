using ManagedBass;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Sample;
using System;
using System.Reflection;

namespace osu_replay_renderer_netcore.Audio
{
    public class SampleBassAdapter : Sample
    {
        public static readonly Type SampleBass = typeof(AudioMixer).Assembly.GetType("osu.Framework.Audio.Sample.SampleBass");
        public static readonly Type SampleBassFactory = typeof(AudioMixer).Assembly.GetType("osu.Framework.Audio.Sample.SampleBassFactory");

        public readonly ISample TargetedSample;

        private readonly object factory;

        public int SampleId => (int)SampleBassFactory.GetMethod("get_SampleId").Invoke(factory, null);
        public override double Length => 0;
        public override bool IsLoaded => (bool)SampleBassFactory.GetMethod("get_IsLoaded").Invoke(factory, null);

        public SampleBassAdapter(ISample sample) : base("test")
        {
            TargetedSample = sample;
            factory = SampleBass.GetField("factory", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sample);
        }

        protected override SampleChannel CreateChannel() => (SampleChannel)SampleBass.GetMethod("CreateChannel").Invoke(TargetedSample, null);

        public AudioBuffer AsAudioBuffer()
        {
            var info = Bass.SampleGetInfo(SampleId);

            if (info.Channels < 1 || info.Length <= 0)
                return null;

            bool isFloat = info.Flags.HasFlag(BassFlags.Float);
            int pcmBits = detectPcmBits(info, isFloat);

            if (pcmBits is not (8 or 16 or 24 or 32))
                pcmBits = isFloat ? 32 : 16;

            var format = new AudioFormat
            {
                Channels = info.Channels,
                SampleRate = info.Frequency,
                PCMSize = pcmBits / 8
            };

            int bytesPerFrame = format.PCMSize * format.Channels;
            if (bytesPerFrame <= 0)
                return null;

            int totalLength = info.Length - (info.Length % bytesPerFrame);
            if (totalLength <= 0)
                return null;

            int samples = totalLength / bytesPerFrame;
            var bytes = new byte[totalLength];

            Bass.SampleGetData(SampleId, bytes);

            var buff = new AudioBuffer(format, samples);

            for (int i = 0; i < samples * format.Channels; i++)
            {
                int offset = i * format.PCMSize;
                buff.Data[i] = decodeSample(bytes, offset, pcmBits, isFloat);
            }

            return buff;
        }

        private static int detectPcmBits(SampleInfo info, bool isFloat)
        {
            if (info.OriginalResolution > 0)
                return normalizeBits(info.OriginalResolution, isFloat);

            if (isFloat)
                return 32;

            if (info.Flags.HasFlag(BassFlags.Byte))
                return 8;

            return 16;
        }

        private static int normalizeBits(int bits, bool isFloat)
        {
            if (bits <= 8)
                return 8;
            if (bits <= 16)
                return 16;
            if (bits <= 24)
                return 24;
            if (bits <= 32)
                return 32;

            return isFloat ? 32 : 16;
        }

        private static float decodeSample(byte[] buffer, int offset, int pcmBits, bool isFloat)
        {
            return pcmBits switch
            {
                8 => (buffer[offset] - 128) / 128f,
                16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                24 => read24Bit(buffer, offset),
                32 => isFloat
                    ? clampSample(BitConverter.ToSingle(buffer, offset))
                    : BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => 0f
            };
        }

        private static float read24Bit(byte[] buffer, int offset)
        {
            int sample = buffer[offset]
                        | (buffer[offset + 1] << 8)
                        | (buffer[offset + 2] << 16);

            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000);

            return sample / 8388608f;
        }

        private static float clampSample(float value)
            => Math.Clamp(value, -1f, 1f);
    }
}

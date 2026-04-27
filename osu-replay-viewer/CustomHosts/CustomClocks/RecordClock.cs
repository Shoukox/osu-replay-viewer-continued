using osu.Framework.Timing;

namespace osu_replay_renderer_netcore.CustomHosts.CustomClocks
{
    public class RecordClock : IFrameBasedClock
    {
        private readonly int fps;

        public double FrameTime { get; }
        public ulong CurrentFrame { get; set; }

        public RecordClock(int frameRate)
        {
            fps = frameRate;
            FrameTime = 1000.0 / fps;
        }

        public double ClockOffset { get; set; } = 0;

        public double ElapsedFrameTime => FrameTime;
        public double FramesPerSecond => fps;

        FrameTimeInfo IFrameBasedClock.TimeInfo => new()
        {
            Elapsed = FrameTime,
            Current = CurrentTime
        };

        public double CurrentTime => ClockOffset + 1000.0 * CurrentFrame / FramesPerSecond;
        public double Rate => 1.0;
        public bool IsRunning => true;

        public void ProcessFrame()
        {
        }
    }
}
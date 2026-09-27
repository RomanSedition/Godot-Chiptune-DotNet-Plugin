using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/playback_engine.gd. Thin
    // AudioStreamGenerator glue around PlaybackState. Keeps audio-backend
    // concerns out of the sequencing/mixing logic so that logic stays
    // testable headlessly (see PlaybackState.cs).
    [Tool]
    public partial class PlaybackEngine : Node
    {
        [Export] public bool Loop { get; set; } = false;

        public PlaybackState State { get; private set; }

        AudioStreamGeneratorPlayback _playback;
        AudioStreamPlayer _player;

        // Fractional samples owed since the last tick, from
        // delta*SampleRate not being a whole number -- carried forward
        // instead of dropped, so pacing doesn't slowly drift out of sync
        // with real time over a long playback.
        float _sampleCarry;

        public void Setup(Song song, AudioStreamPlayer player)
        {
            _player = player;
            State = new PlaybackState(song, Loop);
            var stream = new AudioStreamGenerator
            {
                MixRate = PlaybackState.SampleRate,
                // Generous on purpose: this only bounds how much
                // *headroom* exists for the real per-frame fill (see
                // _Process()) to ride out a brief main-thread hiccup (a
                // redraw, a GC pause) without an audible underrun. It
                // doesn't control how much gets pushed on any single tick.
                BufferLength = 0.3f,
            };
            _player.Stream = stream;
        }

        public void Play(int orderIndex = 0, int rowIndex = 0)
        {
            State.PlayFrom(orderIndex, rowIndex);
            _player.Play();
            _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
            _sampleCarry = 0.0f;
            SetProcess(true);
        }

        public void Stop()
        {
            // State can legitimately still be null here if Stop is pressed
            // before Play/a preview has ever run.
            if (State == null)
                return;
            State.Stop();
            SetProcess(false);
            if (_player.Playing)
                _player.Stop();
        }

        // Paces sample generation to actual elapsed wall-clock time on
        // EVERY tick, not just the first. GetFramesAvailable() reports
        // ring-buffer space, not elapsed time -- the buffer starts fully
        // empty, so naively filling "whatever fits" pushes an entire
        // buffer_length's worth of audio (however many rows that covers)
        // in one shot the moment there's room, independent of how much
        // real time has actually passed.
        public override void _Process(double delta)
        {
            if (_playback == null || !State.Playing)
                return;
            _sampleCarry += (float)delta * PlaybackState.SampleRate;
            var wanted = (int)_sampleCarry;
            _sampleCarry -= wanted;
            var toFill = Mathf.Min(wanted, _playback.GetFramesAvailable());
            // If the buffer is momentarily full, the shortfall isn't
            // dropped -- it's owed back onto the carry so it gets pushed
            // as soon as there's room. Discarding it here would let
            // real-time pacing silently fall behind under load instead of
            // just riding out the backpressure.
            _sampleCarry += wanted - toFill;
            for (var i = 0; i < toFill; i++)
            {
                if (!State.Playing)
                    break;
                var sample = State.AdvanceSample();
                _playback.PushFrame(new Vector2(sample, sample));
            }
        }
    }
}

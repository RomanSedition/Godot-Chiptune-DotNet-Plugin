using System.Diagnostics;
using System.Threading;
using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/playback_engine.gd. Thin
    // AudioStreamGenerator glue around PlaybackState. Keeps audio-backend
    // concerns out of the sequencing/mixing logic so that logic stays
    // testable headlessly (see PlaybackState.cs).
    //
    // Sample generation runs on a dedicated background Thread (started by
    // Play(), joined by Stop()) rather than the main-thread _Process() this
    // used before -- see "Playback performance" in CHIPTRACKER_SPEC.md.
    // PushFrame()/GetFramesAvailable() on an AudioStreamGeneratorPlayback are
    // Godot's documented mechanism for feeding a generator stream from a
    // separate thread. The one place this can race with a main-thread edit
    // today -- live note recording writing a Cell while this engine plays --
    // is guarded by Song.PlaybackLock (taken here in PlaybackState.TriggerRow
    // and in ChiptrackerMainView.OnNoteKeyPressed).
    [Tool]
    public partial class PlaybackEngine : Node
    {
        [Export] public bool Loop { get; set; } = false;

        public PlaybackState State { get; private set; }

        // Relayed, main-thread-safe copies of State.RowAdvanced/Finished --
        // State's own events fire on whichever thread called
        // AdvanceSample() (the generation thread, once Play() has started
        // it), so a handler touching Node/Control APIs (UI redraws, or
        // OnBarPreviewRequested's auto-Stop()) can't subscribe to State's
        // directly without risking a cross-thread Godot API call, or in
        // Stop()'s case a thread trying to Join() itself. Callers that care
        // about thread safety (ChiptrackerMainView, for the live engine)
        // subscribe to these instead of State.RowAdvanced/State.Finished.
        public event System.Action<int, int> RowAdvanced;
        public event System.Action Finished;

        AudioStreamGeneratorPlayback _playback;
        AudioStreamPlayer _player;

        Thread _generationThread;
        volatile bool _running;

        public void Setup(Song song, AudioStreamPlayer player)
        {
            _player = player;
            // Dropped along with the State they were subscribed for. These
            // events live on this Node, which outlives any single pass,
            // while every subscriber attaches right after a Setup() and
            // means them for that pass only -- bar preview's stop-at-
            // bar-end lambda most of all, which would otherwise survive to
            // stop the *next* Play on its first row. Before generation
            // moved to a thread these hung off State, so a fresh Setup()
            // dropped them for free; clearing here keeps that lifetime.
            RowAdvanced = null;
            Finished = null;
            State = new PlaybackState(song, Loop);
            State.RowAdvanced += (orderIndex, rowIndex) => CallDeferred(MethodName.RaiseRowAdvanced, orderIndex, rowIndex);
            State.Finished += () => CallDeferred(MethodName.RaiseFinished);
            var stream = new AudioStreamGenerator
            {
                MixRate = PlaybackState.SampleRate,
                // Generous on purpose: this only bounds how much
                // *headroom* exists for the generation thread's fill (see
                // RunGenerationLoop()) to ride out a brief scheduling
                // hiccup without an audible underrun. It doesn't control
                // how much gets pushed on any single tick. It no longer
                // has to absorb a whole-note synthesis burst either --
                // PlaybackState generates a sample at a time now, so a
                // dense row costs no more than any other (see "Playback
                // performance" in CHIPTRACKER_SPEC.md).
                BufferLength = 0.3f,
            };
            _player.Stream = stream;
        }

        public void Play(int orderIndex = 0, int rowIndex = 0)
        {
            State.PlayFrom(orderIndex, rowIndex);
            _player.Play();
            _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
            // No audio device (e.g. --headless with no dummy driver
            // available) -- matches the old _Process()'s
            // "_playback == null" no-op rather than spinning a thread that
            // can never push anything.
            if (_playback == null)
                return;
            _running = true;
            _generationThread = new Thread(RunGenerationLoop) { IsBackground = true };
            _generationThread.Start();
        }

        // CallDeferred targets (see the RowAdvanced/State.RowAdvanced
        // wiring in Setup()) -- always run on the main thread regardless of
        // which thread State's own event fired on.
        void RaiseRowAdvanced(int orderIndex, int rowIndex) => RowAdvanced?.Invoke(orderIndex, rowIndex);
        void RaiseFinished() => Finished?.Invoke();

        public void Stop()
        {
            // State can legitimately still be null here if Stop is pressed
            // before Play/a preview has ever run.
            if (State == null)
                return;
            _running = false;
            _generationThread?.Join();
            _generationThread = null;
            State.Stop();
            if (_player.Playing)
                _player.Stop();
        }

        // Paces sample generation to actual elapsed wall-clock time on every
        // iteration, not just the first. GetFramesAvailable() reports
        // ring-buffer space, not elapsed time -- the buffer starts fully
        // empty, so naively filling "whatever fits" pushes an entire
        // buffer_length's worth of audio (however many rows that covers) in
        // one shot the moment there's room, independent of how much real
        // time has actually passed. Same carry-forward logic the old
        // _Process() used, just paced by a Stopwatch instead of Godot's
        // per-frame delta.
        //
        // Nothing may escape this method. An unhandled exception on the
        // main thread is caught and logged by Godot, but on a raw
        // System.Threading.Thread it aborts the whole editor process --
        // a playback bug must not be able to take the editor down with it.
        void RunGenerationLoop()
        {
            try
            {
                Generate();
            }
            catch (System.Exception e)
            {
                _running = false;
                // Not GD.PushError: that's a Godot call, and this is the
                // audio thread. CallDeferred hops to the main thread first.
                Callable.From(() => GD.PushError($"Chiptracker: playback thread stopped after {e}")).CallDeferred();
            }
        }

        void Generate()
        {
            var clock = Stopwatch.StartNew();
            var lastElapsed = clock.Elapsed.TotalSeconds;
            var sampleCarry = 0.0f;
            while (_running)
            {
                if (!State.Playing)
                {
                    Thread.Sleep(1);
                    continue;
                }
                var now = clock.Elapsed.TotalSeconds;
                var delta = now - lastElapsed;
                lastElapsed = now;
                sampleCarry += (float)delta * PlaybackState.SampleRate;
                var wanted = (int)sampleCarry;
                sampleCarry -= wanted;
                var toFill = Mathf.Min(wanted, _playback.GetFramesAvailable());
                // If the buffer is momentarily full, the shortfall isn't
                // dropped -- it's owed back onto the carry so it gets
                // pushed as soon as there's room. Discarding it here would
                // let real-time pacing silently fall behind under load
                // instead of just riding out the backpressure.
                sampleCarry += wanted - toFill;
                for (var i = 0; i < toFill; i++)
                {
                    if (!State.Playing)
                        break;
                    var sample = State.AdvanceSample();
                    _playback.PushFrame(new Vector2(sample, sample));
                }
                Thread.Sleep(1);
            }
        }
    }
}

using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/cached_playback_engine.gd.
    // AudioStreamPlayer glue around PatternAudioCache -- the M8 counterpart
    // to PlaybackEngine, but streams a pre-rendered AudioStreamWav instead
    // of synthesizing samples live every frame. No per-sample mixing cost
    // and no dependence on frame timing during playback:
    // AudioStreamPlayer.Play(fromPosition) seeks into the WAV natively,
    // and _Process() here only polls the player's position to keep State
    // (a PlaybackCursor, for the UI) in sync -- it never touches the audio
    // path itself.
    //
    // Needs [Tool] for the same reason PlaybackEngine does: a
    // CachedPlaybackEngine node lives inside the Chiptracker main-screen
    // tab's scene.
    [Tool]
    public partial class CachedPlaybackEngine : Node
    {
        const int SampleRate = PlaybackState.SampleRate;

        // Emitted each time a looping pass wraps back to the start of the
        // pattern -- the moment to rebuild and swap in edited audio (see
        // RefreshStream()).
        [Signal] public delegate void LoopedEventHandler();

        public PlaybackCursor State;
        public Song Song;
        public PatternAudioCache Cache;

        // When true, Play() repeats the order-list entry it starts in
        // until Stop() instead of running on through the song. Read at
        // Play() time, so toggling it while already playing takes effect
        // on the next Play().
        public bool Loop;

        // internal rather than private: PlaybackLoopTest pokes these
        // directly, the same way m9_loop_test.gd/m9_rebuild_test.gd/
        // m9_countin_test.gd poke the GDScript equivalents.
        internal AudioStreamPlayer _player;
        Array<int> _offsets = new(); // cumulative starting sample per order_list index
        int _samplesPerRow;

        // With a count-in, the playing stream is [clicks][song from the
        // start of the pattern being played]. _prefix is the length of
        // the clicks in samples, and _bias converts a song sample (what
        // _offsets is in) to a position in that stream: stream position =
        // song sample + _bias. Both are 0 without one.
        internal int _prefix;
        internal int _bias;

        public override void _Ready() => SetProcess(false); // only polling while Play() is actually active

        // Rebuilds the concatenated song stream from cache's current
        // buffers -- call after cache.Rebuild(song) so this and cache
        // agree on what's cached.
        public void Setup(Song song, AudioStreamPlayer player, PatternAudioCache cache)
        {
            Song = song;
            Cache = cache;
            _player = player;
            State = new PlaybackCursor();
            _samplesPerRow = Mathf.RoundToInt(SampleRate * 60.0f / song.Tempo / song.RowsPerBeat);
            State.SamplesPerRow = _samplesPerRow;
            _offsets = cache.OffsetTable(song);
            _player.Stream = cache.SongStream(song);
            if (!_player.IsConnected(AudioStreamPlayer.SignalName.Finished, new Callable(this, MethodName.OnPlayerFinished)))
                _player.Connect(AudioStreamPlayer.SignalName.Finished, new Callable(this, MethodName.OnPlayerFinished));
        }

        // With countIn, one bar of clicks plays first and the pass then
        // starts from row 0 of the pattern (rowIndex is ignored), sample-
        // exactly after the last click, since the clicks are part of the
        // same stream.
        public void Play(int orderIndex = 0, int rowIndex = 0, bool countIn = false)
        {
            if (Song == null || _offsets.Count == 0 || orderIndex < 0 || orderIndex >= _offsets.Count)
                return;
            _prefix = 0;
            _bias = 0;
            var playerStart = 0;
            if (countIn)
            {
                rowIndex = 0;
                BuildCountedStream(orderIndex);
            }
            else
            {
                playerStart = _offsets[orderIndex] + rowIndex * _samplesPerRow;
            }
            ApplyLoopRange(orderIndex);
            State.OrderIndex = orderIndex;
            State.RowIndex = rowIndex;
            State.SamplesIntoRow = 0;
            State.CountingIn = countIn;
            State.Playing = true;
            _player.Play((float)playerStart / SampleRate);
            SetProcess(true);
        }

        // Replaces the player's stream with [clicks][the song from the
        // start of this pattern] and sets _prefix/_bias to match.
        void BuildCountedStream(int orderIndex)
        {
            var clicks = CountIn.Render(_samplesPerRow, Song.RowsPerBeat, Song.MetronomeBeatsPerBar, Song.MetronomeAccent);
            var songStream = (AudioStreamWav)_player.Stream;
            var patternStart = _offsets[orderIndex];
            var clickBytes = Synth.ToPcm16(clicks);
            var songBytes = songStream.Data;
            var skip = patternStart * 2; // 16-bit mono: 2 bytes a sample
            var tailLength = Mathf.Max(0, songBytes.Length - skip);
            var data = new byte[clickBytes.Length + tailLength];
            System.Array.Copy(clickBytes, 0, data, 0, clickBytes.Length);
            if (tailLength > 0)
                System.Array.Copy(songBytes, skip, data, clickBytes.Length, tailLength);
            _player.Stream = new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = SampleRate,
                Stereo = false,
                Data = data,
            };
            _prefix = clicks.Length;
            _bias = _prefix - patternStart;
        }

        // Sets the stream's own loop points to the one pattern being
        // played, so the audio thread wraps it sample-exactly with no gap
        // or frame-timing dependence. The position poll in _Process()
        // then sees the position jump back to the start of that pattern,
        // which is what makes the playhead wrap.
        void ApplyLoopRange(int orderIndex)
        {
            if (_player.Stream is not AudioStreamWav stream)
                return;
            var length = Cache.PatternSampleCount(Song.OrderList[orderIndex]);
            if (Loop && length > 0)
            {
                stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                stream.LoopBegin = _offsets[orderIndex] + _bias;
                stream.LoopEnd = _offsets[orderIndex] + _bias + length;
            }
            else
            {
                stream.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
            }
        }

        public void Stop()
        {
            // _player stays null if State was assigned directly rather
            // than via Setup() (a test-only shortcut for injecting a fake
            // cursor -- see LiveRecordTest.TestMainViewTakes).
            if (State == null || _player == null)
                return;
            State.Playing = false;
            SetProcess(false);
            if (_player.Playing)
                _player.Stop();
        }

        // Derives OrderIndex/RowIndex/SamplesIntoRow from the player's
        // actual playback position every frame -- there's no synthesis
        // loop to hook here the way PlaybackEngine hooks AdvanceSample(),
        // so this is the only way to keep State (and therefore the
        // grid's playhead/transport status) in sync with what's actually
        // audible.
        public override void _Process(double delta)
        {
            if (State == null || !State.Playing || _player == null)
                return;
            Poll(Mathf.RoundToInt(_player.GetPlaybackPosition() * SampleRate));
        }

        // The position-to-row work of _Process(), taking the position in
        // samples so it can be driven directly.
        internal void Poll(int totalSample)
        {
            // Still in the clicks: the playhead waits at the start row.
            if (totalSample < _prefix)
            {
                State.CountingIn = true;
                State.SamplesIntoRow = 0;
                return;
            }
            State.CountingIn = false;
            var located = Locate(totalSample - _bias);
            var orderIndex = located.X;
            var rowIndex = located.Y;
            State.SamplesIntoRow = located.Z;
            // Looping, the row goes backwards within the same order
            // entry exactly when the stream wraps.
            var wrapped = Loop && orderIndex == State.OrderIndex && rowIndex < State.RowIndex;
            if (orderIndex != State.OrderIndex || rowIndex != State.RowIndex)
            {
                State.OrderIndex = orderIndex;
                State.RowIndex = rowIndex;
                State.EmitRowAdvanced(orderIndex, rowIndex);
            }
            if (wrapped)
                EmitSignal(SignalName.Looped);
        }

        // Where a position in the stream falls, as
        // (OrderIndex, RowIndex, SamplesIntoRow).
        internal Vector3I Locate(int totalSample)
        {
            var orderIndex = OrderIndexForSample(totalSample);
            var intoSegment = Mathf.Max(0, totalSample - _offsets[orderIndex]);
            var perRow = Mathf.Max(1, _samplesPerRow);
            return new Vector3I(orderIndex, intoSegment / perRow, intoSegment % perRow);
        }

        // The playhead right now, in song samples (what Locate() takes),
        // or -1 when nothing is playing. During a count-in it is before
        // the start of the song, so it can be negative; check
        // State.CountingIn first. Adds the time since the audio server
        // last mixed, as Godot recommends: the bare position only moves
        // in whole mix chunks, which is far coarser than the timing a
        // recorded note needs. Unlike the per-frame poll in _Process(),
        // this is read at the moment it's asked for (a key press).
        public int NowSample()
        {
            if (_player == null || State == null || !State.Playing)
                return -1;
            return Mathf.RoundToInt((_player.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix()) * SampleRate) - _bias;
        }

        // Swaps in a stream rebuilt from cache's current buffers and
        // carries on from where playback is now, so edits made during
        // playback (live recording) are heard from the next pass. Call
        // after cache.Rebuild(song). Restarts the player at the position
        // the old stream had reached (plus the time since the audio
        // server last mixed, as Godot recommends for an accurate
        // position); doing it right after a wrap keeps that position at
        // the very start of the loop, where the old and new audio are
        // identical.
        public void RefreshStream()
        {
            if (State == null || !State.Playing || _player == null || State.CountingIn)
                return;
            // In song time: the swapped-in stream is the plain song,
            // without the count-in clicks (which are over by the time
            // anything is rebuilt).
            var position = _player.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - (float)_bias / SampleRate;
            _prefix = 0;
            _bias = 0;
            _offsets = Cache.OffsetTable(Song);
            _player.Stream = Cache.SongStream(Song);
            ApplyLoopRange(State.OrderIndex);
            // If the pattern lengths changed, the old position may no
            // longer fall in this entry; restart it from its beginning
            // rather than somewhere else.
            var sample = (int)(position * SampleRate);
            var entryStart = _offsets[State.OrderIndex];
            var entryEnd = entryStart + Cache.PatternSampleCount(Song.OrderList[State.OrderIndex]);
            if (sample < entryStart || sample >= entryEnd)
                position = (float)entryStart / SampleRate;
            _player.Play((float)position);
        }

        int OrderIndexForSample(int sample)
        {
            var idx = 0;
            for (var i = 0; i < _offsets.Count; i++)
            {
                if (_offsets[i] <= sample)
                    idx = i;
                else
                    break;
            }
            return idx;
        }

        void OnPlayerFinished()
        {
            if (!State.Playing)
                return;
            State.Playing = false;
            SetProcess(false);
            State.EmitFinished();
        }
    }
}

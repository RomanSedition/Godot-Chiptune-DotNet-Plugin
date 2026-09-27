using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m9_loop_test.gd, tests/m9_rebuild_test.gd and
    // tests/m9_countin_test.gd -- these three cover CachedPlaybackEngine/
    // PatternAudioCache mechanics (loop points, wrap detection, Locate(),
    // RefreshStream(), the count-in's [clicks][song] stream splicing) that
    // M8/M9's other tests (PatternAudioCacheTest, LiveRecordTest) don't
    // exercise directly -- the code was ported as part of those chunks, but
    // these specific scenarios were never given their own test until now.
    public partial class PlaybackLoopTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            // m9_loop_test.gd
            TestLoopPoints(failures);
            TestLoopOff(failures);
            await TestLoopWiring(failures);

            // m9_rebuild_test.gd
            TestPerPatternDirty(failures);
            TestWrapDetection(failures);
            TestLocate(failures);
            TestRefreshStream(failures);
            await TestRebuildMainView(failures);

            // m9_countin_test.gd
            TestClickBar(failures);
            TestCountInEngine(failures);
            await TestCountInMainView(failures);

            if (failures.Count == 0)
            {
                GD.Print("playback_loop_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"playback_loop_test: FAIL - {f}");
                GD.PrintErr($"playback_loop_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        // Two patterns of different content so their cached buffers
        // differ, ordered [0, 1] -- the loop range for order entry 1 must
        // not start at sample 0.
        static Song MakeSong()
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 4 };
            song.Channels.Add(new Channel());
            var instrument = new Instrument { Id = 0 };
            song.Instruments.Add(instrument);
            var first = new Pattern(4, 1);
            var second = new Pattern(4, 1);
            second.Rows[1][0] = new Cell { Note = 60, InstrumentId = 0, Volume = 15 };
            song.Patterns.Add(first);
            song.Patterns.Add(second);
            song.OrderList.Add(0);
            song.OrderList.Add(1);
            return song;
        }

        CachedPlaybackEngine MakeEngine(Song song, PatternAudioCache cache)
        {
            var engine = new CachedPlaybackEngine();
            var player = new AudioStreamPlayer();
            Root.AddChild(engine);
            Root.AddChild(player);
            engine.Setup(song, player, cache);
            return engine;
        }

        void TestLoopPoints(List<string> failures)
        {
            var song = MakeSong();
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var offsets = cache.OffsetTable(song);
            var lengthA = cache.PatternSampleCount(0);
            var lengthB = cache.PatternSampleCount(1);
            Check(failures, lengthA > 0 && lengthB > 0 && offsets[1] == lengthA, "sanity: two cached patterns laid end to end");

            var engine = MakeEngine(song, cache);
            engine.Loop = true;
            engine.Play(1, 2);
            var stream = (AudioStreamWav)engine._player.Stream;
            Check(failures, stream.LoopMode == AudioStreamWav.LoopModeEnum.Forward, "loop on: the stream loops forward");
            Check(failures, stream.LoopBegin == offsets[1], "loop starts where the second pattern starts");
            Check(failures, stream.LoopEnd == offsets[1] + lengthB, "loop ends where the second pattern ends");
            engine.Stop();

            engine.Setup(song, engine._player, cache);
            engine.Loop = true;
            engine.Play(0, 0);
            stream = (AudioStreamWav)engine._player.Stream;
            Check(failures, stream.LoopBegin == 0 && stream.LoopEnd == lengthA, "starting in the first pattern loops exactly that pattern");
            engine.Stop();
        }

        void TestLoopOff(List<string> failures)
        {
            var song = MakeSong();
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var engine = MakeEngine(song, cache);
            engine.Loop = false;
            engine.Play(1, 0);
            var stream = (AudioStreamWav)engine._player.Stream;
            Check(failures, stream.LoopMode == AudioStreamWav.LoopModeEnum.Disabled, "loop off: the stream doesn't loop");
            engine.Stop();
        }

        // The transport's Loop button must reach the cached engine (it
        // used to set only the live engine that bar preview uses).
        async System.Threading.Tasks.Task TestLoopWiring(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var mainView = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(mainView);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            mainView._transportBar.EmitSignal(TransportBar.SignalName.LoopToggled, true);
            Check(failures, mainView._cachedPlaybackEngine.Loop, "the Loop button turns looping on in the cached engine");
            mainView._transportBar.EmitSignal(TransportBar.SignalName.LoopToggled, false);
            Check(failures, !mainView._cachedPlaybackEngine.Loop, "and off again");

            mainView.QueueFree();
        }

        static void AddNote(Song song, int pattern, int row, int note) =>
            song.Patterns[pattern].Rows[row][0] = new Cell { Note = note, InstrumentId = 0, Volume = 15 };

        // A different two-pattern song from MakeSong(): pattern 0 starts
        // with a note (so re-rendering it changes its buffer), matching
        // m9_rebuild_test.gd's own _make_song().
        static Song MakeRebuildSong()
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 4 };
            song.Channels.Add(new Channel());
            var instrument = new Instrument { Id = 0 };
            song.Instruments.Add(instrument);
            var first = new Pattern(4, 1);
            var second = new Pattern(4, 1);
            first.Rows[0][0] = new Cell { Note = 60, InstrumentId = 0, Volume = 15 };
            song.Patterns.Add(first);
            song.Patterns.Add(second);
            song.OrderList.Add(0);
            song.OrderList.Add(1);
            return song;
        }

        void TestPerPatternDirty(List<string> failures)
        {
            var song = MakeRebuildSong();
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            Check(failures, cache.IsClean(song), "sanity: a rebuilt cache is clean");
            var oldFirst = cache._buffers[0];
            var oldSecond = cache._buffers[1];

            // Change both patterns' audio, but only tell the cache about the second.
            AddNote(song, 0, 2, 72);
            AddNote(song, 1, 1, 64);
            cache.MarkPatternDirty(1);
            Check(failures, !cache.IsClean(song), "a dirty pattern makes the cache not clean");
            Check(failures, cache.IsPatternDirty(1) && !cache.IsPatternDirty(0), "only the marked pattern is dirty");
            cache.Rebuild(song);
            Check(failures, cache.IsClean(song), "rebuild makes it clean again");
            Check(failures, cache._buffers[1] != oldSecond, "the marked pattern was re-rendered");
            Check(failures, cache._buffers[0] == oldFirst, "the unmarked pattern was left alone (its buffer is still the old one)");

            // MarkAllDirty still re-renders everything.
            cache.MarkAllDirty();
            cache.Rebuild(song);
            Check(failures, cache._buffers[0] != oldFirst, "MarkAllDirty re-renders every pattern");
        }

        void TestWrapDetection(List<string> failures)
        {
            var song = MakeRebuildSong();
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var offsets = cache.OffsetTable(song);
            var length = cache.PatternSampleCount(1);

            var engine = MakeEngine(song, cache);
            engine.Loop = true;
            var wraps = 0;
            engine.Connect(CachedPlaybackEngine.SignalName.Looped, Callable.From(() => wraps++));
            engine.Play(1, 0);
            engine.Poll(offsets[1] + 10);
            engine.Poll(offsets[1] + length - 5); // the last row
            Check(failures, wraps == 0, "no wrap while the position only moves forward");
            engine.Poll(offsets[1] + 3); // back at the start of the pattern
            Check(failures, wraps == 1, "the position jumping back to the start of the looped pattern is a wrap");
            engine.Stop();

            // Not looping: the position doesn't jump back, but even if it
            // read that way it must not be reported as a loop.
            var engine2 = MakeEngine(song, cache);
            engine2.Loop = false;
            var wraps2 = 0;
            engine2.Connect(CachedPlaybackEngine.SignalName.Looped, Callable.From(() => wraps2++));
            engine2.Play(1, 0);
            engine2.Poll(offsets[1] + length - 5);
            engine2.Poll(offsets[1] + 3);
            Check(failures, wraps2 == 0, "with loop off, nothing is reported as a wrap");
            engine2.Stop();
        }

        void TestLocate(List<string> failures)
        {
            var song = MakeRebuildSong(); // 120 BPM, 2 rows/beat: 11025 samples per row, 4 rows per pattern
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var offsets = cache.OffsetTable(song);
            var engine = MakeEngine(song, cache);
            var perRow = 11025;
            Check(failures, engine.Locate(0) == new Vector3I(0, 0, 0), "the very start is order 0, row 0");
            Check(failures, engine.Locate(offsets[1] + 2 * perRow + 50) == new Vector3I(1, 2, 50), "a position inside the second pattern is located to its row and offset");
            Check(failures, engine.Locate(offsets[1] - 1) == new Vector3I(0, 3, perRow - 1), "the last sample of the first pattern is its last row");
            Check(failures, engine.NowSample() == -1, "NowSample() is -1 while nothing is playing");
            engine.Play(1, 0);
            Check(failures, engine.NowSample() >= 0, "NowSample() reads a position once playing");
            engine.Stop();
            Check(failures, engine.NowSample() == -1, "and -1 again after stop");
        }

        void TestRefreshStream(List<string> failures)
        {
            var song = MakeRebuildSong();
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var offsets = cache.OffsetTable(song);
            var engine = MakeEngine(song, cache);
            engine.Loop = true;
            engine.Play(1, 0);
            var oldStream = (AudioStreamWav)engine._player.Stream;

            AddNote(song, 1, 1, 64);
            cache.MarkPatternDirty(1);
            cache.Rebuild(song);
            engine.RefreshStream();
            var newStream = (AudioStreamWav)engine._player.Stream;
            Check(failures, newStream != oldStream, "RefreshStream() swaps in a new stream");
            Check(failures, newStream.Data != oldStream.Data, "the new stream contains the recorded note");
            Check(failures, newStream.LoopMode == AudioStreamWav.LoopModeEnum.Forward && newStream.LoopBegin == offsets[1] && newStream.LoopEnd == offsets[1] + cache.PatternSampleCount(1),
                "the loop range is re-applied to the new stream");
            Check(failures, engine.State.Playing, "playback carries on");
            engine.Stop();

            // Not playing: nothing to swap, and it must not start playback.
            var idle = MakeEngine(song, cache);
            var idleStream = idle._player.Stream;
            idle.RefreshStream();
            Check(failures, idle._player.Stream == idleStream, "RefreshStream() does nothing while stopped");
        }

        async System.Threading.Tasks.Task TestRebuildMainView(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var view = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(view);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            var song = MakeRebuildSong();
            view.SetSong(song);
            view._recordLatencyMs = 0.0f;
            view.RebuildCache();
            Check(failures, view._audioCache.IsClean(song), "sanity: the tab's cache starts clean");

            // Record a note into pattern 1 while "playing" it.
            var cursor = new PlaybackCursor { SamplesPerRow = 100, OrderIndex = 1, RowIndex = 2, SamplesIntoRow = 10, Playing = true };
            view._cachedPlaybackEngine.State = cursor;
            view._patternGrid.SelectedChannel = 0;
            view._cellEditor.ToggleEditMode();
            view._cellEditor.ToggleRecord();
            view.OnNoteKeyPressed(67);
            Check(failures, view._audioCache.IsPatternDirty(1) && !view._audioCache.IsPatternDirty(0), "a recorded note dirties only its own pattern");
            Check(failures, !view._audioCache.IsClean(song), "so the cache is no longer clean");

            view._cachedPlaybackEngine.EmitSignal(CachedPlaybackEngine.SignalName.Looped);
            Check(failures, view._audioCache.IsClean(song), "a loop wrap rebuilds the cache");

            // With nothing dirty, a wrap does no work.
            view._cachedPlaybackEngine.EmitSignal(CachedPlaybackEngine.SignalName.Looped);
            Check(failures, view._audioCache.IsClean(song), "a wrap with a clean cache leaves it clean");

            view.QueueFree();
        }

        static float Peak(float[] samples, int from, int to)
        {
            var peak = 0.0f;
            for (var i = from; i < to; i++)
                peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
            return peak;
        }

        void TestClickBar(List<string> failures)
        {
            var perRow = 1000;
            var rowsPerBeat = 4;
            var beat = perRow * rowsPerBeat;
            var accented = CountIn.Render(perRow, rowsPerBeat, 4, true);
            Check(failures, accented.Length == 4 * beat, "the bar is exactly four beats long (a whole number of rows)");
            // Clicks decay to silence within the first fifth of the beat.
            Check(failures, Peak(accented, 0, beat / 10) > 0.6f, "the first click is loud");
            Check(failures, Peak(accented, beat, beat + beat / 10) > 0.3f && Peak(accented, beat, beat + beat / 10) <= 0.61f, "later clicks are softer (volume 9 of 15)");
            Check(failures, Peak(accented, 0, beat / 10) > Peak(accented, beat, beat + beat / 10), "with accent on, the first click is the loudest");
            for (var b = 0; b < 4; b++)
                Check(failures, Peak(accented, b * beat + beat * 3 / 10, (b + 1) * beat) == 0.0f, $"beat {b} is silent after its click (the click is short)");
            var plain = CountIn.Render(perRow, rowsPerBeat, 4, false);
            Check(failures, Peak(plain, 0, beat / 10) <= 0.81f && Peak(plain, beat * 2, beat * 2 + beat / 10) <= 0.81f, "accent off: every click is volume 12 of 15 or less");
            Check(failures, Peak(plain, 0, beat / 10) > 0.5f && Peak(plain, beat * 3, beat * 3 + beat / 10) > 0.5f, "accent off: every click is audible");
        }

        static Song MakeCountInSong()
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 4 };
            song.Channels.Add(new Channel());
            var instrument = new Instrument { Id = 0 };
            song.Instruments.Add(instrument);
            var first = new Pattern(4, 1);
            var second = new Pattern(4, 1);
            second.Rows[1][0] = new Cell { Note = 60, InstrumentId = 0, Volume = 15 };
            song.Patterns.Add(first);
            song.Patterns.Add(second);
            song.OrderList.Add(0);
            song.OrderList.Add(1);
            return song;
        }

        void TestCountInEngine(List<string> failures)
        {
            var song = MakeCountInSong(); // 11025 samples a row, 2 rows a beat: a bar is 88200 samples
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var offsets = cache.OffsetTable(song);
            var perRow = 11025;
            var bar = 4 * 2 * perRow;
            var length = cache.PatternSampleCount(1);
            var plainBytes = cache.SongStream(song).Data.Length;

            // Not counting in: nothing changes.
            var plain = MakeEngine(song, cache);
            plain.Play(1, 2);
            Check(failures, plain._prefix == 0 && plain._bias == 0 && !plain.State.CountingIn, "without a count-in there is no prefix and no bias");
            plain.Stop();

            // Counting in, starting the second pattern.
            var engine = MakeEngine(song, cache);
            engine.Loop = true;
            engine.Play(1, 3, true); // the row (3) is ignored: a count-in starts at row 0
            var stream = (AudioStreamWav)engine._player.Stream;
            Check(failures, engine._prefix == bar, "the count-in prefix is one bar");
            Check(failures, engine.State.CountingIn && engine.State.RowIndex == 0, "the pass starts in the count-in, at row 0");
            Check(failures, stream.Data.Length == bar * 2 + plainBytes - offsets[1] * 2, "the stream is the clicks followed by the song from the start of the pattern");
            Check(failures, stream.LoopBegin == bar && stream.LoopEnd == bar + length, "the loop covers the pattern just after the clicks");

            var orderEvents = new List<Vector2I>();
            engine.State.RowAdvanced += (o, r) => orderEvents.Add(new Vector2I(o, r));
            var wraps = 0;
            engine.Connect(CachedPlaybackEngine.SignalName.Looped, Callable.From(() => wraps++));
            engine.Poll(bar - 10);
            Check(failures, engine.State.CountingIn && engine.State.RowIndex == 0 && orderEvents.Count == 0, "positions inside the clicks keep the playhead waiting");
            engine.Poll(bar + 3 * perRow + 5);
            Check(failures, !engine.State.CountingIn, "past the clicks the count-in is over");
            Check(failures, engine.State.OrderIndex == 1 && engine.State.RowIndex == 3 && engine.State.SamplesIntoRow == 5, "and positions after it are located in the song");
            Check(failures, wraps == 0, "no wrap yet");
            engine.Poll(bar + 2);
            Check(failures, wraps == 1, "the loop wrap is detected after a count-in");

            // Swapping in a rebuilt stream drops the clicks and keeps playing.
            var songStreamBytes = plainBytes;
            engine.RefreshStream();
            var swapped = (AudioStreamWav)engine._player.Stream;
            Check(failures, engine._prefix == 0 && engine._bias == 0, "RefreshStream() drops the count-in prefix");
            Check(failures, swapped.Data.Length == songStreamBytes, "the swapped-in stream is the plain song again");
            Check(failures, swapped.LoopBegin == offsets[1] && swapped.LoopEnd == offsets[1] + length, "and its loop range is back in song terms");
            engine.Stop();

            // RefreshStream() during the count-in does nothing.
            var early = MakeEngine(song, cache);
            early.Play(1, 0, true);
            var before = early._player.Stream;
            early.RefreshStream();
            Check(failures, early._player.Stream == before && early._prefix == bar, "RefreshStream() leaves a count-in stream alone");
            early.Stop();
        }

        async System.Threading.Tasks.Task TestCountInMainView(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var view = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(view);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            var song = MakeCountInSong();
            view.SetSong(song);
            view._recordLatencyMs = 0.0f;
            view._patternGrid.SelectedChannel = 0;

            // Count-in only exists on the cached engine's Play() overload --
            // the live engine (what a dirty cache falls back to) has no
            // count-in parameter at all -- so this test needs a clean
            // cache first, same as a real leaving-Edit-mode would give it.
            view._cellEditor.ToggleEditMode();
            view._cellEditor.ToggleEditMode();
            Check(failures, view._audioCache.IsClean(song), "sanity: cache is clean before testing count-in");

            // Count-in off: Record just arms (nothing starts).
            view._countIn = false;
            view._cellEditor.ToggleRecord();
            Check(failures, view._recording && !view.IsPlaying(), "count-in off: Record arms without starting playback");
            view._cellEditor.ToggleRecord();

            // Count-in on, nothing playing: Record counts in and starts.
            view._countIn = true;
            view._cellEditor.ToggleRecord();
            Check(failures, view._recording && view.IsPlaying(), "count-in on: Record starts playback");
            Check(failures, view._cachedPlaybackEngine.State.CountingIn, "and it starts with the count-in");
            Check(failures, view._cachedPlaybackEngine._prefix > 0, "the stream has the clicks in front");

            // Notes during the count-in are only previewed.
            view.OnNoteKeyPressed(64);
            var written = false;
            foreach (var pattern in song.Patterns)
            {
                foreach (Godot.Collections.Array row in pattern.Rows)
                {
                    if (row[0].As<Cell>().Note == 64)
                        written = true;
                }
            }
            Check(failures, !written, "a note played during the count-in isn't written");
            Check(failures, !view._gridEditHistory.HasOpenGroup(), "and doesn't start a take");
            view.OnStopPressed();

            // Play with Record armed and the count-in on also counts in.
            view.OnPlayPressed();
            Check(failures, view._cachedPlaybackEngine.State.CountingIn, "Play while recording with the count-in on counts in");
            view.OnStopPressed();

            // Not recording: Play is unchanged, however the box is set.
            view._cellEditor.ToggleRecord();
            view.OnPlayPressed();
            Check(failures, !view._cachedPlaybackEngine.State.CountingIn && view._cachedPlaybackEngine._prefix == 0, "Play without recording never counts in");
            view.OnStopPressed();

            // Record while already playing: arms, and doesn't restart with a count-in.
            view.OnPlayPressed();
            view._cellEditor.ToggleRecord();
            Check(failures, view.IsPlaying() && !view._cachedPlaybackEngine.State.CountingIn, "Record while already playing just arms");
            view.OnStopPressed();

            view.QueueFree();
        }
    }
}

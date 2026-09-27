using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m8_pattern_audio_cache_test.gd -- covers
    // PatternAudioCache/CachedPlaybackEngine directly (headless, no human
    // interaction needed) plus the dirty-tracking/rebuild wiring through
    // ChiptrackerMainView. See "Pattern/song audio cache" (M8) in
    // CHIPTRACKER_SPEC.md for the design this implements.
    public partial class PatternAudioCacheTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            TestCacheMatchesIsolatedRender(failures);
            TestDirtyTracking(failures);
            TestOffsetTable(failures);
            await TestMainViewIntegration(failures);

            if (failures.Count == 0)
            {
                GD.Print("M8 pattern_audio_cache_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M8 pattern_audio_cache_test: FAIL - {f}");
                GD.PrintErr($"M8 pattern_audio_cache_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static Song MakeSong()
        {
            var song = new Song
            {
                Tempo = 120,
                RowsPerBeat = 2, // 11025 samples/row @44100
                RowsPerPattern = 4,
            };
            song.Channels.Add(new Channel());
            song.Channels.Add(new Channel());

            var instrument = new Instrument { Id = 0, Waveform = "square", DutyCycle = 0.5f };
            song.Instruments.Add(instrument);

            var patternA = new Pattern(4, 2);
            var lead = new Cell { Note = 60, InstrumentId = 0, Volume = 15 };
            patternA.Rows[0][0] = lead;

            var patternB = new Pattern(4, 2);
            var bass = new Cell { Note = 48, InstrumentId = 0, Volume = 15 };
            patternB.Rows[2][1] = bass;

            song.Patterns.Add(patternA);
            song.Patterns.Add(patternB);
            song.OrderList.Add(0);
            song.OrderList.Add(1);
            return song;
        }

        // A pattern rendered in isolation by the cache should exactly
        // match rendering that same pattern alone through WavRenderer's
        // normal path (OrderList narrowed to just it) -- the cache
        // doesn't invent a different rendering pipeline, it just reuses
        // PlaybackState the same way.
        void TestCacheMatchesIsolatedRender(List<string> failures)
        {
            var song = MakeSong();
            var cache = new PatternAudioCache();
            cache.Rebuild(song);

            var isolatedSong = (Song)song.Duplicate(false);
            isolatedSong.OrderList = new Array<int> { 0 };
            var referenceState = new PlaybackState(isolatedSong, false);
            referenceState.PlayFrom();
            var referenceSamples = new List<float>();
            while (referenceState.Playing)
                referenceSamples.Add(referenceState.AdvanceSample());

            var offsets = cache.OffsetTable(song);
            var stream = cache.SongStream(song);
            Check(failures, offsets.Count == 2, $"OffsetTable has one entry per order_list position (got {offsets.Count})");
            Check(failures, offsets[0] == 0, "first order-list entry starts at sample 0");

            var pattern0Bytes = referenceSamples.Count * 2;
            Check(failures, offsets[1] == referenceSamples.Count,
                $"second order-list entry starts right after pattern 0's rendered length (got {offsets[1]}, want {referenceSamples.Count})");
            Check(failures, stream.Data.Length >= pattern0Bytes, "SongStream contains at least pattern 0's bytes");

            for (var i = 0; i < referenceSamples.Count; i++)
            {
                var expected = Mathf.RoundToInt(Mathf.Clamp(referenceSamples[i], -1.0f, 1.0f) * 32767.0f);
                var actual = System.BitConverter.ToInt16(stream.Data, i * 2);
                if (Mathf.Abs(expected - actual) > 1)
                {
                    failures.Add($"SongStream sample {i} matches an isolated single-pattern render (got {actual}, want {expected})");
                    break;
                }
            }
        }

        // MarkAllDirty() should make IsClean() false again even after a
        // rebuild, and Rebuild() should re-render everything the
        // OrderList references once that happens.
        void TestDirtyTracking(List<string> failures)
        {
            var song = MakeSong();
            var cache = new PatternAudioCache();
            Check(failures, !cache.IsClean(song), "a brand-new cache starts dirty");

            cache.Rebuild(song);
            Check(failures, cache.IsClean(song), "cache is clean right after Rebuild()");

            cache.MarkAllDirty();
            Check(failures, !cache.IsClean(song), "MarkAllDirty() makes the cache dirty again");

            cache.Rebuild(song);
            Check(failures, cache.IsClean(song), "cache is clean again after a second Rebuild()");

            // Growing the order list to reference a pattern that was
            // never rendered (e.g. a freshly-added pattern) should also
            // read as dirty, even though MarkAllDirty() was never called
            // for it specifically.
            song.OrderList = new Array<int> { 0, 1, 0 };
            Check(failures, cache.IsClean(song), "repeating an already-cached pattern in OrderList doesn't require a rebuild");
            var thirdPattern = new Pattern(4, 2);
            song.Patterns.Add(thirdPattern);
            song.OrderList = new Array<int> { 0, 1, 2 };
            Check(failures, !cache.IsClean(song), "referencing a never-rendered pattern index reads as dirty");
        }

        // OffsetTable()'s cumulative sums should exactly match summing
        // each referenced pattern's own rendered sample count, OrderList
        // order.
        void TestOffsetTable(List<string> failures)
        {
            var song = MakeSong();
            song.OrderList = new Array<int> { 1, 0, 1 }; // deliberately out of pattern-index order, with a repeat
            var cache = new PatternAudioCache();
            cache.Rebuild(song);
            var offsets = cache.OffsetTable(song);

            var running = 0;
            for (var i = 0; i < song.OrderList.Count; i++)
            {
                Check(failures, offsets[i] == running, $"OffsetTable()[{i}] == running total (got {offsets[i]}, want {running})");
                var isolated = (Song)song.Duplicate(false);
                isolated.OrderList = new Array<int> { song.OrderList[i] };
                var state = new PlaybackState(isolated, false);
                state.PlayFrom();
                var count = 0;
                while (state.Playing)
                {
                    state.AdvanceSample();
                    count++;
                }
                running += count;
            }
        }

        // Exercises the same dirty -> rebuild -> Play path a human would
        // trigger by typing a note, toggling Edit mode off, then pressing
        // Play.
        async System.Threading.Tasks.Task TestMainViewIntegration(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var mainView = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(mainView);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            Check(failures, !mainView._transportBar._cacheClean, "cache starts dirty for a freshly-opened tab");

            // Leaving Edit mode triggers a full rebuild.
            mainView._cellEditor._editModeButton.ButtonPressed = true;
            mainView._cellEditor.OnEditModeToggled(true);
            mainView._cellEditor._editModeButton.ButtonPressed = false;
            mainView._cellEditor.OnEditModeToggled(false);
            Check(failures, mainView._transportBar._cacheClean, "leaving Edit mode rebuilds the cache and marks it clean");

            // A cell edit invalidates it again.
            mainView._patternGrid.SelectedRow = 1;
            mainView._patternGrid.SelectedChannel = 0;
            mainView.OnCellSelected(1, 0);
            mainView._cellEditor._noteSpin.Value = 65;
            mainView._cellEditor.OnNoteChanged(65);
            Check(failures, !mainView._transportBar._cacheClean, "editing a cell invalidates the cache again");

            // Play rebuilds synchronously if needed, then streams via the
            // cached engine (not the live one -- that's bar-preview-only
            // after M8).
            mainView.OnPlayPressed();
            Check(failures, mainView._transportBar._cacheClean, "pressing Play with a dirty cache rebuilds it before playing");
            Check(failures, mainView._cachedPlaybackEngine.State.Playing, "Play starts cached playback");
            Check(failures, mainView._patternGrid.PlaybackState == mainView._cachedPlaybackEngine.State,
                "the grid's playback cursor points at the cached engine's state, not the live one");
            mainView.OnStopPressed();
            Check(failures, !mainView._cachedPlaybackEngine.State.Playing, "Stop halts cached playback");

            mainView.QueueFree();
        }
    }
}

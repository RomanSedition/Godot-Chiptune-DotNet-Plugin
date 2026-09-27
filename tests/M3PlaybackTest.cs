using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m3_playback_test.gd -- same checks, run
    // headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/M3PlaybackTest.cs
    public partial class M3PlaybackTest : SceneTree
    {
        public override void _Initialize()
        {
            var failures = new List<string>();

            TestRowTiming(failures);
            TestVolumeScaling(failures);
            TestMultiChannelMixingAndSustain(failures);
            TestMuteAndSolo(failures);
            TestProcessPacesByElapsedTimeNotBufferSpace(failures);
            TestEndOfSongNoLoop(failures);
            TestLooping(failures);

            if (failures.Count == 0)
            {
                GD.Print("M3 playback_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M3 playback_test: FAIL - {f}");
                GD.PrintErr($"M3 playback_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static Instrument MakeInstrument(string waveform = "square", float dutyCycle = 0.5f) =>
            new() { Id = 0, Waveform = waveform, DutyCycle = dutyCycle };

        static Cell MakeCell(int note, int volume = 15, int instrumentId = 0) =>
            new() { Note = note, Volume = volume, InstrumentId = instrumentId };

        static Cell EmptyCell() => new(); // default note = -1

        // 2 channels, 4 rows: ch0 note at row0, ch1 note at row1, rows 2-3 empty.
        static Song MakeTwoChannelSong(int tempo, int rowsPerBeat)
        {
            var song = new Song { Tempo = tempo, RowsPerBeat = rowsPerBeat, RowsPerPattern = 4 };
            song.Channels.Add(new Channel());
            song.Channels.Add(new Channel());
            song.Instruments.Add(MakeInstrument());

            var pattern = new Pattern(4, 2);
            pattern.Rows[0][0] = MakeCell(60); // C4
            pattern.Rows[0][1] = EmptyCell();
            pattern.Rows[1][0] = EmptyCell();
            pattern.Rows[1][1] = MakeCell(67); // G4
            pattern.Rows[2][0] = EmptyCell();
            pattern.Rows[2][1] = EmptyCell();
            pattern.Rows[3][0] = EmptyCell();
            pattern.Rows[3][1] = EmptyCell();
            song.Patterns.Add(pattern);
            song.OrderList.Add(0);
            return song;
        }

        static void TestRowTiming(List<string> failures)
        {
            var song = MakeTwoChannelSong(120, 2); // 0.25s/row -> 11025 samples/row @44100
            var state = new PlaybackState(song);
            Check(failures, state.SamplesPerRow == 11025, $"row_timing: samples_per_row == 11025 (got {state.SamplesPerRow})");
        }

        static void TestVolumeScaling(List<string> failures)
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 1 };
            song.Channels.Add(new Channel());
            song.Instruments.Add(MakeInstrument());
            var pattern = new Pattern(1, 1);
            pattern.Rows[0][0] = MakeCell(60, 7); // volume 7/15
            song.Patterns.Add(pattern);
            song.OrderList.Add(0);

            var state = new PlaybackState(song);
            state.PlayFrom();
            // Phase 0 of a 50%-duty square is +1.0 raw; volume scale is 7/15.
            var expected = 7.0f / 15.0f;
            Check(failures, Mathf.Abs(state.ChannelRawSample(0) - expected) < 0.0001f,
                $"volume_scaling: channel_raw_sample scaled by volume (got {state.ChannelRawSample(0)}, want {expected})");
        }

        static void TestMultiChannelMixingAndSustain(List<string> failures)
        {
            var song = MakeTwoChannelSong(120, 2);
            var state = new PlaybackState(song);
            state.PlayFrom();

            // Row 0: only channel 0 sounds (full volume, phase 0 -> raw +1.0).
            Check(failures, Mathf.Abs(state.ChannelRawSample(0) - 1.0f) < 0.0001f, "mixing: ch0 raw at row0 start is +1.0");
            Check(failures, state.ChannelRawSample(1) == 0.0f, "mixing: ch1 silent at row0 start");
            var firstSample = state.AdvanceSample();
            Check(failures, Mathf.Abs(firstSample - 0.5f) < 0.0001f, $"mixing: row0 first mixed sample == 0.5 (got {firstSample})");

            // Advance through the rest of row 0 to just before the row1 trigger.
            for (var i = 0; i < state.SamplesPerRow - 1; i++)
                state.AdvanceSample();
            Check(failures, state.RowIndex == 1, "mixing: row_index advanced to 1 after one row's worth of samples");

            // At row1's first sample, ch1 just triggered (raw +1.0 full
            // volume) while ch0 sustains from row0 (still mid-buffer, some
            // +-1.0 square sample).
            var ch0Sustained = state.ChannelRawSample(0);
            Check(failures, Mathf.Abs(Mathf.Abs(ch0Sustained) - 1.0f) < 0.0001f, "mixing: ch0 still sustaining a +-1.0 square sample into row1");
            Check(failures, Mathf.Abs(state.ChannelRawSample(1) - 1.0f) < 0.0001f, "mixing: ch1 raw at row1 start is +1.0");
            var expectedMix = Mathf.Clamp((ch0Sustained + 1.0f) / 2.0f, -1.0f, 1.0f);
            var row1Sample = state.AdvanceSample();
            Check(failures, Mathf.Abs(row1Sample - expectedMix) < 0.0001f,
                $"mixing: row1 first mixed sample matches ch0 sustain + ch1 trigger (got {row1Sample}, want {expectedMix})");
        }

        // At row0 of MakeTwoChannelSong, ch0 sounds (+1.0 raw) and ch1 is
        // silent (0.0 raw) -- the un-muted mix is their average, 0.5.
        // Muting the silent channel should make ch0 the ONLY contributor
        // to the average (not just a silent extra divisor), so the result
        // rises to 1.0. Soloing the silent channel should drop ch0 out of
        // the mix entirely even though it's actually sounding, leaving 0.0.
        static void TestMuteAndSolo(List<string> failures)
        {
            var mutedSong = MakeTwoChannelSong(120, 2);
            mutedSong.Channels[1].Muted = true;
            var mutedState = new PlaybackState(mutedSong);
            mutedState.PlayFrom();
            var mutedSample = mutedState.AdvanceSample();
            Check(failures, Mathf.Abs(mutedSample - 1.0f) < 0.0001f,
                $"mute: muting the silent channel raises ch0-only mix to 1.0 (got {mutedSample})");

            var soloSong = MakeTwoChannelSong(120, 2);
            soloSong.Channels[1].Solo = true;
            var soloState = new PlaybackState(soloSong);
            soloState.PlayFrom();
            var soloSample = soloState.AdvanceSample();
            Check(failures, Mathf.Abs(soloSample - 0.0f) < 0.0001f,
                $"solo: soloing the silent channel excludes ch0 even though it's sounding (got {soloSample})");
        }

        // PlaybackEngine._Process() paces sample generation to actual
        // elapsed time (delta * SampleRate) rather than however much
        // ring-buffer space happens to be free, specifically so a large
        // buffer_length (needed for real underrun protection) can't cause
        // a burst-fill of many rows' worth of audio in a single tick.
        // Exercises the carry-forward math directly rather than through a
        // real AudioStreamGeneratorPlayback, which the headless dummy
        // audio driver doesn't reliably provide.
        static void TestProcessPacesByElapsedTimeNotBufferSpace(List<string> failures)
        {
            var sampleCarry = 0.0f;
            var frameDelta = 1.0f / 60.0f; // a typical 60fps tick
            sampleCarry += frameDelta * PlaybackState.SampleRate;
            var wantedFrame1 = (int)sampleCarry;
            sampleCarry -= wantedFrame1;
            // A single ~16ms frame must want roughly 735 samples
            // (44100/60), not anywhere near a full 0.3s buffer's worth
            // (13230).
            Check(failures, wantedFrame1 > 700 && wantedFrame1 < 760,
                $"one frame's worth of pacing is ~735 samples at 60fps/44.1kHz (got {wantedFrame1})");

            // Repeating for many frames shouldn't drift: total requested
            // should track total elapsed time closely despite each
            // frame's fractional remainder being carried forward rather
            // than dropped.
            var totalWanted = wantedFrame1;
            for (var i = 0; i < 599; i++)
            {
                sampleCarry += frameDelta * PlaybackState.SampleRate;
                var wanted = (int)sampleCarry;
                sampleCarry -= wanted;
                totalWanted += wanted;
            }
            var expectedTotal = 600 * frameDelta * PlaybackState.SampleRate;
            Check(failures, Mathf.Abs(totalWanted - expectedTotal) < 1.0f,
                $"carry-forward pacing doesn't drift over 600 frames (got {totalWanted}, want ~{expectedTotal})");
        }

        static void TestEndOfSongNoLoop(List<string> failures)
        {
            var song = MakeTwoChannelSong(120, 2);
            var state = new PlaybackState(song);
            var finishedFired = false;
            state.Finished += () => finishedFired = true;
            state.PlayFrom();

            var totalSamples = state.SamplesPerRow * song.RowsPerPattern;
            for (var i = 0; i < totalSamples; i++)
                state.AdvanceSample();

            Check(failures, finishedFired, "end_of_song: finished signal fired when not looping");
            Check(failures, !state.Playing, "end_of_song: playing == false after song ends");
            Check(failures, state.AdvanceSample() == 0.0f, "end_of_song: advance_sample returns 0.0 once stopped");
        }

        static void TestLooping(List<string> failures)
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 2 };
            song.Channels.Add(new Channel());
            song.Instruments.Add(MakeInstrument());
            var pattern = new Pattern(2, 1);
            pattern.Rows[0][0] = MakeCell(60);
            pattern.Rows[1][0] = EmptyCell();
            song.Patterns.Add(pattern);
            song.OrderList.Add(0);

            var state = new PlaybackState(song, true);
            state.PlayFrom();
            var totalSamples = state.SamplesPerRow * song.RowsPerPattern;
            for (var i = 0; i < totalSamples; i++)
                state.AdvanceSample();

            Check(failures, state.Playing, "looping: still playing after wrapping past the end");
            Check(failures, state.OrderIndex == 0 && state.RowIndex == 0, "looping: wrapped back to order 0, row 0");
        }
    }
}

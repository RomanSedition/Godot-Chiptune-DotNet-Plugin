using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m1_song_test.gd -- same song, same
    // assertions, run headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/M1SongTest.cs
    public partial class M1SongTest : SceneTree
    {
        const string SavePath = "user://m1_test_song_net.tres";

        public override void _Initialize()
        {
            var failures = new List<string>();

            var song = new Song
            {
                Tempo = 140,
                RowsPerBeat = 4,
                RowsPerPattern = 8,
            };

            var ch0 = new Channel { Name = "Square 1", InstrumentType = "square" };
            var ch1 = new Channel { Name = "Triangle", InstrumentType = "triangle" };
            song.Channels.Add(ch0);
            song.Channels.Add(ch1);

            var instr = new Instrument { Id = 0, Waveform = "square", DutyCycle = 0.5f };
            song.Instruments.Add(instr);

            var pattern = new Pattern(song.RowsPerPattern, song.Channels.Count);
            var leadCell = new Cell { Note = 60, InstrumentId = 0, Volume = 15 };
            pattern.Rows[0][0] = leadCell;
            song.Patterns.Add(pattern);
            song.OrderList.Add(0);

            // total_duration_sec: tempo 140, rows_per_beat 4 -> row
            // duration 60/140/4 s; at 44100Hz that's exactly 4725
            // samples/row (44100*60/140/4 divides evenly here), so 8 rows
            // * 4725 / 44100 is exact, no rounding fuzz to account for.
            var expectedDuration = 8.0f * 4725.0f / 44100.0f;
            Check(failures, Mathf.Abs(song.TotalDurationSec() - expectedDuration) < 0.0000001f,
                $"total_duration_sec matches the sample-accurate expected duration (got {song.TotalDurationSec()}, want {expectedDuration})");

            var saveErr = ResourceSaver.Save(song, SavePath);
            if (saveErr != Error.Ok)
                failures.Add($"save failed with error {saveErr}");

            var loaded = ResourceLoader.Load(SavePath, "", ResourceLoader.CacheMode.Ignore) as Song;
            if (loaded == null)
            {
                failures.Add("load returned null");
            }
            else
            {
                Check(failures, loaded.Tempo == 140, "tempo round-trip");
                Check(failures, loaded.RowsPerBeat == 4, "rows_per_beat round-trip");
                Check(failures, loaded.RowsPerPattern == 8, "rows_per_pattern round-trip");
                Check(failures, loaded.Channels.Count == 2, "channel count round-trip");
                Check(failures, loaded.Channels[1].InstrumentType == "triangle", "channel field round-trip");
                Check(failures, loaded.Instruments.Count == 1, "instrument count round-trip");
                Check(failures, loaded.Patterns.Count == 1, "pattern count round-trip");
                Check(failures, loaded.Patterns[0].Rows.Count == 8, "pattern row count round-trip");
                Check(failures, loaded.Patterns[0].Rows[0].Count == 2, "pattern column count round-trip");
                var reloadedCell = loaded.Patterns[0].Rows[0][0].As<Cell>();
                Check(failures, reloadedCell.Note == 60, "cell note round-trip");
                Check(failures, reloadedCell.Volume == 15, "cell volume round-trip");
                var emptyCell = loaded.Patterns[0].Rows[1][0].As<Cell>();
                Check(failures, emptyCell.Note == -1, "default empty cell note");
            }

            if (failures.Count == 0)
            {
                GD.Print($"M1 song_test: PASS ({SavePath})");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M1 song_test: FAIL - {f}");
                GD.PrintErr($"M1 song_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }
    }
}

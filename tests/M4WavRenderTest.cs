using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m4_wav_render_test.gd -- same checks, run
    // headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/M4WavRenderTest.cs
    public partial class M4WavRenderTest : SceneTree
    {
        const string OutputPath = "user://m4_test_render_net.wav";

        public override void _Initialize()
        {
            var failures = new List<string>();

            TestRenderBasic(failures);
            TestEmptyOrderListRejected(failures);

            if (failures.Count == 0)
            {
                GD.Print("M4 wav_render_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M4 wav_render_test: FAIL - {f}");
                GD.PrintErr($"M4 wav_render_test: {failures.Count} failure(s)");
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
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 4 }; // 11025 samples/row @44100
            song.Channels.Add(new Channel());
            song.Channels.Add(new Channel());

            var instrument = new Instrument { Id = 0, Waveform = "square", DutyCycle = 0.5f };
            song.Instruments.Add(instrument);

            var pattern = new Pattern(4, 2);
            var lead = new Cell { Note = 60, InstrumentId = 0, Volume = 15 };
            pattern.Rows[0][0] = lead;
            var bass = new Cell { Note = 48, InstrumentId = 0, Volume = 15 };
            pattern.Rows[2][1] = bass;
            song.Patterns.Add(pattern);
            song.OrderList.Add(0);
            return song;
        }

        static void TestRenderBasic(List<string> failures)
        {
            var song = MakeSong();
            if (FileAccess.FileExists(OutputPath))
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(OutputPath));

            var err = WavRenderer.RenderSong(song, OutputPath);
            Check(failures, err == Error.Ok, $"render: returns OK (got {err})");
            Check(failures, FileAccess.FileExists(OutputPath), "render: output file exists");

            var loaded = AudioStreamWav.LoadFromFile(ProjectSettings.GlobalizePath(OutputPath));
            Check(failures, loaded != null, "render: file loads back as AudioStreamWav");
            if (loaded == null)
                return;

            Check(failures, loaded.MixRate == 44100, $"render: mix_rate round-trips (got {loaded.MixRate})");
            Check(failures, !loaded.Stereo, "render: mono");
            Check(failures, loaded.Format == AudioStreamWav.FormatEnum.Format16Bits, "render: 16-bit PCM format");

            var expectedSamples = 11025 * song.RowsPerPattern; // samples_per_row * rows
            var expectedBytes = expectedSamples * 2;
            Check(failures, loaded.Data.Length == expectedBytes,
                $"render: PCM byte count matches song length (got {loaded.Data.Length}, want {expectedBytes})");

            // Song.TotalDurationSec() is the general-purpose version of the
            // same expected-length math above -- cross-check the actual
            // rendered file's duration against it directly, exactly what
            // exporting a real song and comparing its length against the
            // tempo/row-count calculation would do.
            var actualDuration = (loaded.Data.Length / 2) / 44100.0f; // 16-bit mono
            var expectedDuration = song.TotalDurationSec();
            Check(failures, Mathf.Abs(actualDuration - expectedDuration) < 0.0000001f,
                $"render: actual WAV duration matches Song.TotalDurationSec() exactly (got {actualDuration:F6}s, want {expectedDuration:F6}s)");

            // First sample: lead at full volume, phase 0 -> +1.0 raw, mixed
            // across 2 channels (the other silent) -> ~0.5 scale, i.e.
            // ~16384 in 16-bit PCM.
            var firstSample = (short)(loaded.Data[0] | (loaded.Data[1] << 8));
            Check(failures, Mathf.Abs(firstSample - 16384) <= 1, $"render: first sample near half-scale positive (got {firstSample})");
        }

        static void TestEmptyOrderListRejected(List<string> failures)
        {
            var song = MakeSong();
            song.OrderList.Clear();
            var err = WavRenderer.RenderSong(song, "user://m4_should_not_exist_net.wav");
            Check(failures, err == Error.InvalidParameter, "render: empty order_list rejected with ERR_INVALID_PARAMETER");
        }
    }
}

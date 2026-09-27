using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m4_render_demo.gd -- one-off script: renders
    // the same demo tune from M3PlaybackDemoNet.cs to a real WAV file on
    // disk, for manual listening (not part of the automated test suite).
    public partial class M4RenderDemoNet : SceneTree
    {
        public override void _Initialize()
        {
            var song = BuildDemoSong();
            const string outputPath = "res://tests/m4_demo_render_net.wav";
            var err = WavRenderer.RenderSong(song, outputPath);
            if (err == Error.Ok)
                GD.Print($"Rendered to {ProjectSettings.GlobalizePath(outputPath)}");
            else
                GD.PrintErr($"Render failed with error {err}");
            Quit(err == Error.Ok ? 0 : 1);
        }

        static Song BuildDemoSong()
        {
            var song = new Song { Tempo = 140, RowsPerBeat = 4, RowsPerPattern = 16 };

            var lead = new Channel { Name = "Lead", InstrumentType = "square" };
            var bass = new Channel { Name = "Bass", InstrumentType = "triangle" };
            song.Channels.Add(lead);
            song.Channels.Add(bass);

            var leadInstr = new Instrument { Id = 0, Waveform = "square", DutyCycle = 0.5f };
            var bassInstr = new Instrument { Id = 1, Waveform = "triangle" };
            song.Instruments.Add(leadInstr);
            song.Instruments.Add(bassInstr);

            var pattern = new Pattern(16, 2);
            var leadNotes = new System.Collections.Generic.Dictionary<int, int> { [0] = 60, [4] = 64, [8] = 67, [12] = 72 };
            foreach (var (row, note) in leadNotes)
                pattern.Rows[row][0] = new Cell { Note = note, InstrumentId = 0, Volume = 12 };
            var bassNotes = new System.Collections.Generic.Dictionary<int, int> { [0] = 36, [8] = 41 };
            foreach (var (row, note) in bassNotes)
                pattern.Rows[row][1] = new Cell { Note = note, InstrumentId = 1, Volume = 15 };

            song.Patterns.Add(pattern);
            song.OrderList.Add(0);
            return song;
        }
    }
}

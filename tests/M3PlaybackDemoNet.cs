using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m3_playback_demo.gd -- manual listening
    // check for M3: plays a short hand-built 2-channel tune through the
    // real-time sequencer (PlaybackEngine -> PlaybackState). Run this
    // scene (F6) and listen for a simple melody + bassline together.
    public partial class M3PlaybackDemoNet : Node
    {
        AudioStreamPlayer _player;
        PlaybackEngine _engine;

        public override void _Ready()
        {
            _player = GetNode<AudioStreamPlayer>("AudioStreamPlayer");
            _engine = GetNode<PlaybackEngine>("PlaybackEngine");

            var song = BuildDemoSong();
            _engine.Loop = false;
            _engine.Setup(song, _player);
            _engine.State.RowAdvanced += OnRowAdvanced;
            _engine.State.Finished += OnFinished;
            _engine.Play();
            GD.Print("M3 demo: playing...");
        }

        void OnRowAdvanced(int orderIndex, int rowIndex) => GD.Print($"row {rowIndex} (order {orderIndex})");

        void OnFinished() => GD.Print("M3 demo: done");

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
            // Simple C major arpeggio lead on the beat.
            var leadNotes = new System.Collections.Generic.Dictionary<int, int> { [0] = 60, [4] = 64, [8] = 67, [12] = 72 }; // C4 E4 G4 C5
            foreach (var (row, note) in leadNotes)
            {
                var cell = new Cell { Note = note, InstrumentId = 0, Volume = 12 };
                pattern.Rows[row][0] = cell;
            }
            // Bass holds root notes, changing every 8 rows.
            var bassNotes = new System.Collections.Generic.Dictionary<int, int> { [0] = 36, [8] = 41 }; // C2, F2
            foreach (var (row, note) in bassNotes)
            {
                var cell = new Cell { Note = note, InstrumentId = 1, Volume = 15 };
                pattern.Rows[row][1] = cell;
            }

            song.Patterns.Add(pattern);
            song.OrderList.Add(0);
            return song;
        }
    }
}

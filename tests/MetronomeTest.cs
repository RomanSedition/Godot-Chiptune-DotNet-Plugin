using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/metronome_test.gd -- covers the metronome:
    // Song.AddMetronome()/RemoveMetronome() (one instrument, group and
    // channel named Metronome), tick placement and accent, staying in
    // sync as patterns/rows/rows_per_beat change, name reservation, the
    // removal protections, PlaybackState.ExcludeMetronome (the WAV
    // export behaviour), and the dock's Add/Remove Metronome + Accent controls.
    public partial class MetronomeTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            TestAddCreatesReservedTrio(failures);
            TestOnlyOneMetronome(failures);
            TestTickPlacementAndAccent(failures);
            TestStaysInSync(failures);
            TestProtections(failures);
            TestRemoveMetronome(failures);
            TestExportExclusion(failures);
            await TestDock(failures);

            if (failures.Count == 0)
            {
                GD.Print("metronome_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"metronome_test: FAIL - {f}");
                GD.PrintErr($"metronome_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static Song MakeSong(int channelCount = 2, int rows = 32)
        {
            var song = new Song { RowsPerPattern = rows };
            for (var i = 0; i < channelCount; i++)
                song.Channels.Add(new Channel());
            song.Instruments.Add(new Instrument { Id = 0 });
            song.Patterns.Add(new Pattern(rows, channelCount));
            song.OrderList.Add(0);
            return song;
        }

        void TestAddCreatesReservedTrio(List<string> failures)
        {
            var song = MakeSong();
            Check(failures, !song.HasMetronome(), "a fresh song has no metronome");
            Check(failures, song.AddMetronome(), "AddMetronome() succeeds on a song without one");
            Check(failures, song.HasMetronome(), "HasMetronome() after add");
            var channelIndex = song.MetronomeChannelIndex();
            Check(failures, channelIndex == 2, "the metronome channel is appended last");
            var channel = song.Channels[channelIndex];
            Check(failures, channel.Name == "Metronome", "channel is named Metronome");
            Check(failures, channel.InstrumentType == "noise", "channel is a noise channel");
            Check(failures, song.Groups.Count == 2, "a second group was added");
            Check(failures, song.Groups[1].Name == "Metronome", "group is named Metronome");
            Check(failures, channel.GroupIndex == 1, "the channel lives in the Metronome group");
            var instrument = song.MetronomeInstrument();
            Check(failures, instrument != null && instrument.Name == "Metronome", "instrument is named Metronome");
            Check(failures, instrument != null && instrument.Waveform == "noise", "instrument is noise");
            Check(failures, instrument != null && instrument.Id != 0, "instrument id doesn't collide with existing ones");
            Check(failures, instrument != null && instrument.Envelope.Count > 4 && instrument.Envelope[0] == 1.0f && instrument.Envelope[^1] == 0.0f, "envelope starts full and decays to silence");
            Check(failures, song.Patterns[0].Rows[0].Count == 3, "patterns grew a column for the channel");
        }

        void TestOnlyOneMetronome(List<string> failures)
        {
            var song = MakeSong();
            song.AddMetronome();
            var channelsBefore = song.Channels.Count;
            var groupsBefore = song.Groups.Count;
            var instrumentsBefore = song.Instruments.Count;
            Check(failures, !song.AddMetronome(), "a second AddMetronome() is refused");
            Check(failures, song.Channels.Count == channelsBefore && song.Groups.Count == groupsBefore && song.Instruments.Count == instrumentsBefore, "a refused add changes nothing");
        }

        void TestTickPlacementAndAccent(List<string> failures)
        {
            var song = MakeSong(2, 32);
            song.AddMetronome();
            var ch = song.MetronomeChannelIndex();
            var pattern = song.Patterns[0];
            var ticksOk = true;
            for (var row = 0; row < 32; row++)
            {
                var hasNote = pattern.Rows[row][ch].As<Cell>().Note >= 0;
                if (hasNote != (row % 4 == 0))
                    ticksOk = false;
            }
            Check(failures, ticksOk, "ticks land exactly on rows 0, 4, 8, ... (every rows_per_beat)");
            Check(failures, pattern.Rows[0][ch].As<Cell>().Volume == Song.MetronomeAccentVolume, "row 0 (bar start) is accented");
            Check(failures, pattern.Rows[4][ch].As<Cell>().Volume == Song.MetronomeBeatVolume, "row 4 is a plain beat");
            Check(failures, pattern.Rows[16][ch].As<Cell>().Volume == Song.MetronomeAccentVolume, "row 16 (next bar) is accented");
            Check(failures, pattern.Rows[0][ch].As<Cell>().InstrumentId == song.MetronomeInstrument().Id, "ticks use the metronome instrument");
            song.MetronomeAccent = false;
            song.SyncMetronome();
            Check(failures, pattern.Rows[0][ch].As<Cell>().Volume == Song.MetronomePlainVolume && pattern.Rows[4][ch].As<Cell>().Volume == Song.MetronomePlainVolume, "accent off makes every tick the same volume");
            foreach (var row in new[] { 0, 1, 2 })
                Check(failures, pattern.Rows[row][0].As<Cell>().Note == -1, $"other channels are untouched (row {row})");
        }

        void TestStaysInSync(List<string> failures)
        {
            var song = MakeSong(1, 16);
            song.AddMetronome();
            var ch = song.MetronomeChannelIndex();
            var index = song.AddPattern();
            Check(failures, song.Patterns[index].Rows[8][ch].As<Cell>().Note >= 0 && song.Patterns[index].Rows[9][ch].As<Cell>().Note == -1, "a pattern added later gets ticks");
            song.SetRowsPerPattern(24);
            Check(failures, song.Patterns[0].Rows.Count == 24 && song.Patterns[0].Rows[20][ch].As<Cell>().Note >= 0, "growing rows_per_pattern extends the ticks");
            song.SetRowsPerBeat(2);
            var ok = true;
            for (var row = 0; row < 24; row++)
            {
                if ((song.Patterns[0].Rows[row][ch].As<Cell>().Note >= 0) != (row % 2 == 0))
                    ok = false;
            }
            Check(failures, ok, "changing rows_per_beat re-lays the ticks");
            // Without a metronome, none of this writes anything.
            var plain = MakeSong(1, 8);
            plain.SetRowsPerBeat(2);
            plain.AddPattern();
            Check(failures, plain.Channels.Count == 1 && plain.Patterns[1].Rows[0][0].As<Cell>().Note == -1, "no metronome, no ticks");
        }

        void TestProtections(List<string> failures)
        {
            var song = MakeSong();
            song.AddMetronome();
            var ch = song.MetronomeChannelIndex();
            var channelsBefore = song.Channels.Count;
            song.RemoveChannel(ch);
            Check(failures, song.Channels.Count == channelsBefore && song.HasMetronome(), "RemoveChannel() refuses the Metronome channel");
            var groupsBefore = song.Groups.Count;
            song.RemoveGroup(song.Channels[ch].GroupIndex);
            Check(failures, song.Groups.Count == groupsBefore && song.IsMetronomeGroup(song.Channels[ch].GroupIndex), "RemoveGroup() refuses the Metronome group");
            Check(failures, Song.IsReservedName("Metronome") && Song.IsReservedName("  metronome ") && Song.IsReservedName("METRONOME"), "the reserved name matches case-insensitively and ignoring padding");
            Check(failures, !Song.IsReservedName("Metro") && !Song.IsReservedName("Metronome 2") && !Song.IsReservedName(""), "similar names aren't reserved");
            // Other channels and groups are still removable.
            song.RemoveChannel(0);
            Check(failures, song.Channels.Count == channelsBefore - 1 && song.HasMetronome(), "other channels can still be removed");
            var extra = song.AddGroup();
            song.RemoveGroup(extra);
            Check(failures, song.HasMetronome() && song.Groups.Count == groupsBefore, "other groups can still be removed");
        }

        void TestRemoveMetronome(List<string> failures)
        {
            var song = MakeSong(2, 16);
            song.AddMetronome();
            Check(failures, song.RemoveMetronome(), "RemoveMetronome() succeeds");
            Check(failures, !song.HasMetronome() && song.MetronomeInstrument() == null, "channel and instrument are gone");
            Check(failures, song.Channels.Count == 2 && song.Groups.Count == 1 && song.Instruments.Count == 1, "all three were removed, nothing else");
            Check(failures, song.Patterns[0].Rows[0].Count == 2, "patterns shrank back");
            Check(failures, !song.RemoveMetronome(), "removing with no metronome does nothing");
            Check(failures, song.AddMetronome(), "a metronome can be added again after removal");
            // A song whose only channel is the metronome can't lose it (a song needs one channel).
            var lone = new Song();
            lone.Patterns.Add(new Pattern(8, 0));
            lone.AddMetronome();
            Check(failures, lone.Channels.Count == 1 && !lone.RemoveMetronome() && lone.HasMetronome(), "the only channel can't be removed");
        }

        void TestExportExclusion(List<string> failures)
        {
            var song = MakeSong(1, 8);
            song.AddMetronome();

            var audible = new PlaybackState(song, false);
            audible.PlayFrom();
            var heard = false;
            for (var i = 0; i < 200; i++)
            {
                if (Mathf.Abs(audible.AdvanceSample()) > 0.001f)
                    heard = true;
            }
            Check(failures, heard, "the metronome is audible in normal playback");

            var exported = new PlaybackState(song, false) { ExcludeMetronome = true };
            exported.PlayFrom();
            var silent = true;
            for (var i = 0; i < 200; i++)
            {
                if (Mathf.Abs(exported.AdvanceSample()) > 0.0f)
                    silent = false;
            }
            Check(failures, silent, "exclude_metronome leaves it out (nothing else is playing)");
        }

        async System.Threading.Tasks.Task TestDock(List<string> failures)
        {
            var mainView = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn").Instantiate<ChiptrackerMainView>();
            var dock = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_dock.tscn").Instantiate<ChiptrackerDock>();
            dock.Size = new Vector2(1600, 300);
            Root.AddChild(mainView);
            Root.AddChild(dock);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            mainView.SetDock(dock);
            var song = MakeSong(2, 32);
            mainView.SetSong(song);

            Check(failures, !dock._addMetronomeButton.Disabled && dock._removeMetronomeButton.Disabled, "without a metronome, Add is enabled and Remove is disabled");

            dock._addMetronomeButton.EmitSignal(BaseButton.SignalName.Pressed);
            Check(failures, song.HasMetronome(), "the Add Metronome button creates the metronome");
            Check(failures, dock._addMetronomeButton.Disabled && !dock._removeMetronomeButton.Disabled, "with a metronome, Add is disabled and Remove is enabled");
            await ToSignal(this, SceneTree.SignalName.ProcessFrame); // rebuilt columns free the old ones with QueueFree()
            Check(failures, dock._groupColumnsContainer.GetChildCount() == 2, "the Groups tab shows the Metronome group as a second column");

            // Remove buttons with nothing selected skip the metronome and take the last other item.
            dock.OnRemoveChannelPressed();
            Check(failures, song.HasMetronome() && song.Channels.Count == 2, "Remove channel with no selection removed a normal channel, not the Metronome");
            dock.OnRemoveInstrumentPressed();
            Check(failures, song.MetronomeInstrument() != null && song.Instruments.Count == 1, "Remove instrument with no selection left the Metronome instrument");
            dock.OnAddGroupPressed();
            dock.OnRemoveGroupPressed();
            Check(failures, song.Groups.Count == 2 && song.IsMetronomeGroup(1), "Remove group with no selection removed the new group, not the Metronome group");
            dock._selectedGroupIndex = 1;
            dock.OnRemoveGroupPressed();
            Check(failures, song.Groups.Count == 2 && song.IsMetronomeGroup(1), "Remove group refuses the Metronome group even when selected");

            var metro = song.MetronomeChannelIndex();
            dock.OnMoveChannelGroup(metro, -1);
            Check(failures, song.IsMetronomeGroup(song.Channels[metro].GroupIndex), "the Metronome channel can't be moved out of its group");
            dock.OnMoveChannelGroup(0, 1);
            Check(failures, song.Channels[0].GroupIndex == 0, "another channel can't be moved into the Metronome group");

            Check(failures, !dock.RenameAllowed(true, "Renamed"), "the metronome's own channel/group/instrument can't be renamed");
            Check(failures, !dock.RenameAllowed(false, "metronome"), "no other channel/group/instrument can take the reserved name");
            Check(failures, dock.RenameAllowed(false, "Lead"), "ordinary renames are still allowed");

            dock._metronomeAccentCheck.ButtonPressed = false;
            dock._metronomeAccentCheck.EmitSignal(BaseButton.SignalName.Toggled, false);
            Check(failures, !song.MetronomeAccent && song.Patterns[0].Rows[0][metro].As<Cell>().Volume == Song.MetronomePlainVolume, "the Accent toggle rewrites the ticks");

            dock._removeMetronomeButton.EmitSignal(BaseButton.SignalName.Pressed);
            Check(failures, !song.HasMetronome() && song.Groups.Count == 1, "the Remove Metronome button removes the channel and group");
            Check(failures, song.MetronomeInstrument() == null, "and the instrument");
            Check(failures, !dock._addMetronomeButton.Disabled && dock._removeMetronomeButton.Disabled, "after removal, Add is enabled again");
        }
    }
}

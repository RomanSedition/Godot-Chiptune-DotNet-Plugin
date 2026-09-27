using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;
using ChiptrackerNet.Bridge;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/bridge_extended_test.gd -- the MCP bridge
    // commands added to give external tools parity with the dock/panel:
    // instrument rename/remove/duplicate/update, layer add/update/remove,
    // channel rename/mute/solo/group, groups add/remove/rename, metronome
    // add/remove, and order-list add/remove/move. Run headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/M5BridgeExtendedTest.cs
    public partial class M5BridgeExtendedTest : SceneTree
    {
        public override void _Initialize()
        {
            var failures = new List<string>();

            TestInstrumentCommands(failures);
            TestLayerCommands(failures);
            TestChannelCommands(failures);
            TestGroupCommands(failures);
            TestMetronomeCommands(failures);
            TestOrderCommands(failures);

            if (failures.Count == 0)
            {
                GD.Print("bridge_extended_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"bridge_extended_test: FAIL - {f}");
                GD.PrintErr($"bridge_extended_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static bool FloatsEqual(Array<float> actual, params float[] expected)
        {
            if (actual.Count != expected.Length)
                return false;
            for (var i = 0; i < expected.Length; i++)
            {
                if (!Mathf.IsEqualApprox(actual[i], expected[i]))
                    return false;
            }
            return true;
        }

        static bool IntsEqual(Array<int> actual, params int[] expected)
        {
            if (actual.Count != expected.Length)
                return false;
            for (var i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                    return false;
            }
            return true;
        }

        static bool Ok(Dictionary response) => response["ok"].AsBool();
        static Dictionary Result(Dictionary response) => response["result"].AsGodotDictionary();

        CommandDispatcher MakeDispatcher(int channelCount = 2)
        {
            var song = new Song();
            song.Channels.Clear();
            for (var i = 0; i < channelCount; i++)
                song.Channels.Add(new Channel());
            song.Patterns.Add(new Pattern(4, channelCount));
            song.OrderList.Add(0);
            var dispatcher = new CommandDispatcher { Song = song };
            Root.AddChild(dispatcher);
            return dispatcher;
        }

        static int Find(Array<Instrument> instruments, int id)
        {
            for (var i = 0; i < instruments.Count; i++)
            {
                if (instruments[i].Id == id)
                    return i;
            }
            return -1;
        }

        void TestInstrumentCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            var song = d.Song;
            var added = d.Dispatch("add_instrument", new Dictionary { ["waveform"] = "square", ["envelope"] = new Array<float> { 1.0f, 0.5f } });
            var id = Result(added)["instrument_id"].AsInt32();

            // update_instrument: partial updates, by id not position.
            var updated = d.Dispatch("update_instrument", new Dictionary { ["id"] = id, ["waveform"] = "noise", ["duty_cycle"] = 0.3f });
            Check(failures, Ok(updated), "update_instrument accepts a partial update");
            var instrument = song.Instruments[0];
            Check(failures, instrument.Waveform == "noise" && Mathf.IsEqualApprox(instrument.DutyCycle, 0.3f), "and applies only the given fields");
            Check(failures, FloatsEqual(instrument.Envelope, 1.0f, 0.5f), "leaving fields not mentioned untouched");
            Check(failures, !Ok(d.Dispatch("update_instrument", new Dictionary { ["id"] = 999, ["waveform"] = "square" })), "update_instrument refuses an unknown id");
            Check(failures, !Ok(d.Dispatch("update_instrument", new Dictionary { ["id"] = id, ["envelope"] = new Array<float> { 1.0f }, ["envelope_times"] = new Array<float> { 0.0f, 1.0f } })), "and a malformed envelope_times");

            // rename_instrument.
            var renamed = d.Dispatch("rename_instrument", new Dictionary { ["id"] = id, ["name"] = "Bass" });
            Check(failures, Ok(renamed) && song.Instruments[0].Name == "Bass", "rename_instrument renames by id");
            Check(failures, !Ok(d.Dispatch("rename_instrument", new Dictionary { ["id"] = id, ["name"] = "Metronome" })), "rename_instrument refuses the reserved name");

            // duplicate_instrument: deep copy, independent layers.
            d.Dispatch("add_layer", new Dictionary { ["instrument_id"] = id, ["waveform"] = "noise", ["step_offset"] = 7 });
            var duplicated = d.Dispatch("duplicate_instrument", new Dictionary { ["id"] = id });
            Check(failures, Ok(duplicated), "duplicate_instrument succeeds");
            var newId = Result(duplicated)["instrument_id"].AsInt32();
            Check(failures, newId != id, "and gives a fresh id");
            var copy = song.Instruments[^1];
            Check(failures, copy.Name == "Bass (Copy)", "with a (Copy) suffix");
            Check(failures, copy.Layers.Count == 1 && copy.Layers[0] != instrument.Layers[0], "and its own independent layer");
            copy.Layers[0].StepOffset = 99;
            Check(failures, instrument.Layers[0].StepOffset == 7, "mutating the copy's layer doesn't touch the original's");

            // remove_instrument.
            var count = song.Instruments.Count;
            var removed = d.Dispatch("remove_instrument", new Dictionary { ["id"] = newId });
            Check(failures, Ok(removed) && song.Instruments.Count == count - 1, "remove_instrument removes by id");
            Check(failures, !Ok(d.Dispatch("remove_instrument", new Dictionary { ["id"] = newId })), "and refuses an id that's already gone");

            // The Metronome instrument is protected across all four commands.
            song.AddMetronome();
            var metronomeId = song.MetronomeInstrument().Id;
            Check(failures, !Ok(d.Dispatch("update_instrument", new Dictionary { ["id"] = metronomeId, ["waveform"] = "square" })), "update_instrument refuses the Metronome");
            Check(failures, !Ok(d.Dispatch("rename_instrument", new Dictionary { ["id"] = metronomeId, ["name"] = "Kick" })), "rename_instrument refuses the Metronome");
            Check(failures, !Ok(d.Dispatch("remove_instrument", new Dictionary { ["id"] = metronomeId })), "remove_instrument refuses the Metronome");
        }

        void TestLayerCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            var song = d.Song;
            var id = Result(d.Dispatch("add_instrument", new Dictionary { ["waveform"] = "square", ["envelope"] = new Array<float> { 1.0f } }))["instrument_id"].AsInt32();
            var instrument = song.Instruments[0];

            var added = d.Dispatch("add_layer", new Dictionary
            {
                ["instrument_id"] = id, ["waveform"] = "noise", ["envelope"] = new Array<float> { 0.5f },
                ["step_offset"] = 12, ["fixed_note_enabled"] = true, ["fixed_note"] = 40,
            });
            Check(failures, Ok(added) && Result(added)["layer_index"].AsInt32() == 0, "add_layer adds the first layer at index 0");
            var layer = instrument.Layers[0];
            Check(failures, layer.Waveform == "noise" && layer.StepOffset == 12 && layer.FixedNoteEnabled && layer.FixedNote == 40, "with all its fields");
            Check(failures, layer.Layers.Count == 0, "and never layers of its own");

            var updated = d.Dispatch("update_layer", new Dictionary { ["instrument_id"] = id, ["layer_index"] = 0, ["waveform"] = "square", ["step_offset"] = -5 });
            Check(failures, Ok(updated) && layer.Waveform == "square" && layer.StepOffset == -5, "update_layer changes only the given fields");
            Check(failures, layer.FixedNoteEnabled, "leaving others (fixed_note_enabled) untouched");
            Check(failures, !Ok(d.Dispatch("update_layer", new Dictionary { ["instrument_id"] = id, ["layer_index"] = 5, ["waveform"] = "noise" })), "update_layer refuses an out-of-range layer_index");

            var removed = d.Dispatch("remove_layer", new Dictionary { ["instrument_id"] = id, ["layer_index"] = 0 });
            Check(failures, Ok(removed) && instrument.Layers.Count == 0, "remove_layer removes it");
            Check(failures, !Ok(d.Dispatch("remove_layer", new Dictionary { ["instrument_id"] = id, ["layer_index"] = 0 })), "and refuses again once it's gone");

            song.AddMetronome();
            var metronomeId = song.MetronomeInstrument().Id;
            Check(failures, !Ok(d.Dispatch("add_layer", new Dictionary { ["instrument_id"] = metronomeId, ["envelope"] = new Array<float> { 1.0f } })), "add_layer refuses the Metronome instrument");

            // A "layers" key inside a layer's own params is ignored (layering is one level deep).
            var otherId = Result(d.Dispatch("add_instrument", new Dictionary { ["envelope"] = new Array<float> { 1.0f } }))["instrument_id"].AsInt32();
            d.Dispatch("add_layer", new Dictionary
            {
                ["instrument_id"] = otherId, ["envelope"] = new Array<float> { 1.0f },
                ["layers"] = new Array { new Dictionary { ["envelope"] = new Array<float> { 1.0f } } },
            });
            var otherInstrument = song.Instruments[Find(song.Instruments, otherId)];
            Check(failures, otherInstrument.Layers.Count == 1 && otherInstrument.Layers[0].Layers.Count == 0, "a layer's own layers key is ignored");
        }

        void TestChannelCommands(List<string> failures)
        {
            var d = MakeDispatcher(2);
            var song = d.Song;

            var renamed = d.Dispatch("rename_channel", new Dictionary { ["index"] = 0, ["name"] = "Kick" });
            Check(failures, Ok(renamed) && song.Channels[0].Name == "Kick", "rename_channel renames");
            Check(failures, !Ok(d.Dispatch("rename_channel", new Dictionary { ["index"] = 0, ["name"] = "Metronome" })), "and refuses the reserved name");

            var updated = d.Dispatch("update_channel", new Dictionary { ["index"] = 0, ["muted"] = true, ["solo"] = false, ["instrument_type"] = "noise" });
            Check(failures, Ok(updated) && song.Channels[0].Muted && song.Channels[0].InstrumentType == "noise", "update_channel sets muted/instrument_type together");
            var soloOnly = d.Dispatch("update_channel", new Dictionary { ["index"] = 1, ["solo"] = true });
            Check(failures, Ok(soloOnly) && song.Channels[1].Solo && !song.Channels[1].Muted, "and can set just one field");

            var groupIndex = Result(d.Dispatch("add_group", new Dictionary()))["group_index"].AsInt32();
            Check(failures, groupIndex == 1 && song.Groups.Count == 2, "add_group appends a group");
            var moved = d.Dispatch("move_channel_to_group", new Dictionary { ["channel_index"] = 0, ["group_index"] = groupIndex });
            Check(failures, Ok(moved) && song.Channels[0].GroupIndex == groupIndex, "move_channel_to_group reassigns a channel");
            Check(failures, !Ok(d.Dispatch("move_channel_to_group", new Dictionary { ["channel_index"] = 0, ["group_index"] = 99 })), "and refuses an out-of-range group");

            var groupRenamed = d.Dispatch("rename_group", new Dictionary { ["index"] = groupIndex, ["name"] = "Drums" });
            Check(failures, Ok(groupRenamed) && song.Groups[groupIndex].Name == "Drums", "rename_group renames");
            Check(failures, !Ok(d.Dispatch("rename_group", new Dictionary { ["index"] = 0, ["name"] = "Metronome" })), "and refuses the reserved name");
            Check(failures, !Ok(d.Dispatch("remove_group", new Dictionary { ["index"] = 0 })), "remove_group refuses Group 1 (index 0)");
            var groupRemoved = d.Dispatch("remove_group", new Dictionary { ["index"] = groupIndex });
            Check(failures, Ok(groupRemoved) && song.Groups.Count == 1, "remove_group removes a non-default group");
            Check(failures, song.Channels[0].GroupIndex == 0, "folding its channel back into Group 1");
        }

        void TestGroupCommands(List<string> failures)
        {
            var d = MakeDispatcher(1);
            Check(failures, !Ok(d.Dispatch("remove_group", new Dictionary { ["index"] = 0 })), "remove_group still refuses the only group even as Group 1");
        }

        void TestMetronomeCommands(List<string> failures)
        {
            var d = MakeDispatcher(1);
            var song = d.Song;

            var added = d.Dispatch("add_metronome", new Dictionary());
            Check(failures, Ok(added) && song.HasMetronome(), "add_metronome adds one");
            Check(failures, Result(added).ContainsKey("channel_index") && Result(added).ContainsKey("instrument_id"), "and reports where");
            Check(failures, !Ok(d.Dispatch("add_metronome", new Dictionary())), "a second add_metronome is refused");

            var removed = d.Dispatch("remove_metronome", new Dictionary());
            Check(failures, Ok(removed) && !song.HasMetronome(), "remove_metronome removes it");
            Check(failures, !Ok(d.Dispatch("remove_metronome", new Dictionary())), "and refuses when there's none");
        }

        void TestOrderCommands(List<string> failures)
        {
            var d = MakeDispatcher(1);
            var song = d.Song;
            d.Dispatch("add_pattern", new Dictionary()); // pattern 1
            song.OrderList.Clear();
            song.OrderList.Add(0);

            var added = d.Dispatch("add_to_order", new Dictionary { ["pattern"] = 1 });
            Check(failures, Ok(added) && Result(added)["order_index"].AsInt32() == 1 && IntsEqual(song.OrderList, 0, 1), "add_to_order appends a pattern");
            Check(failures, !Ok(d.Dispatch("add_to_order", new Dictionary { ["pattern"] = 99 })), "and refuses an out-of-range pattern");

            var moved = d.Dispatch("move_order_entry", new Dictionary { ["order_index"] = 1, ["direction"] = -1 });
            Check(failures, Ok(moved) && IntsEqual(song.OrderList, 1, 0), "move_order_entry swaps with the previous entry");
            Check(failures, !Ok(d.Dispatch("move_order_entry", new Dictionary { ["order_index"] = 0, ["direction"] = -1 })), "and refuses moving past the start");
            Check(failures, !Ok(d.Dispatch("move_order_entry", new Dictionary { ["order_index"] = 0, ["direction"] = 2 })), "or an invalid direction");

            var removed = d.Dispatch("remove_from_order", new Dictionary { ["order_index"] = 0 });
            Check(failures, Ok(removed) && IntsEqual(song.OrderList, 0), "remove_from_order removes an entry");
            Check(failures, !Ok(d.Dispatch("remove_from_order", new Dictionary { ["order_index"] = 5 })), "and refuses an out-of-range one");
        }
    }
}

using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // Covers MIDI input: MidiKeyboard's base mapping/channel-filter
    // behavior, AkaiMPKMini4's CC bindings and diagram hotspots,
    // ChiptrackerMidiNode's InputEventMidi translation into signals, and
    // the MIDI-specific tail of tests/m9_record_test.gd (the Akai
    // AUTOMATION button toggling Record through ChiptrackerMainView).
    // There's no dedicated GDScript reference test file for MIDI beyond
    // that tail -- this is otherwise new coverage for this port.
    public partial class MidiTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            TestMidiKeyboardBase(failures);
            TestAkaiMappings(failures);
            TestMidiNode(failures);
            await TestMainViewMidiAction(failures);

            if (failures.Count == 0)
            {
                GD.Print("midi_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"midi_test: FAIL - {f}");
                GD.PrintErr($"midi_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        void TestMidiKeyboardBase(List<string> failures)
        {
            var profile = new MidiKeyboard { OctaveOffset = 1 };
            Check(failures, profile.MapNote(60) == 72, "MapNote applies OctaveOffset in semitones (12 per octave)");
            Check(failures, profile.MapVelocity(127) == 15, "max velocity maps to max volume");
            Check(failures, profile.MapVelocity(0) == 0, "zero velocity maps to zero volume");
            Check(failures, profile.ActionForControlChange(1, 127) == "", "the base profile makes no CC mappings");
            Check(failures, profile.GetDiagramPath() == "", "the base profile has no diagram");
            Check(failures, profile.GetMappings().Count == 1, "the base profile documents just the keys");

            var anyChannel = new MidiKeyboard { MidiChannel = -1 };
            Check(failures, anyChannel.AcceptsChannel(0) && anyChannel.AcceptsChannel(5), "MidiChannel -1 listens on every channel");
            var oneChannel = new MidiKeyboard { MidiChannel = 3 };
            Check(failures, oneChannel.AcceptsChannel(3) && !oneChannel.AcceptsChannel(0), "a specific MidiChannel only accepts that channel");
        }

        void TestAkaiMappings(List<string> failures)
        {
            var akai = new AkaiMPKMini4();
            Check(failures, akai.DeviceName == "Akai MPK Mini mk4", "the constructor sets the device name");

            // The Akai's AUTOMATION button (CC 78) toggles Record, once per press.
            Check(failures, akai.ActionForControlChange(78, 127) == MidiKeyboard.ActionToggleRecord, "AUTOMATION (CC 78) maps to ToggleRecord");
            Check(failures, akai.ActionForControlChange(78, 0) == "", "its release (value 0) does nothing");
            Check(failures, akai.ActionForControlChange(76, 127) == MidiKeyboard.ActionPlay, "CONTINUE (CC 76) maps to Play");
            Check(failures, akai.ActionForControlChange(77, 127) == MidiKeyboard.ActionToggleEditMode, "QUANTIZE (CC 77) maps to ToggleEditMode");
            Check(failures, akai.ActionForControlChange(73, 127) == MidiKeyboard.ActionUndoGridEdit, "the UNDO button (CC 73) maps to UndoGridEdit");
            Check(failures, akai.ActionForControlChange(74, 127) == MidiKeyboard.ActionRedoGridEdit, "GLOBAL (CC 74) maps to RedoGridEdit");
            Check(failures, akai.ActionForControlChange(82, 127) == MidiKeyboard.ActionTapTempo, "TAP TEMPO (CC 82) maps to TapTempo");
            Check(failures, akai.ActionForControlChange(1, 127) == "", "an unbound CC does nothing");

            var mappings = akai.GetMappings();
            Check(failures, mappings.Count == 7, $"GetMappings includes the keys plus the six device bindings (got {mappings.Count})");

            var hotspots = akai.GetDiagramHotspots();
            Check(failures, hotspots.Count > 0, "the Akai profile has diagram hotspots");
            var keysHotspot = hotspots.Find(h => h.Title.StartsWith("KEYS"));
            Check(failures, keysHotspot != null && keysHotspot.State == MidiSpotState.Assigned, "the KEYS region is marked assigned");
            var automationHotspot = hotspots.Find(h => h.Title.StartsWith("AUTOMATION"));
            Check(failures, automationHotspot != null && automationHotspot.State == MidiSpotState.Assigned && automationHotspot.Function.Contains("Record"),
                "the AUTOMATION hotspot's function names Record");
            var reservedHotspot = hotspots.Find(h => h.Title.StartsWith("ARP"));
            Check(failures, reservedHotspot != null && reservedHotspot.State == MidiSpotState.Unavailable, "a reserved button (ARP) is marked unavailable");
            Check(failures, akai.GetDiagramPath() != "", "the Akai profile has a diagram path");
        }

        void TestMidiNode(List<string> failures)
        {
            var node = new ChiptrackerMidiNode { KeyboardProfile = new AkaiMPKMini4() };
            Root.AddChild(node);

            var noteOnCount = 0;
            var lastNote = -1;
            var lastVolume = -1;
            node.Connect(ChiptrackerMidiNode.SignalName.NoteOn, Callable.From((int note, int volume) =>
            {
                noteOnCount++;
                lastNote = note;
                lastVolume = volume;
            }));
            var noteOffCount = 0;
            node.Connect(ChiptrackerMidiNode.SignalName.NoteOff, Callable.From((int note) => noteOffCount++));
            string lastAction = null;
            node.Connect(ChiptrackerMidiNode.SignalName.ActionRequested, Callable.From((string action) => lastAction = action));

            node.HandleMidiEvent(new InputEventMidi { Message = MidiMessage.NoteOn, Pitch = 60, Velocity = 100, Channel = 0 });
            Check(failures, noteOnCount == 1 && lastNote == 60 && lastVolume == new MidiKeyboard().MapVelocity(100),
                "a NoteOn with velocity fires NoteOn, mapped through the profile");

            node.HandleMidiEvent(new InputEventMidi { Message = MidiMessage.NoteOn, Pitch = 60, Velocity = 0, Channel = 0 });
            Check(failures, noteOffCount == 1, "a NoteOn with zero velocity fires NoteOff instead");

            node.HandleMidiEvent(new InputEventMidi { Message = MidiMessage.NoteOff, Pitch = 64, Channel = 0 });
            Check(failures, noteOffCount == 2, "a real NoteOff also fires NoteOff");

            node.HandleMidiEvent(new InputEventMidi { Message = MidiMessage.ControlChange, ControllerNumber = 78, ControllerValue = 127, Channel = 0 });
            Check(failures, lastAction == MidiKeyboard.ActionToggleRecord, "a control-change maps to an action via the profile");

            node.Enabled = false;
            node.HandleMidiEvent(new InputEventMidi { Message = MidiMessage.NoteOn, Pitch = 60, Velocity = 100, Channel = 0 });
            Check(failures, noteOnCount == 1, "a disabled node ignores events");
            node.Enabled = true;

            node.KeyboardProfile = new MidiKeyboard { MidiChannel = 5 };
            node.HandleMidiEvent(new InputEventMidi { Message = MidiMessage.NoteOn, Pitch = 60, Velocity = 100, Channel = 0 });
            Check(failures, noteOnCount == 1, "a channel-restricted profile ignores events on other channels");

            node.QueueFree();
        }

        async System.Threading.Tasks.Task TestMainViewMidiAction(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var view = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(view);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            // The Akai's AUTOMATION button (CC 78) toggles Record, once per press.
            var akai = new AkaiMPKMini4();
            Check(failures, akai.ActionForControlChange(78, 127) == MidiKeyboard.ActionToggleRecord, "AUTOMATION (CC 78) maps to ToggleRecord");
            Check(failures, akai.ActionForControlChange(78, 0) == "", "its release (value 0) does nothing");

            view.OnMidiAction(MidiKeyboard.ActionToggleRecord);
            Check(failures, view._recording, "the ToggleRecord action turns Record on");
            view.OnMidiAction(MidiKeyboard.ActionToggleRecord);
            Check(failures, !view._recording, "and off again");

            view.QueueFree();
        }
    }
}

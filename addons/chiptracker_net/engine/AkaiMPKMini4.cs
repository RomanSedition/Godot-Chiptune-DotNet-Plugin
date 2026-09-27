using System.Collections.Generic;
using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/akai_mpk_mini_4.gd.
    // Mapping profile for the Akai MPK Mini mk4, using the CC numbers
    // captured from one unit's current preset (see MidiKeyboardMap.md).
    // The Akai MIDI Editor lets you remap knobs/pads to different CC
    // numbers or channels -- if your program differs, update KnobCc (or
    // override HandleControlChange/AcceptsChannel) to match.
    // [GlobalClass] is safe in this project only -- see MidiKeyboard.cs.
    [Tool]
    [GlobalClass]
    public partial class AkaiMPKMini4 : MidiKeyboard
    {
        // K1-K8, DIVISION..BPM (see MidiKeyboardMap.md)
        public static readonly int[] KnobCc = { 24, 25, 26, 27, 28, 29, 30, 31 };

        const int CcContinue = 76;
        const int CcQuantize = 77;
        const int CcUndoButton = 73; // the button labelled UNDO on the device (captured under its printed label, "REDO")
        const int CcGlobal = 74;
        const int CcAutomation = 78;
        const int CcTapTempo = 82;

        class Binding
        {
            public string Action;
            public string Input;
        }

        // The single list of button-to-action mappings: drives both what
        // a press does and what the MIDI Keyboard tab displays. Input is
        // the label printed under the button on the device.
        static readonly Dictionary<int, Binding> Bindings = new()
        {
            [CcContinue] = new Binding { Action = ActionPlay, Input = "CONTINUE" },
            [CcQuantize] = new Binding { Action = ActionToggleEditMode, Input = "QUANTIZE" },
            [CcUndoButton] = new Binding { Action = ActionUndoGridEdit, Input = "UNDO button" },
            [CcGlobal] = new Binding { Action = ActionRedoGridEdit, Input = "GLOBAL" },
            [CcAutomation] = new Binding { Action = ActionToggleRecord, Input = "AUTOMATION" },
            [CcTapTempo] = new Binding { Action = ActionTapTempo, Input = "TAP TEMPO" },
        };

        const string DiagramPath = "res://addons/chiptracker_net/MIDI/Akai MPK Mini 4/Akai Mini 4 Diagram.jpg";

        const string Unassigned = "Unassigned";
        const string Reserved = "Reserved: the device keeps this button and sends no MIDI";
        const string PadNoteFormat = "Unassigned (currently plays note {0} like a key)";

        // One entry of Hotspots below. Cc ties a control to Bindings (and
        // shows its CC number); Detail is extra text for controls with no
        // CC; Fixed overrides the function text.
        class HotspotSpec
        {
            public string Name;
            public Rect2 Rect;
            public int? Cc;
            public int? PadNote;
            public string Detail;
            public string Fixed;
            public bool Unavailable;
            public bool Assigned;
        }

        // Regions on the 1733x608 diagram image, in its own pixels.
        static readonly HotspotSpec[] Hotspots =
        {
            new() { Name = "PITCH WHEEL", Rect = new Rect2(90, 85, 67, 220), Detail = "Pitch bend" },
            new() { Name = "MODULATION WHEEL", Rect = new Rect2(240, 85, 66, 220), Cc = 1 },
            new() { Name = "PAD 5", Rect = new Rect2(380, 38, 123, 125), PadNote = 40 },
            new() { Name = "PAD 6", Rect = new Rect2(518, 38, 123, 125), PadNote = 41 },
            new() { Name = "PAD 7", Rect = new Rect2(657, 38, 123, 125), PadNote = 42 },
            new() { Name = "PAD 8", Rect = new Rect2(796, 38, 123, 125), PadNote = 43 },
            new() { Name = "PAD 1", Rect = new Rect2(380, 180, 123, 122), PadNote = 36 },
            new() { Name = "PAD 2", Rect = new Rect2(518, 180, 123, 122), PadNote = 37 },
            new() { Name = "PAD 3", Rect = new Rect2(657, 180, 123, 122), PadNote = 38 },
            new() { Name = "PAD 4", Rect = new Rect2(796, 180, 123, 122), PadNote = 39 },
            new() { Name = "VOLUME (big encoder)", Rect = new Rect2(1008, 135, 106, 103), Cc = 14 },
            new() { Name = "- (bank)", Rect = new Rect2(967, 282, 85, 55), Cc = 80 },
            new() { Name = "+ (bank)", Rect = new Rect2(1066, 282, 90, 55), Cc = 81 },
            new() { Name = "SHIFT", Rect = new Rect2(967, 357, 90, 56), Cc = 17 },
            new() { Name = "PLUGIN/DAW", Rect = new Rect2(1066, 357, 90, 56), Fixed = Reserved, Unavailable = true },
            new() { Name = "OCT -", Rect = new Rect2(90, 355, 90, 60), Fixed = "Shifts the octave the keys send (handled by the device)", Unavailable = true },
            new() { Name = "OCT +", Rect = new Rect2(217, 355, 90, 60), Fixed = "Shifts the octave the keys send (handled by the device)", Unavailable = true },
            new() { Name = "ARP", Rect = new Rect2(403, 357, 87, 56), Fixed = Reserved, Unavailable = true },
            new() { Name = "LATCH (button)", Rect = new Rect2(503, 357, 90, 56), Fixed = Reserved, Unavailable = true },
            new() { Name = "NOTE REPEAT", Rect = new Rect2(606, 357, 87, 56), Fixed = Reserved, Unavailable = true },
            new() { Name = "TAP TEMPO", Rect = new Rect2(709, 357, 88, 56), Cc = 82 },
            new() { Name = "BANK A/B", Rect = new Rect2(811, 357, 87, 56), Cc = 12 },
            new() { Name = "UNDO button", Rect = new Rect2(1213, 357, 87, 56), Cc = 73 },
            new() { Name = "GLOBAL", Rect = new Rect2(1315, 357, 87, 56), Cc = 74 },
            new() { Name = "CONTINUE", Rect = new Rect2(1418, 357, 86, 56), Cc = 76 },
            new() { Name = "QUANTIZE", Rect = new Rect2(1517, 357, 88, 56), Cc = 77 },
            new() { Name = "AUTOMATION", Rect = new Rect2(1620, 357, 87, 56), Cc = 78 },
            new() { Name = "KNOB 1: DIVISION", Rect = new Rect2(1205, 55, 92, 92), Cc = 24 },
            new() { Name = "KNOB 2: SWING", Rect = new Rect2(1345, 55, 92, 92), Cc = 25 },
            new() { Name = "KNOB 3: MODE", Rect = new Rect2(1485, 55, 92, 92), Cc = 26 },
            new() { Name = "KNOB 4: OCT", Rect = new Rect2(1622, 55, 90, 92), Cc = 27 },
            new() { Name = "KNOB 5: LATCH", Rect = new Rect2(1205, 216, 92, 91), Cc = 28 },
            new() { Name = "KNOB 6: SYNC", Rect = new Rect2(1345, 216, 92, 91), Cc = 29 },
            new() { Name = "KNOB 7: GATE", Rect = new Rect2(1485, 216, 92, 91), Cc = 30 },
            new() { Name = "KNOB 8: BPM", Rect = new Rect2(1622, 216, 90, 91), Cc = 31 },
            new() { Name = "KEYS", Rect = new Rect2(0, 500, 1733, 95), Detail = "Notes, channel 1", Fixed = "Preview notes / enter notes in Edit mode", Assigned = true },
        };

        public AkaiMPKMini4()
        {
            DeviceName = "Akai MPK Mini mk4";
        }

        public override string GetDiagramPath() => DiagramPath;

        public override List<MidiHotspot> GetDiagramHotspots()
        {
            var hotspots = new List<MidiHotspot>();
            foreach (var spot in Hotspots)
            {
                var title = spot.Name;
                var function = Unassigned;
                var state = MidiSpotState.Free;
                if (spot.Unavailable)
                    state = MidiSpotState.Unavailable;
                else if (spot.Assigned)
                    state = MidiSpotState.Assigned;
                if (spot.Cc.HasValue)
                {
                    title += $" (CC {spot.Cc.Value})";
                    if (Bindings.TryGetValue(spot.Cc.Value, out var binding))
                    {
                        function = MidiKeyboard.ActionLabels[binding.Action];
                        state = MidiSpotState.Assigned;
                    }
                }
                else if (spot.PadNote.HasValue)
                {
                    title += $" (Note {spot.PadNote.Value}, channel 10)";
                    function = string.Format(PadNoteFormat, spot.PadNote.Value);
                }
                else if (spot.Detail != null)
                {
                    title += $" ({spot.Detail})";
                }
                if (spot.Fixed != null)
                    function = spot.Fixed;
                hotspots.Add(new MidiHotspot { Rect = spot.Rect, Title = title, Function = function, State = state });
            }
            return hotspots;
        }

        public override List<MidiMapping> GetMappings()
        {
            var mappings = base.GetMappings();
            foreach (var (cc, binding) in Bindings)
            {
                mappings.Add(new MidiMapping
                {
                    Function = MidiKeyboard.ActionLabels[binding.Action],
                    Input = $"{binding.Input} (CC {cc})",
                });
            }
            return mappings;
        }

        // Buttons send a positive value on press (some also send 0 on
        // release), so reacting only to value > 0 fires once per press.
        public override string ActionForControlChange(int controllerNumber, int value)
        {
            if (value > 0 && Bindings.TryGetValue(controllerNumber, out var binding))
                return binding.Action;
            return "";
        }

        public override void HandleControlChange(int controllerNumber, int value)
        {
            var knobIndex = System.Array.IndexOf(KnobCc, controllerNumber);
            if (knobIndex != -1)
                HandleKnob(knobIndex, value);
        }

        // Override to bind K1-K8 to specific tracker actions (e.g. K1 ->
        // volume, K2 -> tempo). Default is a no-op.
        protected virtual void HandleKnob(int knobIndex, int value)
        {
        }
    }
}

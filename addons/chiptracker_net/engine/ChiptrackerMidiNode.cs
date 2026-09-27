using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/chiptracker_midi_node.gd.
    // Add as a child of a ChiptrackerSongNode to drive tracker note entry/
    // preview from a physical MIDI keyboard. Assign a MidiKeyboard
    // resource (or a per-device subclass, e.g. AkaiMPKMini4) to
    // KeyboardProfile to configure how that device's notes/CCs map onto
    // Chiptracker.
    //
    // This node only translates raw InputEventMidi into the signals
    // below -- it does not itself write notes into a Song or trigger
    // playback. Connect to these signals (from the tracker UI, the
    // editor plugin, etc.) to decide what a note-on/off or knob turn
    // actually does.
    //
    // Explicitly NOT [GlobalClass] -- GDScript already declares
    // class_name ChiptrackerMidiNode, and Godot's global-class registry
    // is a single flat namespace shared between GDScript and C#.
    [Tool]
    public partial class ChiptrackerMidiNode : Node
    {
        [Export] public MidiKeyboard KeyboardProfile { get; set; }
        [Export] public bool Enabled { get; set; } = true;

        [Signal] public delegate void NoteOnEventHandler(int note, int volume);
        [Signal] public delegate void NoteOffEventHandler(int note);
        [Signal] public delegate void ControlChangedEventHandler(int controllerNumber, int value);
        [Signal] public delegate void ActionRequestedEventHandler(string action);

        public override void _Ready()
        {
            if (GetParent() is not ChiptrackerSongNode)
                GD.PushWarning("ChiptrackerMidiNode expects a ChiptrackerSongNode as its parent.");
            // Deliberately never closed (no matching OS.CloseMidiInputs()
            // in _ExitTree()): macOS's CoreMIDI backend cannot reopen a
            // port once closed within the same process ("MIDIDriverCoreMidi
            // cannot be reopened"), which would permanently kill MIDI
            // input for the rest of the editor/game session the first
            // time this node left the tree (scene reload, node deletion,
            // etc). Calling OpenMidiInputs() again on an already-open
            // driver is harmless, so it's safe to just leave it open for
            // the life of the process.
            OS.OpenMidiInputs();
            SetProcessInput(true);
        }

        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventMidi midiEvent)
                HandleMidiEvent(midiEvent);
        }

        // Public so the editor tab can forward events to this node:
        // edited-scene nodes don't receive hardware MIDI in the editor,
        // but nodes owned by the editor itself do.
        public void HandleMidiEvent(InputEventMidi midiEvent)
        {
            if (!Enabled || KeyboardProfile == null)
                return;
            if (!KeyboardProfile.AcceptsChannel(midiEvent.Channel))
                return;

            switch (midiEvent.Message)
            {
                case MidiMessage.NoteOn:
                    if (midiEvent.Velocity > 0)
                        EmitSignal(SignalName.NoteOn, KeyboardProfile.MapNote(midiEvent.Pitch), KeyboardProfile.MapVelocity(midiEvent.Velocity));
                    else
                        // Many keyboards send NoteOn with velocity 0 instead of a NoteOff.
                        EmitSignal(SignalName.NoteOff, KeyboardProfile.MapNote(midiEvent.Pitch));
                    break;
                case MidiMessage.NoteOff:
                    EmitSignal(SignalName.NoteOff, KeyboardProfile.MapNote(midiEvent.Pitch));
                    break;
                case MidiMessage.ControlChange:
                    KeyboardProfile.HandleControlChange(midiEvent.ControllerNumber, midiEvent.ControllerValue);
                    EmitSignal(SignalName.ControlChanged, midiEvent.ControllerNumber, midiEvent.ControllerValue);
                    var action = KeyboardProfile.ActionForControlChange(midiEvent.ControllerNumber, midiEvent.ControllerValue);
                    if (action != "")
                        EmitSignal(SignalName.ActionRequested, action);
                    break;
            }
        }
    }
}

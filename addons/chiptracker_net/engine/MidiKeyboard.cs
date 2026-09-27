using System.Collections.Generic;
using Godot;

namespace ChiptrackerNet.Engine
{
    // One entry of MidiKeyboard.GetMappings(): what a control does, and
    // which physical input triggers it, for display in the dock's MIDI
    // Keyboard tab.
    public class MidiMapping
    {
        public string Function;
        public string Input;
    }

    // How a control on a device diagram is marked: free to map, already
    // mapped, or not available to map (reserved by the device).
    public enum MidiSpotState
    {
        Free,
        Assigned,
        Unavailable,
    }

    // One clickable region on a MidiKeyboard.GetDiagramPath() image, in
    // the image's own pixels.
    public class MidiHotspot
    {
        public Rect2 Rect;
        public string Title;
        public string Function;
        public MidiSpotState State;
    }

    // C# counterpart of addons/chiptracker/engine/midi_keyboard.gd. Base
    // class for a MIDI keyboard's Chiptracker mapping profile. Subclass
    // per physical device (see AkaiMPKMini4) and override the mapping
    // methods below to match that device's note/CC layout. Assign an
    // instance to ChiptrackerMidiNode.KeyboardProfile.
    [Tool]
    public partial class MidiKeyboard : Resource
    {
        [Export] public string DeviceName { get; set; } = "";
        [Export] public int MidiChannel { get; set; } = -1; // -1 listens on every channel
        [Export] public int OctaveOffset { get; set; } = 0; // semitones added to incoming note numbers
        [Export] public int DefaultInstrumentId { get; set; } = 0;

        // Abstract Chiptracker actions a device profile can trigger.
        // Profiles map their own physical inputs to these names, so the
        // tracker never needs to know which button on which keyboard was
        // pressed.
        public const string ActionToggleEditMode = "toggle_edit_mode";
        public const string ActionPlay = "play";
        public const string ActionUndoGridEdit = "undo_grid_edit";
        public const string ActionRedoGridEdit = "redo_grid_edit";
        public const string ActionToggleRecord = "toggle_record";
        public const string ActionTapTempo = "tap_tempo";

        // Display names for the actions above, shown in the dock's MIDI Keyboard tab.
        public static readonly Dictionary<string, string> ActionLabels = new()
        {
            [ActionToggleEditMode] = "Toggle Edit mode",
            [ActionPlay] = "Play / Stop (double press: stop and go to top)",
            [ActionUndoGridEdit] = "Undo grid edit",
            [ActionRedoGridEdit] = "Redo grid edit",
            [ActionToggleRecord] = "Toggle Record (write notes at the playing row)",
            [ActionTapTempo] = "Tap tempo (tap along to set the tempo)",
        };

        // Maps an incoming MIDI note number (0-127) to a Chiptracker note
        // number (also MIDI-style, per Cell.Note). Override to remap a
        // device's key layout; the default just applies OctaveOffset.
        public virtual int MapNote(int midiNote) => midiNote + OctaveOffset * 12;

        // Maps MIDI velocity (0-127) to Chiptracker's 0-15 volume scale.
        public virtual int MapVelocity(int velocity) => Mathf.RoundToInt(velocity / 127.0f * 15.0f);

        // Every mapping this profile currently makes, for display.
        // Subclasses call base.GetMappings() and append their own device
        // controls.
        public virtual List<MidiMapping> GetMappings() => new()
        {
            new MidiMapping { Function = "Preview notes / enter notes in Edit mode", Input = "Keys" },
        };

        // res:// path of an image of the device, or "" if there isn't
        // one. Shown by the MIDI Keyboard tab's "Show Diagram" button.
        public virtual string GetDiagramPath() => "";

        // Clickable regions on the diagram image, in the image's own pixels.
        public virtual List<MidiHotspot> GetDiagramHotspots() => new();

        // Returns the action a control-change message should trigger, or
        // "" for none. Override in a device profile. Called for every
        // control change, so check `value` to react only once per button
        // press.
        public virtual string ActionForControlChange(int controllerNumber, int value) => "";

        // Called for every InputEventMidi control-change message
        // accepted by AcceptsChannel(). Override to bind knobs/faders/
        // pads to tracker actions (instrument select, tempo, etc).
        // Default is a no-op.
        public virtual void HandleControlChange(int controllerNumber, int value)
        {
        }

        // Whether this profile wants to hear events on the given MIDI
        // channel (InputEventMidi.Channel is 0-indexed).
        public bool AcceptsChannel(int channel) => MidiChannel < 0 || channel == MidiChannel;
    }
}

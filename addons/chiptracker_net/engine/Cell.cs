using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/cell.gd.
    [Tool]
    public partial class Cell : Resource
    {
        [Export] public int Note { get; set; } = -1; // MIDI-style note number, -1 = empty/no-op
        [Export] public int InstrumentId { get; set; } = 0;
        [Export] public int Volume { get; set; } = 15; // 0-15, NES-style 4-bit scale
        [Export] public string Effect { get; set; } = "";
        [Export] public int EffectParam { get; set; } = 0;

        // Snapshot of the fields actually reachable through the grid/cell-editor
        // UI today (not Effect/EffectParam, still stubs). Used by
        // GridEditHistory to record/restore a cell edit.
        public Dictionary Snapshot() => new()
        {
            ["note"] = Note,
            ["instrument_id"] = InstrumentId,
            ["volume"] = Volume,
        };

        public void ApplySnapshot(Dictionary data)
        {
            Note = data["note"].AsInt32();
            InstrumentId = data["instrument_id"].AsInt32();
            Volume = data["volume"].AsInt32();
        }
    }
}

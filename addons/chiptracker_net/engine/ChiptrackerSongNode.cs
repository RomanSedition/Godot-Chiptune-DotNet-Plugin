using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/chiptracker_song_node.gd.
    // Add this node to any scene to hold a Chiptracker song as real,
    // scene-persisted data. Song (and its nested Channel/Pattern/
    // Instrument/Cell types) are already Resources with [Export] fields,
    // so saving the scene saves the whole song -- channels, instruments,
    // patterns, order list -- as an inline sub-resource, and it shows up
    // in the Inspector like any other exported Resource property.
    //
    // Select this node in the Scene dock to edit its song in the
    // Chiptracker .Net tab (see ChiptrackerNetPlugin's _Handles()/
    // _Edit()) -- the tab binds directly to Song, so edits through the
    // tracker UI or the editor MCP bridge write straight into this
    // node's data.
    // NOT [GlobalClass]: the GDScript addon already declares
    // `class_name ChiptrackerSongNode` -- see
    // feedback_godot_csharp_globalclass_collision in memory. Referenced
    // by namespace (ChiptrackerNet.Engine.ChiptrackerSongNode) instead.
    [Tool]
    public partial class ChiptrackerSongNode : Node
    {
        [Export] public Song Song { get; set; } = new();

        // Read live by PatternGrid during playback -- lets the
        // continuously-interpolating playhead line be turned off
        // per-scene from the Inspector without touching code, while the
        // discrete row highlight (which most trackers use alone) stays
        // unaffected either way.
        [Export] public bool ShowSmoothPlayhead { get; set; } = true;
    }
}

using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/channel.gd.
    [Tool]
    public partial class Channel : Resource
    {
        [Export] public string Name { get; set; } = "";
        [Export] public string InstrumentType { get; set; } = "square"; // "square", "triangle", "noise"
        [Export] public bool Muted { get; set; } = false;
        [Export] public bool Solo { get; set; } = false; // if any channel is soloed, only soloed channels play

        // Index into Song.Groups -- which Group column this channel is
        // listed under in the dock's Groups tab, and which band it's drawn
        // in on the pattern grid. 0 is always "Group 1", the default/
        // leftmost group.
        [Export] public int GroupIndex { get; set; } = 0;
    }
}

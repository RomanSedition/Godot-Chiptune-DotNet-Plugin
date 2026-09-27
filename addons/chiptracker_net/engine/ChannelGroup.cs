using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/channel_group.gd. Empty
    // Name means display as "Group N" (1-based index) by index; a set
    // name shows instead -- same convention as Pattern.Name/Channel.Name.
    [Tool]
    public partial class ChannelGroup : Resource
    {
        [Export] public string Name { get; set; } = "";
    }
}

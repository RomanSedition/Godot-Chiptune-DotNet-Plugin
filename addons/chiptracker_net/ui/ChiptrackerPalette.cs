#if TOOLS
using Godot;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/theme_palette.gd. "Deep Sea"
    // chrome palette for the editor tab/dock (toolbars, panels, section
    // headers) -- NOT the pattern grid, which keeps its own green/yellow
    // tracker-style row coloring.
    public static class ChiptrackerPalette
    {
        public static readonly Color DeepNavy = new Color(0.075f, 0.137f, 0.227f);
        public static readonly Color SteelBlue = new Color(0.173f, 0.333f, 0.467f);
        public static readonly Color TealBlue = new Color(0.243f, 0.471f, 0.576f);
        public static readonly Color SkyBlue = new Color(0.290f, 0.596f, 0.839f);
        public static readonly Color PaleIce = new Color(0.914f, 0.945f, 0.945f);
    }
}
#endif

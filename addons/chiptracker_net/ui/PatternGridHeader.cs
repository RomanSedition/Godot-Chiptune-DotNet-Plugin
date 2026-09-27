#if TOOLS
using Godot;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/pattern_grid_header.gd.
    // Frozen header row for PatternGrid: sits above GridScroll (a sibling,
    // not a child of it) in chiptracker_main_view.tscn, so it never
    // scrolls vertically along with the pattern rows. It still needs to
    // track GridScroll's horizontal scroll position, though, so its bands
    // stay lined up with the columns scrolling underneath it.
    [Tool]
    public partial class PatternGridHeader : Control
    {
        PatternGrid _grid;
        ScrollContainer _scrollContainer;

        // Called once by ChiptrackerMainView after both PatternGrid and
        // this node already exist.
        public void Bind(PatternGrid grid, ScrollContainer scrollContainer)
        {
            _grid = grid;
            _scrollContainer = scrollContainer;
            ClipContents = true;
            CustomMinimumSize = new Vector2(CustomMinimumSize.X, grid.GetHeaderHeight());
            // The grid's own "draw" signal fires whenever ITS _Draw() runs
            // (song changed, selection moved, playback state changed,
            // etc.) -- redrawing in lockstep with it is a cheap way to
            // stay current without re-wiring every place the grid already
            // calls QueueRedraw() on itself.
            grid.Connect(CanvasItem.SignalName.Draw, new Callable(this, CanvasItem.MethodName.QueueRedraw));
            // "Draw" alone wouldn't catch a pure horizontal scroll.
            scrollContainer.GetHScrollBar().Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnScrollChanged));
        }

        void OnScrollChanged(double value) => QueueRedraw();

        public override void _Draw()
        {
            if (_grid == null)
                return;
            var scrollX = _scrollContainer != null ? _scrollContainer.ScrollHorizontal : 0.0f;
            DrawSetTransform(new Vector2(-scrollX, 0));
            _grid.DrawHeader(this);
        }
    }
}
#endif

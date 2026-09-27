#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/pattern_grid.gd. Custom-drawn
    // pattern grid. Godot has no built-in spreadsheet-style grid control,
    // so rows/columns/cell text/selection/playback cursor are all drawn
    // manually in _Draw(). Editing a cell's fields happens in the side
    // panel/cell editor rather than in-grid text entry.
    //
    // Deferred to a later M6 slice (per the "core" scope): song_node
    // (ChiptrackerSongNode doesn't exist in the C# port yet) and grid-edit
    // undo/redo (GridUndoRequested/GridRedoRequested are declared and
    // fire, but ChiptrackerMainView doesn't act on them yet).
    [Tool]
    public partial class PatternGrid : Control
    {
        [Signal] public delegate void CellSelectedEventHandler(int row, int channel);
        [Signal] public delegate void OctaveChangedEventHandler(int newOctave);
        [Signal] public delegate void NoteKeyPressedEventHandler(int note);
        [Signal] public delegate void NoteClearedEventHandler();
        [Signal] public delegate void GridUndoRequestedEventHandler();
        [Signal] public delegate void GridRedoRequestedEventHandler();
        [Signal] public delegate void BarPreviewRequestedEventHandler();

        // Shift+Up/Down while the cursor sits on a note: emitted once the
        // target row (one step in that direction) has already been
        // checked in-bounds and empty.
        [Signal] public delegate void NoteMoveRequestedEventHandler(int fromRow, int toRow, int channel);

        // Fallback only -- _Ready() overwrites this with a value that
        // actually fits the theme's font.
        public float RowHeight = 20.0f;
        internal const float ChannelWidth = 100.0f;
        const float RowNumWidth = 40.0f;
        const float HeaderHeight = 32.0f;

        // Band above the channel-name header showing which Group each
        // channel belongs to. Fallback only, see _Ready().
        public float GroupHeaderHeight = 26.0f;

        // Horizontal gap inserted between two adjacent groups' channel blocks.
        internal const float GroupGap = 14.0f;

        static readonly Color RowGreen = new(0.4f, 0.85f, 0.35f);
        static readonly Color BeatYellow = new(0.9f, 0.85f, 0.25f);

        const int MinOctave = 0;
        const int MaxOctave = 7;

        // Standard tracker "piano" keyboard layout: bottom row (Z...M)
        // plays the current octave's 12 semitones, top row (Q...U) plays
        // the next octave up.
        static readonly Dictionary<Key, int> NoteKeyOffsets = new()
        {
            [Key.Z] = 0, [Key.S] = 1, [Key.X] = 2, [Key.D] = 3, [Key.C] = 4, [Key.V] = 5,
            [Key.G] = 6, [Key.B] = 7, [Key.H] = 8, [Key.N] = 9, [Key.J] = 10, [Key.M] = 11,
            [Key.Q] = 12, [Key.Key2] = 13, [Key.W] = 14, [Key.Key3] = 15, [Key.E] = 16, [Key.R] = 17,
            [Key.Key5] = 18, [Key.T] = 19, [Key.Key6] = 20, [Key.Y] = 21, [Key.Key7] = 22, [Key.U] = 23,
        };

        public Song Song;
        public int PatternIndex;
        public int SelectedRow;
        public int SelectedChannel;
        public int PlaybackRow = -1;
        public bool EditMode;

        // Set by ChiptrackerMainView whenever playback starts, so this can
        // draw a continuously-interpolating playhead line.
        public PlaybackCursor PlaybackState;

        // Set by ChiptrackerMainView.BindSongNode() -- read live in
        // _Draw() so toggling ChiptrackerSongNode.ShowSmoothPlayhead in
        // the Inspector takes effect immediately, including mid-playback.
        // Null (no bound node, e.g. the tab's own scratch demo song)
        // defaults to showing the line.
        public Engine.ChiptrackerSongNode SongNode;

        // The octave "Z" maps to -- e.g. octave 4 makes Z == note 60 (C4).
        public int Octave = 4;

        ScrollContainer _scrollContainer;

        public override void _Ready()
        {
            FocusMode = FocusModeEnum.All;
            _scrollContainer = GetParent() as ScrollContainer;
            // Size the row to the font's ascent alone, not the full
            // height (ascent+descent) -- descent reserves room for
            // lowercase descenders, which never appear in this grid's
            // text (note names, row numbers, hex volume are all
            // uppercase/digits/symbols).
            var font = GetThemeDefaultFont();
            var fontSize = GetThemeDefaultFontSize();
            RowHeight = Mathf.Ceil(font.GetAscent(fontSize));
            GroupHeaderHeight = RowHeight + 12.0f;
        }

        // Only actually redraws while something is playing -- the smooth
        // playhead line's position changes every frame.
        public override void _Process(double delta)
        {
            if (PlaybackState != null && PlaybackState.Playing)
                QueueRedraw();
        }

        public void SetSong(Song newSong)
        {
            Song = newSong;
            PatternIndex = 0;
            SelectedRow = 0;
            SelectedChannel = 0;
            UpdateMinSize();
            QueueRedraw();
            EnsureSelectionVisible();
        }

        public void SetPatternIndex(int index)
        {
            if (Song == null || index < 0 || index >= Song.Patterns.Count)
                return;
            PatternIndex = index;
            SelectedRow = 0;
            UpdateMinSize();
            QueueRedraw();
            EnsureSelectionVisible();
        }

        // Switches the displayed pattern without resetting selection --
        // used to follow playback across a pattern boundary.
        public void FollowPattern(int index)
        {
            if (Song == null || index < 0 || index >= Song.Patterns.Count || index == PatternIndex)
                return;
            PatternIndex = index;
            UpdateMinSize();
            QueueRedraw();
        }

        public void SetPlaybackRow(int row)
        {
            PlaybackRow = row;
            QueueRedraw();
            if (PlaybackRow >= 0)
            {
                var rowTop = RowHeight * PlaybackRow;
                EnsureVerticalVisible(rowTop, rowTop + RowHeight);
            }
        }

        // Total header height: the group-name band plus the channel-name
        // band below it.
        public float GetHeaderHeight() => GroupHeaderHeight + HeaderHeight;

        public void EnsureSelectionVisible()
        {
            var rowTop = RowHeight * SelectedRow;
            EnsureVerticalVisible(rowTop, rowTop + RowHeight);
            var layout = ChannelLayout();
            if (layout.XForChannel.TryGetValue(SelectedChannel, out var colLeft))
                EnsureHorizontalVisible(colLeft, colLeft + ChannelWidth);
        }

        void EnsureVerticalVisible(float top, float bottom)
        {
            if (_scrollContainer == null)
                return;
            var viewTop = _scrollContainer.ScrollVertical;
            var viewBottom = viewTop + _scrollContainer.Size.Y;
            if (top < viewTop)
                _scrollContainer.ScrollVertical = (int)top;
            else if (bottom > viewBottom)
                _scrollContainer.ScrollVertical = (int)(bottom - _scrollContainer.Size.Y);
        }

        void EnsureHorizontalVisible(float left, float right)
        {
            if (_scrollContainer == null)
                return;
            var viewLeft = _scrollContainer.ScrollHorizontal;
            var viewRight = viewLeft + _scrollContainer.Size.X;
            if (left < viewLeft)
                _scrollContainer.ScrollHorizontal = (int)left;
            else if (right > viewRight)
                _scrollContainer.ScrollHorizontal = (int)(right - _scrollContainer.Size.X);
        }

        public Cell GetSelectedCell()
        {
            if (Song == null || PatternIndex >= Song.Patterns.Count)
                return null;
            var pattern = Song.Patterns[PatternIndex];
            if (SelectedRow < 0 || SelectedRow >= pattern.Rows.Count)
                return null;
            if (SelectedChannel < 0 || SelectedChannel >= pattern.Rows[SelectedRow].Count)
                return null;
            return pattern.Rows[SelectedRow][SelectedChannel].As<Cell>();
        }

        public void UpdateMinSize()
        {
            if (Song == null || Song.Patterns.Count == 0)
            {
                CustomMinimumSize = new Vector2(RowNumWidth, 0);
                return;
            }
            var pattern = Song.Patterns[PatternIndex];
            var rowCount = pattern.Rows.Count;
            CustomMinimumSize = new Vector2(ChannelLayout().TotalWidth, RowHeight * rowCount);
        }

        // Channels in on-screen left-to-right order: grouped by
        // Channel.GroupIndex (Group 1/index 0 first), then by original
        // channel index within a group.
        internal List<int> ChannelDisplayOrder()
        {
            if (Song == null)
                return new List<int>();
            var order = Enumerable.Range(0, Song.Channels.Count).ToList();
            order.Sort((a, b) =>
            {
                var ga = Song.Channels[a].GroupIndex;
                var gb = Song.Channels[b].GroupIndex;
                return ga != gb ? ga.CompareTo(gb) : a.CompareTo(b);
            });
            return order;
        }

        internal class GroupBlock
        {
            public int GroupIndex;
            public float StartX;
            public float EndX;
        }

        internal class Layout
        {
            public List<int> Order;
            public Dictionary<int, float> XForChannel;
            public float TotalWidth;
            public List<GroupBlock> GroupBlocks;
        }

        // Screen-space layout derived from ChannelDisplayOrder(): each
        // channel's left-edge x, the grid's total content width, and the
        // on-screen span of each contiguous same-group run of channels.
        internal Layout ChannelLayout()
        {
            var order = ChannelDisplayOrder();
            var xForChannel = new Dictionary<int, float>();
            var groupBlocks = new List<GroupBlock>();
            var x = RowNumWidth;
            var prevGroup = -1;
            for (var i = 0; i < order.Count; i++)
            {
                var ch = order[i];
                var g = Song.Channels[ch].GroupIndex;
                if (g != prevGroup)
                {
                    if (i > 0)
                        x += GroupGap;
                    groupBlocks.Add(new GroupBlock { GroupIndex = g, StartX = x, EndX = x });
                }
                xForChannel[ch] = x;
                x += ChannelWidth;
                groupBlocks[^1].EndX = x;
                prevGroup = g;
            }
            return new Layout { Order = order, XForChannel = xForChannel, TotalWidth = x, GroupBlocks = groupBlocks };
        }

        // Inverse of ChannelLayout()'s XForChannel: which channel's column
        // (if any) an x coordinate falls inside.
        internal int ChannelAtX(float x)
        {
            foreach (var (ch, left) in ChannelLayout().XForChannel)
            {
                if (x >= left && x < left + ChannelWidth)
                    return ch;
            }
            return -1;
        }

        // Steps to the previous/next channel in on-screen (grouped)
        // left-to-right order, not raw array-index order.
        internal int AdjacentChannel(int fromChannel, int direction)
        {
            var order = ChannelDisplayOrder();
            var pos = order.IndexOf(fromChannel);
            if (pos == -1)
                return fromChannel;
            pos = Mathf.Clamp(pos + direction, 0, order.Count - 1);
            return order[pos];
        }

        // Draws the group-name band and channel-name band onto `target`
        // -- kept here (alongside the layout math it depends on) rather
        // than duplicated on PatternGridHeader, so the frozen header can
        // never draw something different from what this grid's own
        // layout actually is.
        public void DrawHeader(CanvasItem target)
        {
            if (Song == null)
                return;
            var font = GetThemeDefaultFont();
            var fontSize = GetThemeDefaultFontSize();
            var layout = ChannelLayout();

            var groupLabelFont = new FontVariation { BaseFont = font, VariationEmbolden = 1.2f };
            var groupLabelBaseline = GroupHeaderHeight - 6.0f;
            foreach (var block in layout.GroupBlocks)
            {
                var g = block.GroupIndex;
                var groupLabel = g < Song.Groups.Count && !string.IsNullOrEmpty(Song.Groups[g].Name)
                    ? Song.Groups[g].Name
                    : $"Group {g + 1}";
                var blockWidth = block.EndX - block.StartX;
                target.DrawRect(new Rect2(block.StartX, 0, blockWidth, GroupHeaderHeight - 2), new Color(0.15f, 0.15f, 0.2f));
                target.DrawString(groupLabelFont, new Vector2(block.StartX + 4, groupLabelBaseline), groupLabel, HorizontalAlignment.Center, blockWidth - 8, fontSize, new Color(0.75f, 0.8f, 0.85f));
            }

            foreach (var ch in layout.Order)
            {
                var x = layout.XForChannel[ch];
                target.DrawRect(new Rect2(x, GroupHeaderHeight, ChannelWidth, HeaderHeight - 4), new Color(0.2f, 0.2f, 0.25f));
                var label = string.IsNullOrEmpty(Song.Channels[ch].Name) ? $"Ch {ch}" : Song.Channels[ch].Name;
                target.DrawString(font, new Vector2(x + 4, GroupHeaderHeight + HeaderHeight - 12), label, HorizontalAlignment.Left, ChannelWidth - 8, fontSize);
            }
        }

        public override void _Draw()
        {
            if (Song == null || Song.Patterns.Count == 0 || PatternIndex >= Song.Patterns.Count)
                return;
            var pattern = Song.Patterns[PatternIndex];
            var rowCount = pattern.Rows.Count;
            var font = GetThemeDefaultFont();
            var fontSize = GetThemeDefaultFontSize();
            var rowTextBaseline = RowHeight;

            var layout = ChannelLayout();

            for (var row = 0; row < rowCount; row++)
            {
                var y = RowHeight * row;

                var accent = AccentColor(row);
                Color rowBg;
                if (row == PlaybackRow)
                    rowBg = new Color(0.2f, 0.3f, 0.55f);
                else if (row % 4 == 0)
                    rowBg = new Color(0.16f, 0.16f, 0.1f);
                else
                    rowBg = new Color(0.1f, 0.12f, 0.1f);

                DrawRect(new Rect2(0, y, RowNumWidth, RowHeight), rowBg);
                foreach (var block in layout.GroupBlocks)
                    DrawRect(new Rect2(block.StartX, y, block.EndX - block.StartX, RowHeight), rowBg);

                DrawString(font, new Vector2(4, y + rowTextBaseline), $"{row:D2}", HorizontalAlignment.Left, RowNumWidth - 4, fontSize, accent);

                var rowCells = pattern.Rows[row];
                foreach (var ch in layout.Order)
                {
                    var x = layout.XForChannel[ch];
                    if (row == SelectedRow && ch == SelectedChannel)
                        DrawRect(new Rect2(x, y, ChannelWidth, RowHeight), new Color(0.3f, 0.5f, 0.8f, 0.6f));

                    var cell = rowCells[ch].As<Cell>();
                    var text = cell.Note >= 0
                        ? $"{Synth.NoteName(cell.Note)} {cell.InstrumentId:D2} {cell.Volume:X2}"
                        : "--- .. ..";
                    var textColor = cell.Note >= 0 ? accent : new Color(accent.R, accent.G, accent.B, 0.35f);
                    DrawString(font, new Vector2(x + 4, y + rowTextBaseline), text, HorizontalAlignment.Left, ChannelWidth - 8, fontSize, textColor);
                }
            }

            var totalHeight = RowHeight * rowCount;
            foreach (var block in layout.GroupBlocks)
            {
                var blockChannelCount = Mathf.RoundToInt((block.EndX - block.StartX) / ChannelWidth);
                for (var i = 0; i <= blockChannelCount; i++)
                {
                    var x = block.StartX + ChannelWidth * i;
                    DrawLine(new Vector2(x, 0), new Vector2(x, totalHeight), new Color(0.35f, 0.35f, 0.35f));
                }
            }

            // Continuously-interpolating playhead line, on top of the
            // discrete per-row highlight -- moves smoothly through the
            // current row instead of only snapping at row boundaries.
            var smoothPlayheadEnabled = SongNode == null || SongNode.ShowSmoothPlayhead;
            if (smoothPlayheadEnabled && PlaybackState != null && PlaybackState.Playing && PlaybackRow >= 0)
            {
                var progress = (float)PlaybackState.SamplesIntoRow / Mathf.Max(PlaybackState.SamplesPerRow, 1);
                var lineY = RowHeight * (PlaybackRow + progress);
                DrawLine(new Vector2(RowNumWidth, lineY), new Vector2(layout.TotalWidth, lineY), new Color(1.0f, 1.0f, 1.0f, 0.9f), 2.0f);
            }
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (Song == null || Song.Patterns.Count == 0)
                return;
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouseEvent)
            {
                var pattern = Song.Patterns[PatternIndex];
                var pos = mouseEvent.Position;
                if (pos.Y < 0 || pos.X < RowNumWidth)
                    return;
                var row = (int)(pos.Y / RowHeight);
                var channel = ChannelAtX(pos.X);
                if (row < 0 || row >= pattern.Rows.Count)
                    return;
                if (channel == -1)
                    return;
                SelectedRow = row;
                SelectedChannel = channel;
                GrabFocus();
                QueueRedraw();
                EnsureSelectionVisible();
                EmitSignal(SignalName.CellSelected, row, channel);
            }
        }

        // _Input (not _GuiInput) so arrow-key navigation and keyboard note
        // entry work whenever Edit mode is on, regardless of which
        // control currently has UI focus.
        public override void _Input(InputEvent @event)
        {
            if (!EditMode || Song == null || Song.Patterns.Count == 0)
                return;
            if (@event is not InputEventKey { Pressed: true } keyEvent)
                return;
            if (GetViewport().GuiGetFocusOwner() is LineEdit)
                return;

            // Grid-edit undo/redo: plain Ctrl (not Cmd/meta).
            if (keyEvent.CtrlPressed && !keyEvent.MetaPressed && keyEvent.Keycode == Key.Z)
            {
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.GridUndoRequested);
                return;
            }
            if (keyEvent.CtrlPressed && !keyEvent.MetaPressed && keyEvent.Keycode == Key.Y)
            {
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.GridRedoRequested);
                return;
            }

            if (keyEvent.Keycode == Key.Space)
            {
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.BarPreviewRequested);
                return;
            }
            if (keyEvent.Keycode == Key.Backspace)
            {
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.NoteCleared);
                return;
            }
            if (keyEvent.Keycode == Key.Minus)
            {
                Octave = Mathf.Max(Octave - 1, MinOctave);
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.OctaveChanged, Octave);
                return;
            }
            if (keyEvent.Keycode == Key.Equal)
            {
                Octave = Mathf.Min(Octave + 1, MaxOctave);
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.OctaveChanged, Octave);
                return;
            }
            if (NoteKeyOffsets.TryGetValue(keyEvent.Keycode, out var offset))
            {
                var note = 12 * (Octave + 1) + offset;
                GetViewport().SetInputAsHandled();
                EmitSignal(SignalName.NoteKeyPressed, note);
                return;
            }

            var pat = Song.Patterns[PatternIndex];

            // Shift+Up/Down: relocate the note at the cursor to the
            // adjacent row in that direction, but only if in-bounds and vacant.
            if (keyEvent.ShiftPressed && (keyEvent.Keycode == Key.Up || keyEvent.Keycode == Key.Down))
            {
                var cell = pat.Rows[SelectedRow][SelectedChannel].As<Cell>();
                if (cell.Note != -1)
                {
                    GetViewport().SetInputAsHandled();
                    var direction = keyEvent.Keycode == Key.Up ? -1 : 1;
                    var targetRow = SelectedRow + direction;
                    if (targetRow >= 0 && targetRow < pat.Rows.Count &&
                        pat.Rows[targetRow][SelectedChannel].As<Cell>().Note == -1)
                    {
                        EmitSignal(SignalName.NoteMoveRequested, SelectedRow, targetRow, SelectedChannel);
                    }
                    return;
                }
            }

            var handled = true;
            switch (keyEvent.Keycode)
            {
                case Key.Up:
                    SelectedRow = Mathf.Max(SelectedRow - 1, 0);
                    break;
                case Key.Down:
                    SelectedRow = Mathf.Min(SelectedRow + 1, pat.Rows.Count - 1);
                    break;
                case Key.Left:
                    SelectedChannel = AdjacentChannel(SelectedChannel, -1);
                    break;
                case Key.Right:
                    SelectedChannel = AdjacentChannel(SelectedChannel, 1);
                    break;
                default:
                    handled = false;
                    break;
            }
            if (handled)
            {
                GetViewport().SetInputAsHandled();
                QueueRedraw();
                EnsureSelectionVisible();
                EmitSignal(SignalName.CellSelected, SelectedRow, SelectedChannel);
            }
        }

        // Moves the cursor down `rows` rows, stopping at the pattern's last row.
        public void MoveCursorDown(int rows)
        {
            if (Song == null || Song.Patterns.Count == 0 || rows <= 0)
                return;
            var pattern = Song.Patterns[PatternIndex];
            SelectedRow = Mathf.Min(SelectedRow + rows, pattern.Rows.Count - 1);
            QueueRedraw();
            EnsureSelectionVisible();
            EmitSignal(SignalName.CellSelected, SelectedRow, SelectedChannel);
        }

        static Color AccentColor(int row) => row % 4 == 0 ? BeatYellow : RowGreen;
    }
}
#endif

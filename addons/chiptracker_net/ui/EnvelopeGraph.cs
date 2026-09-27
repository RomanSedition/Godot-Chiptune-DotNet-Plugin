#if TOOLS
using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/envelope_graph.gd.
    // Draggable visual editor for a variable-length envelope (see
    // Instrument.Envelope -- any length, not just 4 points). Each point
    // has a level (y) and a time along the note (x, in [0, 1]). Drag a
    // point to change both; it can't be dragged earlier than the point
    // before it or later than the one after it. Holding Shift snaps the
    // time to the nearest 0.1 and the level to the nearest ValueSnap.
    //
    // The level's axis is [ValueMin, ValueMax] (default 0-1, a volume
    // level). InstrumentPanel reuses this same control for the pitch
    // envelope with ValueMin/ValueMax set to +/-Instrument.PitchRange
    // (semitones) instead. When the range spans zero, a center line
    // marks it.
    [Tool]
    public partial class EnvelopeGraph : Control
    {
        [Signal] public delegate void PointChangedEventHandler(int index, float value);
        [Signal] public delegate void PointTimeChangedEventHandler(int index, float time);
        [Signal] public delegate void PointSelectedEventHandler(int index);

        const float Margin = 12.0f;
        const float PointRadius = 6.0f;
        const float GridStep = 0.1f;

        public List<float> Values = new() { 1.0f, 1.0f, 1.0f, 1.0f };

        // Each point's time (0-1), always the same length as Values.
        public List<float> Times = new() { 0.0f, 1.0f / 3.0f, 2.0f / 3.0f, 1.0f };
        public int SelectedIndex;
        public bool Editable = true;

        // The level axis's range -- see the class comment. Points are clamped to it.
        public float ValueMin = 0.0f;
        public float ValueMax = 1.0f;

        // Shift-drag snaps the level to the nearest multiple of this, an
        // absolute amount rather than a fraction of the range (0.1 for a
        // 0-1 level, 1.0 for whole semitones). Time always snaps to
        // GridStep regardless.
        public float ValueSnap = GridStep;

        // printf-style format for the value half of a hovered point's tooltip.
        public string ValueFormat = "F3";

        int _draggingIndex = -1;
        int _hoveredIndex = -1;

        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(0, 140);
            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));
            MouseDefaultCursorShape = CursorShape.PointingHand;
        }

        // `newTimes` says where each point sits along the note; pass null
        // (or the wrong length) for points spaced evenly from 0 to 1.
        public void SetValues(IEnumerable<float> newValues, IEnumerable<float> newTimes = null)
        {
            Values = new List<float>(newValues);
            var timesList = newTimes != null ? new List<float>(newTimes) : null;
            Times = timesList != null && timesList.Count == Values.Count ? timesList : EvenTimes(Values.Count);
            SelectedIndex = Mathf.Clamp(SelectedIndex, 0, Mathf.Max(0, Values.Count - 1));
            QueueRedraw();
        }

        public void SetSelectedIndex(int index)
        {
            SelectedIndex = Mathf.Clamp(index, 0, Mathf.Max(0, Values.Count - 1));
            QueueRedraw();
        }

        static List<float> EvenTimes(int count)
        {
            var result = new List<float>();
            for (var i = 0; i < count; i++)
                result.Add(count <= 1 ? 0.0f : (float)i / (count - 1));
            return result;
        }

        Rect2 PlotRect() => new(new Vector2(Margin, Margin), Size - new Vector2(Margin, Margin) * 2.0f);

        // Where `value` sits on the axis, as a 0 (bottom, ValueMin) to 1
        // (top, ValueMax) fraction.
        float ToFraction(float value)
        {
            var span = ValueMax - ValueMin;
            return span > 0.0f ? Mathf.Clamp((value - ValueMin) / span, 0.0f, 1.0f) : 0.0f;
        }

        float FromFraction(float fraction) => ValueMin + fraction * (ValueMax - ValueMin);

        Vector2 PointPosition(int index)
        {
            var plot = PlotRect();
            var time = index < Times.Count ? Times[index] : 0.0f;
            var x = plot.Position.X + plot.Size.X * Mathf.Clamp(time, 0.0f, 1.0f);
            var v = index < Values.Count ? Values[index] : ValueMax;
            var y = plot.Position.Y + plot.Size.Y * (1.0f - ToFraction(v));
            return new Vector2(x, y);
        }

        float ValueFromY(float y)
        {
            var plot = PlotRect();
            var fraction = 1.0f - Mathf.Clamp((y - plot.Position.Y) / plot.Size.Y, 0.0f, 1.0f);
            return FromFraction(fraction);
        }

        float TimeFromX(float x)
        {
            var plot = PlotRect();
            return Mathf.Clamp((x - plot.Position.X) / plot.Size.X, 0.0f, 1.0f);
        }

        // The earliest and latest time point `index` may take: its
        // neighbours', or 0 and 1 at the ends.
        Vector2 TimeBounds(int index)
        {
            var earliest = index > 0 ? Times[index - 1] : 0.0f;
            var latest = index < Times.Count - 1 ? Times[index + 1] : 1.0f;
            return new Vector2(earliest, latest);
        }

        public override void _Draw()
        {
            var plot = PlotRect();

            // Gridlines at every 0.1 step -- the 0.0/0.5/1.0 lines are
            // drawn a shade brighter so the eye has a few fixed reference
            // points among the ten.
            var stepCount = Mathf.RoundToInt(1.0f / GridStep);
            for (var step = 0; step <= stepCount; step++)
            {
                var v = step * GridStep;
                var isMajor = step % 5 == 0;
                var color = ChiptrackerPalette.PaleIce;
                color.A = isMajor ? 0.35f : 0.12f;
                var y = plot.Position.Y + plot.Size.Y * (1.0f - v);
                DrawLine(new Vector2(plot.Position.X, y), new Vector2(plot.End.X, y), color, 1.0f);
                var x = plot.Position.X + plot.Size.X * v;
                DrawLine(new Vector2(x, plot.Position.Y), new Vector2(x, plot.End.Y), color, 1.0f);
            }

            // A signed range (e.g. the pitch graph) gets a solid line at
            // zero, since that's the meaningful "no change" reference.
            if (ValueMin < 0.0f && ValueMax > 0.0f)
            {
                var zeroY = plot.Position.Y + plot.Size.Y * (1.0f - ToFraction(0.0f));
                DrawLine(new Vector2(plot.Position.X, zeroY), new Vector2(plot.End.X, zeroY), ChiptrackerPalette.PaleIce, 1.5f);
            }

            if (Values.Count == 0)
                return;

            // Connecting line between points.
            var points = new Vector2[Values.Count];
            for (var i = 0; i < Values.Count; i++)
                points[i] = PointPosition(i);
            // Before the first point and after the last, the level holds:
            // a fainter flat line shows that when the ends aren't at the edges.
            var holdColor = ChiptrackerPalette.SkyBlue;
            holdColor.A = 0.45f;
            if (points[0].X > plot.Position.X)
                DrawLine(new Vector2(plot.Position.X, points[0].Y), points[0], holdColor, 2.0f);
            if (points[^1].X < plot.End.X)
                DrawLine(points[^1], new Vector2(plot.End.X, points[^1].Y), holdColor, 2.0f);
            for (var i = 0; i < points.Length - 1; i++)
                DrawLine(points[i], points[i + 1], ChiptrackerPalette.SkyBlue, 2.0f);

            // Draggable handles -- selected/dragging get a solid fill plus
            // a ring, hovered gets a lighter tint, everything else stays plain.
            for (var i = 0; i < Values.Count; i++)
            {
                var isActive = i == _draggingIndex || i == SelectedIndex;
                var isHovered = i == _hoveredIndex;
                Color fill;
                if (isActive)
                    fill = ChiptrackerPalette.SkyBlue;
                else if (isHovered)
                    fill = ChiptrackerPalette.SkyBlue.Lerp(ChiptrackerPalette.PaleIce, 0.5f);
                else
                    fill = ChiptrackerPalette.PaleIce;
                DrawCircle(points[i], PointRadius, fill);
                if (i == SelectedIndex)
                    DrawArc(points[i], PointRadius + 3.0f, 0.0f, Mathf.Tau, 20, ChiptrackerPalette.SkyBlue, 1.5f);
            }
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (!Editable)
                return;
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseEvent)
            {
                if (mouseEvent.Pressed)
                {
                    var index = FindPointNear(mouseEvent.Position);
                    _draggingIndex = index;
                    if (index >= 0)
                    {
                        SelectedIndex = index;
                        EmitSignal(SignalName.PointSelected, index);
                    }
                }
                else
                {
                    _draggingIndex = -1;
                }
                QueueRedraw();
            }
            else if (@event is InputEventMouseMotion motionEvent)
            {
                var hovered = FindPointNear(motionEvent.Position);
                if (hovered != _hoveredIndex)
                {
                    _hoveredIndex = hovered;
                    TooltipText = hovered >= 0 ? $"{Values[hovered].ToString(ValueFormat)} at {Times[hovered]:F3}" : "";
                }
                if (_draggingIndex >= 0 && _draggingIndex < Values.Count)
                    DragTo(_draggingIndex, motionEvent.Position, motionEvent.ShiftPressed);
                QueueRedraw();
            }
        }

        // Moves point `index` under the mouse. With `snap`, the level
        // goes to the nearest ValueSnap and the time to the nearest 0.1
        // first; the time is then held between its neighbours (snapping
        // can't push it past one).
        void DragTo(int index, Vector2 mouse, bool snap)
        {
            var value = ValueFromY(mouse.Y);
            var time = TimeFromX(mouse.X);
            if (snap)
            {
                value = Mathf.Round(value / ValueSnap) * ValueSnap;
                time = Mathf.Round(time / GridStep) * GridStep;
            }
            var bounds = TimeBounds(index);
            time = Mathf.Clamp(time, bounds.X, bounds.Y);
            Values[index] = Mathf.Clamp(value, ValueMin, ValueMax);
            Times[index] = time;
            EmitSignal(SignalName.PointChanged, index, Values[index]);
            EmitSignal(SignalName.PointTimeChanged, index, time);
        }

        int FindPointNear(Vector2 pos)
        {
            var closest = -1;
            var closestDist = PointRadius * 3.0f; // generous hit area, easier to grab
            for (var i = 0; i < Values.Count; i++)
            {
                var dist = PointPosition(i).DistanceTo(pos);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = i;
                }
            }
            return closest;
        }
    }
}
#endif

#if TOOLS
using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/midi_diagram_view.gd. Draws
    // a keyboard's diagram image scaled to fit, marks which controls are
    // already mapped (green) or unavailable (dimmed), outlines the
    // control under the mouse in orange, and shows a tooltip naming that
    // control and the function currently assigned to it. Hotspots come
    // from the keyboard profile (MidiKeyboard.GetDiagramHotspots()) in
    // the diagram image's own pixels; controls with no marker are free to
    // map.
    [Tool]
    public partial class MidiDiagramView : Control
    {
        static readonly Color HighlightColor = new(1.0f, 0.55f, 0.1f);
        const float HighlightFillAlpha = 0.2f;
        const float OutlineWidth = 3.0f;
        static readonly Color AssignedColor = new(0.2f, 0.8f, 0.35f);
        const float AssignedFillAlpha = 0.28f;
        const float AssignedOutlineWidth = 2.0f;
        static readonly Color UnavailableFill = new(0.1f, 0.1f, 0.1f, 0.5f);
        const float LegendHeight = 32.0f;
        const int LegendFontSize = 14;
        static readonly Color LegendTextColor = new(0.9f, 0.9f, 0.9f);
        const float SwatchSize = 16.0f;

        Texture2D _texture;
        List<MidiHotspot> _hotspots = new();
        int _hovered = -1;

        public void Setup(Texture2D texture, List<MidiHotspot> hotspots)
        {
            _texture = texture;
            _hotspots = hotspots;
            _hovered = -1;
            MouseFilter = MouseFilterEnum.Stop;
            QueueRedraw();
        }

        public override void _Ready()
        {
            Connect(SignalName.MouseExited, Callable.From(() =>
            {
                _hovered = -1;
                QueueRedraw();
            }));
            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));
        }

        // Where the image is drawn inside this control: scaled to fit
        // above the legend strip, centered, aspect ratio kept.
        Rect2 ImageRect()
        {
            if (_texture == null)
                return new Rect2();
            var imageSize = _texture.GetSize();
            var available = new Vector2(Size.X, Mathf.Max(Size.Y - LegendHeight, 1.0f));
            var scaleFactor = Mathf.Min(available.X / imageSize.X, available.Y / imageSize.Y);
            var drawnSize = imageSize * scaleFactor;
            return new Rect2((available - drawnSize) / 2.0f, drawnSize);
        }

        int HotspotAt(Vector2 point)
        {
            if (_texture == null)
                return -1;
            var imageRect = ImageRect();
            if (!imageRect.HasPoint(point))
                return -1;
            var imagePoint = (point - imageRect.Position) / (imageRect.Size.X / _texture.GetSize().X);
            for (var i = 0; i < _hotspots.Count; i++)
            {
                if (_hotspots[i].Rect.HasPoint(imagePoint))
                    return i;
            }
            return -1;
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseMotion motionEvent)
            {
                var index = HotspotAt(motionEvent.Position);
                if (index != _hovered)
                {
                    _hovered = index;
                    QueueRedraw();
                }
            }
        }

        public override string _GetTooltip(Vector2 atPosition)
        {
            var index = HotspotAt(atPosition);
            if (index == -1)
                return "";
            var hotspot = _hotspots[index];
            return $"{hotspot.Title}\n{hotspot.Function}";
        }

        // Image-space rect to this control's pixels.
        Rect2 ToScreen(Rect2 source, Rect2 imageRect)
        {
            var scaleFactor = imageRect.Size.X / _texture.GetSize().X;
            return new Rect2(imageRect.Position + source.Position * scaleFactor, source.Size * scaleFactor);
        }

        public override void _Draw()
        {
            if (_texture == null)
                return;
            var imageRect = ImageRect();
            DrawTextureRect(_texture, imageRect, false);
            foreach (var hotspot in _hotspots)
            {
                var screenRect = ToScreen(hotspot.Rect, imageRect);
                switch (hotspot.State)
                {
                    case MidiSpotState.Assigned:
                        DrawRect(screenRect, new Color(AssignedColor, AssignedFillAlpha), true);
                        DrawRect(screenRect, AssignedColor, false, AssignedOutlineWidth);
                        break;
                    case MidiSpotState.Unavailable:
                        DrawRect(screenRect, UnavailableFill, true);
                        break;
                }
            }
            if (_hovered != -1)
            {
                var highlight = ToScreen(_hotspots[_hovered].Rect, imageRect);
                DrawRect(highlight, new Color(HighlightColor, HighlightFillAlpha), true);
                DrawRect(highlight, HighlightColor, false, OutlineWidth);
            }
            DrawLegend();
        }

        void DrawLegend()
        {
            var font = ThemeDB.FallbackFont;
            var baselineY = Size.Y - (LegendHeight - SwatchSize) / 2.0f - 2.0f;
            var swatchY = Size.Y - (LegendHeight + SwatchSize) / 2.0f;
            var x = 12.0f;
            var entries = new (Color Fill, Color Outline, string Text)[]
            {
                (new Color(AssignedColor, AssignedFillAlpha), AssignedColor, "Assigned"),
                (new Color(0, 0, 0, 0), LegendTextColor, "Free to map"),
                (UnavailableFill, LegendTextColor, "Reserved by the device"),
            };
            foreach (var entry in entries)
            {
                var swatch = new Rect2(x, swatchY, SwatchSize, SwatchSize);
                DrawRect(swatch, entry.Fill, true);
                DrawRect(swatch, entry.Outline, false, 1.5f);
                DrawString(font, new Vector2(x + SwatchSize + 6.0f, baselineY), entry.Text, HorizontalAlignment.Left, -1, LegendFontSize, LegendTextColor);
                x += SwatchSize + 6.0f + font.GetStringSize(entry.Text, HorizontalAlignment.Left, -1, LegendFontSize).X + 24.0f;
            }
        }
    }
}
#endif

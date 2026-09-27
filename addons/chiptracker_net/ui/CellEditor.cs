#if TOOLS
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/cell_editor.gd. Compact row
    // of controls for editing whichever cell is currently selected in the
    // pattern grid -- the grid itself is display/selection only, this is
    // where note/instrument/volume actually get typed in. Cell-data wiring
    // (SetCell et al.) lands with the engine port; this pass covers the
    // chrome (icons, colors, the REC/EDIT controls built in code).
    [Tool]
    public partial class CellEditor : HBoxContainer
    {
        // `before`/`after` are Cell.Snapshot() dictionaries -- the main
        // view records grid-edit undo/redo from these once that lands.
        [Signal] public delegate void CellEditedEventHandler(Dictionary before, Dictionary after);
        [Signal] public delegate void EditModeToggledEventHandler(bool enabled);
        [Signal] public delegate void RecordToggledEventHandler(bool enabled);
        [Signal] public delegate void RecordLatencyChangedEventHandler(float milliseconds);
        [Signal] public delegate void ExportRequestedEventHandler();
        [Signal] public delegate void UndoRequestedEventHandler();
        [Signal] public delegate void RedoRequestedEventHandler();

        const int EditModeDotDiameter = 10;
        static readonly Color EditModeDotOffColor = new Color(0.55f, 0.55f, 0.55f);
        static readonly Color EditModeDotOnColor = new Color(0.85f, 0.2f, 0.2f);
        static readonly Color UndoTextColor = new Color(0.85f, 0.25f, 0.25f);
        static readonly Color RedoTextColor = new Color(0.35f, 0.75f, 0.4f);

        internal SpinBox _noteSpin;
        SpinBox _instrumentSpin;
        internal SpinBox _volumeSpin;
        Button _undoButton;
        Button _redoButton;
        Button _clearButton;
        internal Button _editModeButton;
        Button _exportButton;

        Button _recordButton;
        SpinBox _latencySpin;

        Cell _cell;
        bool _updating;

        public override void _Ready()
        {
            _noteSpin = GetNode<SpinBox>("Margin/Content/NoteSpin");
            _instrumentSpin = GetNode<SpinBox>("Margin/Content/InstrumentSpin");
            _volumeSpin = GetNode<SpinBox>("Margin/Content/VolumeSpin");
            _undoButton = GetNode<Button>("Margin/Content/UndoButton");
            _redoButton = GetNode<Button>("Margin/Content/RedoButton");
            _clearButton = GetNode<Button>("Margin/Content/ClearButton");
            _editModeButton = GetNode<Button>("Margin/Content/EditModeButton");
            _exportButton = GetNode<Button>("Margin/Content/ExportButton");

            _noteSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnNoteChanged));
            _instrumentSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnInstrumentChanged));
            _volumeSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnVolumeChanged));

            foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color" })
            {
                _undoButton.AddThemeColorOverride(key, UndoTextColor);
                _redoButton.AddThemeColorOverride(key, RedoTextColor);
            }
            _undoButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnUndoButtonPressed));
            _redoButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRedoButtonPressed));
            SetUndoRedoState(false, false, "", "");

            _clearButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnClearButtonPressed));
            _editModeButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnEditModeToggled));
            _exportButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnExportButtonPressed));
            UpdateEditModeVisuals();
            BuildRecordButton();
            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

            foreach (var labelName in new[] { "NoteLabel", "InstrLabel", "VolLabel" })
            {
                var label = GetNode<Label>("Margin/Content/" + labelName);
                label.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
                label.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        // "Deep Sea" secondary-panel band, one shade lighter than the
        // transport bar's.
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), ChiptrackerPalette.TealBlue);
        }

        // Used by keyboard note entry as the volume to stamp onto a newly-entered note.
        public int GetVolume() => (int)_volumeSpin.Value;

        public void SetCell(Cell cell)
        {
            _cell = cell;
            _updating = true;
            var hasCell = cell != null;
            _noteSpin.Editable = hasCell;
            _instrumentSpin.Editable = hasCell;
            _volumeSpin.Editable = hasCell;
            _clearButton.Disabled = !hasCell;
            if (hasCell)
            {
                _noteSpin.Value = cell.Note;
                _instrumentSpin.Value = cell.InstrumentId;
                _volumeSpin.Value = cell.Volume;
            }
            _updating = false;
        }

        public void SetUndoRedoState(bool canUndo, bool canRedo, string undoTooltip, string redoTooltip)
        {
            _undoButton.Disabled = !canUndo;
            _redoButton.Disabled = !canRedo;
            _undoButton.TooltipText = canUndo ? undoTooltip : "Nothing to undo";
            _redoButton.TooltipText = canRedo ? redoTooltip : "Nothing to redo";
        }

        internal bool GetUndoButtonDisabled() => _undoButton.Disabled;
        internal bool GetRedoButtonDisabled() => _redoButton.Disabled;
        internal string GetUndoButtonTooltip() => _undoButton.TooltipText;
        internal void OnVolumeChangedNotify(double value) => OnVolumeChanged(value);

        // REC sits directly after EDIT in the same row and looks like it (a
        // dot that is grey off and red on), built in code so the scene
        // stays untouched.
        void BuildRecordButton()
        {
            _recordButton = new Button
            {
                Text = "REC",
                ToggleMode = true,
                FocusMode = FocusModeEnum.None,
                TooltipText = "Record: with playback running, notes are written at the row being played",
            };
            _recordButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnRecordToggled));
            var parent = _editModeButton.GetParent();
            parent.AddChild(_recordButton);
            parent.MoveChild(_recordButton, _editModeButton.GetIndex() + 1);
            UpdateRecordVisuals();

            _latencySpin = new SpinBox
            {
                MinValue = 0,
                MaxValue = 500,
                Step = 1,
                Prefix = "Lat ",
                Suffix = " ms",
                TooltipText = "Recording latency: how far to look back from the playhead when placing a recorded note.\nCovers audio output delay plus key delay. Raise it if notes land a row late.",
            };
            _latencySpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnRecordLatencyChanged));
            parent.AddChild(_latencySpin);
            parent.MoveChild(_latencySpin, _recordButton.GetIndex() + 1);
        }

        public void SetRecordLatency(float milliseconds) => _latencySpin.SetValueNoSignal(milliseconds);

        public void ToggleEditMode() => _editModeButton.ButtonPressed = !_editModeButton.ButtonPressed;

        public void ToggleRecord() => _recordButton.ButtonPressed = !_recordButton.ButtonPressed;

        public void SetRecord(bool enabled)
        {
            _recordButton.SetPressedNoSignal(enabled);
            UpdateRecordVisuals();
        }

        void UpdateRecordVisuals()
        {
            var on = _recordButton.ButtonPressed;
            var textColor = on ? ChiptrackerPalette.SkyBlue : ChiptrackerPalette.PaleIce;
            foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color" })
                _recordButton.AddThemeColorOverride(key, textColor);
            _recordButton.Icon = MakeDotTexture(EditModeDotDiameter, on ? EditModeDotOnColor : EditModeDotOffColor);
        }

        // The status dot lives inside the button as its icon (grey off, red
        // on) rather than as separate text -- a small procedurally-drawn
        // circle, since there's no existing icon asset for it.
        void UpdateEditModeVisuals()
        {
            var on = _editModeButton.ButtonPressed;
            var editColor = on ? ChiptrackerPalette.SkyBlue : ChiptrackerPalette.PaleIce;
            foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color" })
                _editModeButton.AddThemeColorOverride(key, editColor);
            _editModeButton.Icon = MakeDotTexture(EditModeDotDiameter, on ? EditModeDotOnColor : EditModeDotOffColor);
        }

        static ImageTexture MakeDotTexture(int diameter, Color color)
        {
            var image = Image.Create(diameter, diameter, false, Image.Format.Rgba8);
            var radius = diameter / 2.0f;
            var center = new Vector2(radius, radius);
            for (var y = 0; y < diameter; y++)
            {
                for (var x = 0; x < diameter; x++)
                {
                    var inCircle = new Vector2(x + 0.5f, y + 0.5f).DistanceTo(center) <= radius;
                    image.SetPixel(x, y, inCircle ? color : new Color(0, 0, 0, 0));
                }
            }
            return ImageTexture.CreateFromImage(image);
        }

        void OnUndoButtonPressed() => EmitSignal(SignalName.UndoRequested);
        void OnRedoButtonPressed() => EmitSignal(SignalName.RedoRequested);
        void OnExportButtonPressed() => EmitSignal(SignalName.ExportRequested);

        void OnClearButtonPressed()
        {
            if (_cell == null)
                return;
            var before = _cell.Snapshot();
            _cell.Note = -1;
            SetCell(_cell);
            EmitSignal(SignalName.CellEdited, before, _cell.Snapshot());
        }

        internal void OnNoteChanged(double value)
        {
            if (_updating || _cell == null)
                return;
            var before = _cell.Snapshot();
            _cell.Note = (int)value;
            EmitSignal(SignalName.CellEdited, before, _cell.Snapshot());
        }

        void OnInstrumentChanged(double value)
        {
            if (_updating || _cell == null)
                return;
            var before = _cell.Snapshot();
            _cell.InstrumentId = (int)value;
            EmitSignal(SignalName.CellEdited, before, _cell.Snapshot());
        }

        void OnVolumeChanged(double value)
        {
            if (_updating || _cell == null)
                return;
            var before = _cell.Snapshot();
            _cell.Volume = (int)value;
            EmitSignal(SignalName.CellEdited, before, _cell.Snapshot());
        }

        internal void OnEditModeToggled(bool enabled)
        {
            UpdateEditModeVisuals();
            EmitSignal(SignalName.EditModeToggled, enabled);
        }

        void OnRecordToggled(bool enabled)
        {
            UpdateRecordVisuals();
            EmitSignal(SignalName.RecordToggled, enabled);
        }

        void OnRecordLatencyChanged(double value) => EmitSignal(SignalName.RecordLatencyChanged, (float)value);
    }
}
#endif

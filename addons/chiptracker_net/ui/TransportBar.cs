#if TOOLS
using Godot;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/transport_bar.gd. Play/stop/
    // loop controls, tempo, rows-per-pattern, the current pattern's name,
    // and a status readout. Pure UI signal source -- ChiptrackerMainView
    // wires these to the engine once it exists.
    //
    // Every signal connection here is an OBJECT+METHOD Callable
    // (`Connect(Signal, new Callable(this, MethodName.Foo))`), never a C#
    // delegate (`button.Pressed += ...`) -- a delegate connection on a live
    // EditorPlugin control floods `delegate_handle.value == nullptr`
    // errors on a C# hot-reload. See addons/godot_mcp/Editor/UI/DockStyle.cs
    // for the same project-wide rule.
    [Tool]
    public partial class TransportBar : HBoxContainer
    {
        [Signal] public delegate void PlayPressedEventHandler();
        [Signal] public delegate void StopPressedEventHandler();
        [Signal] public delegate void LoopToggledEventHandler(bool enabled);
        [Signal] public delegate void TempoChangedEventHandler(int tempo);
        [Signal] public delegate void RowsPerPatternChangedEventHandler(int rows);
        [Signal] public delegate void TapTempoPressedEventHandler();
        [Signal] public delegate void CountInToggledEventHandler(bool enabled);

        static readonly Color CacheCleanColor = new Color(1.0f, 0.65f, 0.1f);
        static readonly Color CacheDirtyColor = new Color(1f, 1f, 1f);

        Button _playButton;
        Button _stopButton;
        Button _loopButton;
        SpinBox _tempoSpin;
        SpinBox _rowsSpin;
        Label _patternNameLabel;
        internal Label _octaveLabel;
        Label _statusLabel;
        SpinBox _stepSpin;
        CheckBox _countInCheck;

        bool _updating;
        internal bool _cacheClean;
        bool _rebuilding;

        public override void _Ready()
        {
            _playButton = GetNode<Button>("Margin/Content/PlayButton");
            _stopButton = GetNode<Button>("Margin/Content/StopButton");
            _loopButton = GetNode<Button>("Margin/Content/LoopButton");
            _tempoSpin = GetNode<SpinBox>("Margin/Content/TempoSpin");
            _rowsSpin = GetNode<SpinBox>("Margin/Content/RowsSpin");
            _patternNameLabel = GetNode<Label>("Margin/Content/PatternNameLabel");
            _octaveLabel = GetNode<Label>("Margin/Content/OctaveLabel");
            _statusLabel = GetNode<Label>("Margin/Content/StatusLabel");

            _playButton.Icon = Icon("Play");
            _stopButton.Icon = Icon("Stop");
            _loopButton.Icon = Icon("Loop");

            _playButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnPlayButtonPressed));
            _stopButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnStopButtonPressed));
            _loopButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnLoopButtonToggled));
            _tempoSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnTempoChanged));
            _rowsSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnRowsPerPatternChanged));
            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

            foreach (var labelName in new[] { "TempoLabel", "RowsLabel", "OctaveLabel", "StatusLabel" })
            {
                var label = GetNode<Label>("Margin/Content/" + labelName);
                label.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
                label.VerticalAlignment = VerticalAlignment.Center;
            }
            _patternNameLabel.VerticalAlignment = VerticalAlignment.Center;

            BuildTapTempoButton();
            BuildStepSpin();
            BuildCountInCheck();
            UpdatePlayIconColor();
        }

        // TAP TEMPO sits right after the octave readout, built in code so
        // the scene stays untouched (matches transport_bar.gd).
        void BuildTapTempoButton()
        {
            var button = new Button
            {
                Text = "TAP TEMPO",
                FocusMode = FocusModeEnum.None,
                TooltipText = "Tap along to the beat to set the tempo.\nAlso the ` key, and the Akai's TAP TEMPO button.\nStarts over after a two-second pause.",
            };
            button.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnTapTempoButtonPressed));
            var parent = _octaveLabel.GetParent();
            parent.AddChild(button);
            parent.MoveChild(button, _octaveLabel.GetIndex() + 1);
        }

        void BuildStepSpin()
        {
            _stepSpin = new SpinBox
            {
                MinValue = 0,
                MaxValue = 9999,
                Step = 1,
                Rounded = true,
                Value = 0,
                Prefix = "Step",
                TooltipText = "Rows the cursor moves down after a note is entered in Edit mode\n(computer keys or MIDI keyboard). 0 leaves it where it is.\nNot used while the song is playing.",
            };
            var parent = _octaveLabel.GetParent();
            parent.AddChild(_stepSpin);
            parent.MoveChild(_stepSpin, _octaveLabel.GetIndex() + 2);
        }

        public int GetCursorStep() => _stepSpin != null ? Mathf.Max((int)_stepSpin.Value, 0) : 0;

        // Moved up here from CellEditor's row (which was overflowing/getting
        // clipped in a narrow dock) into the space StatusLabel's expand-fill
        // leaves free on this row.
        void BuildCountInCheck()
        {
            _countInCheck = new CheckBox
            {
                Text = "Count-in",
                FocusMode = FocusModeEnum.None,
                TooltipText = "Before a recording pass, play one bar of clicks first.\nApplies when you press Record (if nothing is playing) or Play while recording. The pass then starts from the top of the pattern.",
            };
            _countInCheck.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnCountInToggled));
            var parent = _statusLabel.GetParent();
            parent.AddChild(_countInCheck);
            parent.MoveChild(_countInCheck, _statusLabel.GetIndex());
        }

        public void SetCountIn(bool enabled) => _countInCheck.SetPressedNoSignal(enabled);

        // "Deep Sea" toolbar band -- same shade used for the dock's column
        // header/button bands.
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), ChiptrackerPalette.SteelBlue);
        }

        Texture2D Icon(string name)
        {
            if (!Godot.Engine.IsEditorHint())
                return null;
            return EditorInterface.Singleton.GetBaseControl().GetThemeIcon(name, "EditorIcons");
        }

        public void SetTempo(int tempo)
        {
            _updating = true;
            _tempoSpin.Value = tempo;
            _updating = false;
        }

        public void SetRowsPerPattern(int rows)
        {
            _updating = true;
            _rowsSpin.Value = rows;
            _updating = false;
        }

        public void SetPatternName(string name) => _patternNameLabel.Text = name;

        public void SetOctave(int octave) => _octaveLabel.Text = $"Octave: {octave}";

        public void SetStatus(int orderIndex, int rowIndex, bool playing) =>
            _statusLabel.Text = $"{(playing ? "Playing" : "Stopped")} order {orderIndex}, row {rowIndex:D2}";

        public void SetCacheClean(bool clean)
        {
            _cacheClean = clean;
            UpdatePlayIconColor();
        }

        public void SetRebuilding(bool active)
        {
            _rebuilding = active;
            _playButton.Disabled = active;
            UpdatePlayIconColor();
        }

        void UpdatePlayIconColor()
        {
            var color = _rebuilding ? new Color(0.6f, 0.6f, 0.6f) : (_cacheClean ? CacheCleanColor : CacheDirtyColor);
            foreach (var key in new[] { "icon_normal_color", "icon_pressed_color", "icon_hover_color", "icon_disabled_color" })
                _playButton.AddThemeColorOverride(key, color);
        }

        void OnPlayButtonPressed() => EmitSignal(SignalName.PlayPressed);
        void OnStopButtonPressed() => EmitSignal(SignalName.StopPressed);
        void OnLoopButtonToggled(bool pressed) => EmitSignal(SignalName.LoopToggled, pressed);
        void OnTapTempoButtonPressed() => EmitSignal(SignalName.TapTempoPressed);
        void OnCountInToggled(bool enabled) => EmitSignal(SignalName.CountInToggled, enabled);

        void OnTempoChanged(double value)
        {
            if (_updating)
                return;
            EmitSignal(SignalName.TempoChanged, (int)value);
        }

        void OnRowsPerPatternChanged(double value)
        {
            if (_updating)
                return;
            EmitSignal(SignalName.RowsPerPatternChanged, (int)value);
        }
    }
}
#endif

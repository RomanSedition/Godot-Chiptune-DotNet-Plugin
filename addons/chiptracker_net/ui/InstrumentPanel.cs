#if TOOLS
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/instrument_panel.gd. Side
    // panel for editing the currently selected instrument's waveform,
    // duty cycle, a variable-length volume envelope, and a variable-length
    // pitch envelope (default 4 points each, but any count -- Synth
    // already interpolates over however many points Instrument.Envelope/
    // PitchEnvelope actually have). The pitch envelope is a signed bend in
    // semitones around 0, independent of the volume one.
    //
    // A tab bar at the top can also switch what all of the above is
    // editing: the base instrument (tab 0, "Base") or one of its layers
    // (Instrument.Layers -- additional instruments that play alongside
    // the base one). Only the base tab shows the "+ LAYER" button and the
    // green STACK preview button; a layer's tab shows its own Step
    // Offset/Fixed Note/remove-layer row instead. See _editedTarget,
    // SelectLayerTab, and Synth.GenerateLayeredBuffer.
    [Tool]
    public partial class InstrumentPanel : VBoxContainer
    {
        [Signal] public delegate void InstrumentEditedEventHandler();

        const float PreviewDuration = 0.8f;
        const int DefaultEnvelopePoints = 4;
        const int MinEnvelopePoints = 1;
        const int MinLayerStepOffset = -48;
        const int MaxLayerStepOffset = 48;
        static readonly string[] Waveforms = { "square", "triangle", "noise", "sine" };

        OptionButton _waveformOption;
        internal HSlider _dutySlider;
        Label _dutyValueLabel;
        Label _titleLabel;
        internal Label _envelopeCountLabel;
        Button _addEnvelopePointButton;
        Button _removeEnvelopePointButton;
        Label _selectedPointLabel;
        internal SpinBox _selectedPointSpin;
        internal EnvelopeGraph _envelopeGraph;
        SpinBox _previewNoteSpin;
        Button _previewButton;
        Label _previewNoteNameLabel;
        AudioStreamPlayer _previewPlayer;

        // Built in code -- see the .gd's _build_time_spin/_build_randomize_row.
        SpinBox _selectedTimeSpin;
        internal OptionButton _archetypeOption;
        internal Button _randomizeButton;
        internal RandomNumberGenerator _rng = new();

        // The pitch envelope block -- see _build_pitch_section.
        Label _pitchCountLabel;
        Button _addPitchPointButton;
        Button _removePitchPointButton;
        internal EnvelopeGraph _pitchGraph;
        Label _pitchSelectedPointLabel;
        SpinBox _pitchSelectedPointSpin;
        SpinBox _pitchSelectedTimeSpin;
        SpinBox _pitchNodeCountSpin;
        SpinBox _pitchRangeSpin;
        internal OptionButton _pitchArchetypeOption;
        internal Button _pitchRandomizeButton;

        // Layer tabs -- see _build_layer_tabs/_build_layer_controls.
        internal TabBar _layerTabs;
        Button _addLayerButton;
        Button _previewStackButton;
        HBoxContainer _layerControlsRow;
        SpinBox _stepOffsetSpin;
        CheckBox _fixedNoteCheck;
        SpinBox _fixedNoteSpin;
        Button _removeLayerButton;

        // The base instrument this panel was given -- stays the same
        // object no matter which tab is selected. Owns Layers.
        Instrument _instrument;

        // Whichever instrument the shared controls currently read and
        // write: _instrument itself on the base tab, or one of its
        // layers on a layer tab. Kept in step with the tab bar by
        // SelectLayerTab.
        internal Instrument _editedTarget;
        bool _updating;

        public override void _Ready()
        {
            _titleLabel = GetNode<Label>("Scroll/Margin/Content/TitleLabel");
            _waveformOption = GetNode<OptionButton>("Scroll/Margin/Content/WaveformOption");
            foreach (var waveform in Waveforms)
                _waveformOption.AddItem(waveform);
            _waveformOption.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnWaveformSelected));

            _dutySlider = GetNode<HSlider>("Scroll/Margin/Content/DutyRow/DutySlider");
            _dutyValueLabel = GetNode<Label>("Scroll/Margin/Content/DutyRow/DutyValueLabel");
            _dutySlider.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnDutyChanged));

            _envelopeCountLabel = GetNode<Label>("Scroll/Margin/Content/EnvelopeHeaderRow/EnvelopeCountLabel");
            _addEnvelopePointButton = GetNode<Button>("Scroll/Margin/Content/EnvelopeHeaderRow/AddEnvelopePointButton");
            _removeEnvelopePointButton = GetNode<Button>("Scroll/Margin/Content/EnvelopeHeaderRow/RemoveEnvelopePointButton");
            _addEnvelopePointButton.Icon = Icon("Add");
            _addEnvelopePointButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddEnvelopePointPressed));
            _removeEnvelopePointButton.Icon = Icon("Remove");
            _removeEnvelopePointButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemoveEnvelopePointPressed));

            _selectedPointLabel = GetNode<Label>("Scroll/Margin/Content/SelectedPointRow/SelectedPointLabel");
            _selectedPointSpin = GetNode<SpinBox>("Scroll/Margin/Content/SelectedPointRow/SelectedPointSpin");
            _selectedPointSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnSelectedPointChanged));

            _envelopeGraph = GetNode<EnvelopeGraph>("Scroll/Margin/Content/EnvelopeGraph");
            _envelopeGraph.Connect(EnvelopeGraph.SignalName.PointChanged, new Callable(this, MethodName.OnEnvelopeGraphPointChanged));
            _envelopeGraph.Connect(EnvelopeGraph.SignalName.PointSelected, new Callable(this, MethodName.OnEnvelopeGraphPointSelected));
            _envelopeGraph.Connect(EnvelopeGraph.SignalName.PointTimeChanged, new Callable(this, MethodName.OnEnvelopeGraphPointTimeChanged));

            // Fetched here (rather than after the Build* calls below) so
            // they're already available inside BuildLayerControls, which
            // adds the STACK button as _previewButton's sibling -- mirrors
            // GDScript's @onready timing, where these are resolved before
            // _ready()'s body runs at all.
            _previewNoteSpin = GetNode<SpinBox>("Scroll/Margin/Content/PreviewRow/PreviewNoteSpin");
            _previewButton = GetNode<Button>("Scroll/Margin/Content/PreviewRow/PreviewButton");
            _previewNoteNameLabel = GetNode<Label>("Scroll/Margin/Content/PreviewNoteNameLabel");
            _previewPlayer = GetNode<AudioStreamPlayer>("PreviewPlayer");

            BuildTimeSpin();
            BuildRandomizeRow();
            BuildPitchSection();
            BuildLayerTabs();
            BuildLayerControls();

            _previewButton.Icon = Icon("Play");
            _previewButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnPreviewButtonPressed));
            _previewNoteSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnPreviewNoteChanged));
            OnPreviewNoteChanged(_previewNoteSpin.Value);

            EditInstrument(null);
            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

            foreach (var path in new[] { "TitleLabel", "EnvelopeHeaderRow/EnvelopeCountLabel", "SelectedPointRow/SelectedPointLabel" })
                GetNode<Label>("Scroll/Margin/Content/" + path).AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            _dutyValueLabel.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            GetNode<Label>("Scroll/Margin/Content/PreviewRow/PreviewLabel").AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            _previewNoteNameLabel.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);

            _rng.Randomize();
        }

        Texture2D Icon(string name)
        {
            if (!Godot.Engine.IsEditorHint())
                return null;
            return EditorInterface.Singleton.GetBaseControl().GetThemeIcon(name, "EditorIcons");
        }

        // "Deep Sea" secondary panel, matching the cell editor's shade.
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), ChiptrackerPalette.TealBlue);
        }

        // Always the base instrument -- layers have no id of their own,
        // so a cell can only ever reference this one.
        public Instrument GetEditedInstrument() => _instrument;

        // Re-reads just the title, without touching envelope selection or
        // any other in-progress edit state -- called after a rename
        // committed via the dock.
        public void RefreshTitle()
        {
            if (_instrument == null)
                return;
            _titleLabel.Text = string.IsNullOrEmpty(_instrument.Name) ? $"Instrument {_instrument.Id}" : _instrument.Name;
        }

        public void EditInstrument(Instrument instrument)
        {
            _instrument = instrument;
            _titleLabel.Text = instrument != null
                ? (string.IsNullOrEmpty(instrument.Name) ? $"Instrument {instrument.Id}" : instrument.Name)
                : "No instrument selected";
            RebuildLayerTabs(0); // always land on the base tab for a newly-selected instrument
        }

        // Rebuilds the tab bar from _instrument.Layers (tab 0 "Base", then
        // "Layer 1", "Layer 2"...) and selects selectTab (or keeps
        // whichever tab was already current if -1).
        void RebuildLayerTabs(int selectTab = -1)
        {
            if (selectTab < 0)
                selectTab = _layerTabs.CurrentTab;
            if (_instrument == null)
            {
                _layerTabs.TabCount = 0;
                SelectLayerTab(0);
                return;
            }
            _layerTabs.TabCount = _instrument.Layers.Count + 1;
            _layerTabs.SetTabTitle(0, "Base");
            for (var i = 0; i < _instrument.Layers.Count; i++)
                _layerTabs.SetTabTitle(i + 1, $"Layer {i + 1}");
            _layerTabs.CurrentTab = Mathf.Clamp(selectTab, 0, _layerTabs.TabCount - 1);
            SelectLayerTab(_layerTabs.CurrentTab);
        }

        void OnLayerTabChanged(long tab) => SelectLayerTab((int)tab);

        // Points _editedTarget at the base instrument (tab 0) or the layer
        // tab-1 indexes, toggles the base-only/layer-only controls'
        // visibility, loads that layer's Step Offset/Fixed Note into
        // their boxes, and reloads the shared controls.
        void SelectLayerTab(int tab)
        {
            if (_instrument == null)
                _editedTarget = null;
            else if (tab <= 0)
                _editedTarget = _instrument;
            else
            {
                var layerIndex = tab - 1;
                _editedTarget = layerIndex < _instrument.Layers.Count ? _instrument.Layers[layerIndex] : _instrument;
            }
            var isBase = _instrument != null && _editedTarget == _instrument;
            var isLayer = _editedTarget != null && !isBase;
            _addLayerButton.Visible = isBase;
            _previewStackButton.Visible = isBase;
            _layerControlsRow.Visible = isLayer;
            _removeLayerButton.Visible = isLayer;
            if (isLayer)
            {
                _updating = true;
                _stepOffsetSpin.Value = _editedTarget.StepOffset;
                _fixedNoteCheck.ButtonPressed = _editedTarget.FixedNoteEnabled;
                _fixedNoteSpin.Value = _editedTarget.FixedNote;
                _fixedNoteSpin.Editable = _editedTarget.FixedNoteEnabled;
                _updating = false;
            }
            LoadTargetIntoUi();
        }

        // Loads _editedTarget into the shared controls (waveform, duty,
        // both envelopes and their point selectors).
        void LoadTargetIntoUi()
        {
            var target = _editedTarget;
            var hasTarget = target != null;
            _updating = true;
            _waveformOption.Disabled = !hasTarget;
            _dutySlider.Editable = hasTarget;
            _addEnvelopePointButton.Disabled = !hasTarget;
            _selectedPointSpin.Editable = hasTarget;
            _selectedTimeSpin.Editable = hasTarget;
            _archetypeOption.Disabled = !hasTarget;
            _randomizeButton.Disabled = !hasTarget;
            _envelopeGraph.Editable = hasTarget;
            _addPitchPointButton.Disabled = !hasTarget;
            _pitchSelectedPointSpin.Editable = hasTarget;
            _pitchSelectedTimeSpin.Editable = hasTarget;
            _pitchGraph.Editable = hasTarget;
            _pitchNodeCountSpin.Editable = hasTarget;
            _pitchRangeSpin.Editable = hasTarget;
            _pitchArchetypeOption.Disabled = !hasTarget;
            _pitchRandomizeButton.Disabled = !hasTarget;
            _previewNoteSpin.Editable = hasTarget;
            _previewButton.Disabled = !hasTarget;

            if (hasTarget)
            {
                var idx = System.Array.IndexOf(Waveforms, target.Waveform);
                _waveformOption.Selected = Mathf.Max(idx, 0);
                _dutySlider.Value = target.DutyCycle;
                _dutyValueLabel.Text = $"{target.DutyCycle:F2}";
                // A flat envelope backfill is audibly identical to the
                // previous empty-envelope behavior (Synth treats both as
                // constant full volume) -- it just gives the graph some
                // points to grab instead of showing nothing.
                if (target.Envelope.Count == 0)
                    target.Envelope = DefaultEnvelope();
                _envelopeGraph.SetValues(target.Envelope, target.ResolvedTimes());
                UpdateEnvelopeCountLabel();
                SyncSelectedPointUi();
                // Same flat backfill as the volume envelope above.
                if (target.PitchEnvelope.Count == 0)
                    target.PitchEnvelope = DefaultPitchEnvelope();
                SetPitchRangeAxis(target.PitchRange);
                _updating = true;
                _pitchRangeSpin.Value = target.PitchRange;
                _updating = false;
                _pitchGraph.SetValues(target.PitchEnvelope, target.ResolvedPitchTimes());
                UpdatePitchCountLabel();
                SyncSelectedPitchPointUi();
                _pitchNodeCountSpin.Value = Mathf.Clamp(target.PitchEnvelope.Count, EnvelopeArchetypes.MinNodes, EnvelopeArchetypes.MaxNodes);
            }
            else
            {
                _envelopeGraph.SetValues(DefaultEnvelope());
                _envelopeCountLabel.Text = "Envelope";
                _pitchGraph.SetValues(DefaultPitchEnvelope());
                _pitchCountLabel.Text = "Pitch Envelope";
            }
            _updating = false;
        }

        static Array<float> DefaultEnvelope()
        {
            var envelope = new Array<float>();
            for (var i = 0; i < DefaultEnvelopePoints; i++)
                envelope.Add(1.0f);
            return envelope;
        }

        static Array<float> DefaultPitchEnvelope()
        {
            var envelope = new Array<float>();
            for (var i = 0; i < DefaultEnvelopePoints; i++)
                envelope.Add(0.0f);
            return envelope;
        }

        void OnWaveformSelected(long index)
        {
            if (_updating || _editedTarget == null)
                return;
            _editedTarget.Waveform = Waveforms[index];
            EmitSignal(SignalName.InstrumentEdited);
        }

        internal void OnDutyChanged(double value)
        {
            if (_updating || _editedTarget == null)
                return;
            _editedTarget.DutyCycle = (float)value;
            _dutyValueLabel.Text = $"{value:F2}";
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnEnvelopeGraphPointSelected(long index) => SyncSelectedPointUi();

        // Dragging a graph point writes straight into the envelope array
        // and mirrors the value into the single-point SpinBox (only when
        // that's the currently-selected point).
        void OnEnvelopeGraphPointChanged(long index, float value)
        {
            if (_editedTarget == null || index < 0 || index >= _editedTarget.Envelope.Count)
                return;
            _editedTarget.Envelope[(int)index] = value;
            if (index == _envelopeGraph.SelectedIndex)
            {
                _updating = true;
                _selectedPointSpin.Value = value;
                _updating = false;
            }
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnSelectedPointChanged(double value)
        {
            if (_updating || _editedTarget == null || _editedTarget.Envelope.Count == 0)
                return;
            var index = _envelopeGraph.SelectedIndex;
            if (index < 0 || index >= _editedTarget.Envelope.Count)
                return;
            _editedTarget.Envelope[index] = (float)value;
            _envelopeGraph.SetValues(_editedTarget.Envelope, _editedTarget.ResolvedTimes());
            _envelopeGraph.SetSelectedIndex(index);
            EmitSignal(SignalName.InstrumentEdited);
        }

        // Dragging a graph point sideways moves it in time. The
        // instrument holds it between its neighbours, and the time box
        // follows when it is the selected point.
        void OnEnvelopeGraphPointTimeChanged(long index, float time)
        {
            if (_editedTarget == null || index < 0 || index >= _editedTarget.Envelope.Count)
                return;
            _editedTarget.SetPointTime((int)index, time);
            if (index == _envelopeGraph.SelectedIndex)
                SyncTimeSpin((int)index);
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnSelectedTimeChanged(double value)
        {
            if (_updating || _editedTarget == null || _editedTarget.Envelope.Count == 0)
                return;
            var index = _envelopeGraph.SelectedIndex;
            if (index < 0 || index >= _editedTarget.Envelope.Count)
                return;
            _editedTarget.SetPointTime(index, (float)value);
            _envelopeGraph.SetValues(_editedTarget.Envelope, _editedTarget.ResolvedTimes());
            _envelopeGraph.SetSelectedIndex(index);
            EmitSignal(SignalName.InstrumentEdited);
        }

        void BuildTimeSpin()
        {
            var row = _selectedPointSpin.GetParent();
            var label = new Label { Text = "at" };
            label.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            row.AddChild(label);
            _selectedTimeSpin = new SpinBox
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                TooltipText = "Where the selected point sits along the note (0 = start, 1 = end).\nIt can't pass the point before or after it.",
            };
            _selectedTimeSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnSelectedTimeChanged));
            row.AddChild(_selectedTimeSpin);
        }

        // Shows point index's time in the time box, limited to the space
        // between its neighbours so a typed value can't break the order.
        void SyncTimeSpin(int index)
        {
            var earliest = index > 0 ? _editedTarget.PointTime(index - 1) : 0.0f;
            var latest = index < _editedTarget.Envelope.Count - 1 ? _editedTarget.PointTime(index + 1) : 1.0f;
            _updating = true;
            _selectedTimeSpin.MinValue = earliest;
            _selectedTimeSpin.MaxValue = latest;
            _selectedTimeSpin.Value = _editedTarget.PointTime(index);
            _updating = false;
        }

        // The archetype drop-down and the RANDOMIZE button, in a row just
        // above the graph. No node-count box here -- RANDOMIZE
        // regenerates at the envelope's current point count; add or
        // remove points first (the header row's +/- buttons) to change
        // how many it makes.
        void BuildRandomizeRow()
        {
            var content = _selectedPointSpin.GetParent().GetParent();
            var headerRow = _envelopeCountLabel.GetParent();
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 8);
            _archetypeOption = new OptionButton
            {
                ClipText = true,
                CustomMinimumSize = new Vector2(150, 0),
                TooltipText = "An envelope shape from CommonEnvelopes.md. RANDOMIZE builds one in this style.",
            };
            for (var i = 0; i < EnvelopeArchetypes.Count(); i++)
                _archetypeOption.AddItem(EnvelopeArchetypes.ArchetypeName(i));
            row.AddChild(_archetypeOption);
            _randomizeButton = new Button
            {
                Text = "RANDOMIZE",
                TooltipText = "Replace the envelope with a new random one in the chosen style, keeping its current number of points.\nAdd or remove points first to change how many. Each click gives a slightly different result.",
            };
            _randomizeButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRandomizePressed));
            row.AddChild(_randomizeButton);
            content.AddChild(row);
            content.MoveChild(row, headerRow.GetIndex() + 1);
        }

        // Replaces the envelope with a fresh random one in the chosen
        // archetype's shape, keeping its current number of points. Never
        // touches the pitch envelope.
        internal void OnRandomizePressed()
        {
            if (_editedTarget == null)
                return;
            var index = _archetypeOption.Selected;
            var nodeCount = _editedTarget.Envelope.Count;
            var result = EnvelopeArchetypes.Generate(index, nodeCount, _rng);
            _editedTarget.Envelope = result.Levels;
            _editedTarget.EnvelopeTimes = result.Times;
            _envelopeGraph.SetValues(_editedTarget.Envelope, _editedTarget.ResolvedTimes());
            _envelopeGraph.SetSelectedIndex(0);
            UpdateEnvelopeCountLabel();
            SyncSelectedPointUi();
            EmitSignal(SignalName.InstrumentEdited);
        }

        // The Range box just above: sets the pitch graph's axis, clamps
        // any existing points down to fit a shrunk range, and (unless the
        // panel is just loading a newly-selected instrument) stores it on
        // the instrument.
        void OnPitchRangeChanged(double value)
        {
            var rangeSemitones = Mathf.Max((int)value, 0);
            SetPitchRangeAxis(rangeSemitones);
            if (_updating || _editedTarget == null)
                return;
            _editedTarget.PitchRange = rangeSemitones;
            for (var i = 0; i < _editedTarget.PitchEnvelope.Count; i++)
                _editedTarget.PitchEnvelope[i] = Mathf.Clamp(_editedTarget.PitchEnvelope[i], -(float)rangeSemitones, rangeSemitones);
            _pitchGraph.SetValues(_editedTarget.PitchEnvelope, _editedTarget.ResolvedPitchTimes());
            SyncSelectedPitchPointUi();
            EmitSignal(SignalName.InstrumentEdited);
        }

        void SetPitchRangeAxis(int rangeSemitones)
        {
            _pitchGraph.ValueMin = -(float)rangeSemitones;
            _pitchGraph.ValueMax = rangeSemitones;
            _pitchGraph.QueueRedraw();
        }

        internal void OnAddEnvelopePointPressed()
        {
            if (_editedTarget == null)
                return;
            var index = _editedTarget.AddEnvelopePoint();
            _envelopeGraph.SetValues(_editedTarget.Envelope, _editedTarget.ResolvedTimes());
            _envelopeGraph.SetSelectedIndex(index);
            UpdateEnvelopeCountLabel();
            SyncSelectedPointUi();
            EmitSignal(SignalName.InstrumentEdited);
        }

        internal void OnRemoveEnvelopePointPressed()
        {
            if (_editedTarget == null || _editedTarget.Envelope.Count <= MinEnvelopePoints)
                return;
            var index = Mathf.Clamp(_envelopeGraph.SelectedIndex, 0, _editedTarget.Envelope.Count - 1);
            _editedTarget.RemoveEnvelopePoint(index);
            _envelopeGraph.SetValues(_editedTarget.Envelope, _editedTarget.ResolvedTimes());
            _envelopeGraph.SetSelectedIndex(Mathf.Clamp(index, 0, _editedTarget.Envelope.Count - 1));
            UpdateEnvelopeCountLabel();
            SyncSelectedPointUi();
            EmitSignal(SignalName.InstrumentEdited);
        }

        void UpdateEnvelopeCountLabel()
        {
            var count = _editedTarget != null ? _editedTarget.Envelope.Count : DefaultEnvelopePoints;
            _envelopeCountLabel.Text = $"Envelope ({count} point{(count == 1 ? "" : "s")})";
        }

        void SyncSelectedPointUi()
        {
            if (_editedTarget == null || _editedTarget.Envelope.Count == 0)
                return;
            var index = _envelopeGraph.SelectedIndex;
            _selectedPointLabel.Text = $"Point {index}";
            _updating = true;
            _selectedPointSpin.Value = _editedTarget.Envelope[index];
            _updating = false;
            SyncTimeSpin(index);
            _removeEnvelopePointButton.Disabled = _editedTarget.Envelope.Count <= MinEnvelopePoints;
        }

        // Builds the pitch envelope block: a header row (name + point
        // count, Add, Remove), the pitch archetype/Nodes/Range/RANDOMIZE
        // PITCH row, a graph (a second EnvelopeGraph, with a signed axis
        // instead of 0-1), and a point-selector row, in that order right
        // after the volume envelope's graph and before the preview
        // controls.
        void BuildPitchSection()
        {
            var content = _envelopeGraph.GetParent();
            var insertAt = _envelopeGraph.GetIndex() + 1;

            var header = new HBoxContainer();
            _pitchCountLabel = new Label { Text = "Pitch Envelope" };
            _pitchCountLabel.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            header.AddChild(_pitchCountLabel);
            var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            header.AddChild(spacer);
            _addPitchPointButton = new Button { Icon = Icon("Add") };
            _addPitchPointButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddPitchPointPressed));
            header.AddChild(_addPitchPointButton);
            _removePitchPointButton = new Button { Icon = Icon("Remove") };
            _removePitchPointButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemovePitchPointPressed));
            header.AddChild(_removePitchPointButton);
            content.AddChild(header);
            content.MoveChild(header, insertAt);

            var randomizeRow = new HFlowContainer();
            randomizeRow.AddThemeConstantOverride("h_separation", 8);
            _pitchArchetypeOption = new OptionButton
            {
                ClipText = true,
                CustomMinimumSize = new Vector2(150, 0),
                TooltipText = "A generic pitch curve (see PitchArchetypes) -- independent of the volume envelope's archetype above.\nRANDOMIZE PITCH builds one in this style.",
            };
            for (var i = 0; i < PitchArchetypes.Count(); i++)
                _pitchArchetypeOption.AddItem(PitchArchetypes.ArchetypeName(i));
            randomizeRow.AddChild(_pitchArchetypeOption);
            _pitchNodeCountSpin = new SpinBox
            {
                MinValue = EnvelopeArchetypes.MinNodes,
                MaxValue = EnvelopeArchetypes.MaxNodes,
                Step = 1,
                Rounded = true,
                CustomMinimumSize = new Vector2(110, 0),
                Prefix = "Nodes",
                Value = DefaultEnvelopePoints,
                TooltipText = $"How many pitch points RANDOMIZE PITCH makes (1 to {EnvelopeArchetypes.MaxNodes}, one per frame at 60 Hz).\nAdd/Remove keep this matching the pitch envelope's actual point count.",
            };
            randomizeRow.AddChild(_pitchNodeCountSpin);
            _pitchRangeSpin = new SpinBox
            {
                MinValue = 0,
                MaxValue = 48,
                Step = 1,
                Rounded = true,
                CustomMinimumSize = new Vector2(110, 0),
                Prefix = "Range +/-",
                Value = 24,
                TooltipText = "How far the pitch envelope's graph reaches above and below 0 (semitones).\nRANDOMIZE PITCH's curve, and dragging a point by hand, are held to this too.",
            };
            _pitchRangeSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnPitchRangeChanged));
            randomizeRow.AddChild(_pitchRangeSpin);
            _pitchRandomizeButton = new Button
            {
                Text = "RANDOMIZE PITCH",
                TooltipText = "Replace the pitch envelope with a new random one in the chosen style, with this many points.\nDoesn't touch the volume envelope. Each click gives a slightly different result.",
            };
            _pitchRandomizeButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnPitchRandomizePressed));
            randomizeRow.AddChild(_pitchRandomizeButton);
            content.AddChild(randomizeRow);
            content.MoveChild(randomizeRow, insertAt + 1);
            insertAt += 1;

            _pitchGraph = new EnvelopeGraph
            {
                ValueMin = -24.0f,
                ValueMax = 24.0f,
                ValueSnap = 1.0f, // Shift-drag snaps to whole semitones, not 0.1 of the range
                ValueFormat = "+0.0",
            };
            _pitchGraph.Connect(EnvelopeGraph.SignalName.PointChanged, new Callable(this, MethodName.OnPitchGraphPointChanged));
            _pitchGraph.Connect(EnvelopeGraph.SignalName.PointSelected, new Callable(this, MethodName.OnPitchGraphPointSelected));
            _pitchGraph.Connect(EnvelopeGraph.SignalName.PointTimeChanged, new Callable(this, MethodName.OnPitchGraphPointTimeChanged));
            content.AddChild(_pitchGraph);
            content.MoveChild(_pitchGraph, insertAt + 1);

            var pointRow = new HBoxContainer();
            _pitchSelectedPointLabel = new Label { Text = "Point 0" };
            _pitchSelectedPointLabel.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            pointRow.AddChild(_pitchSelectedPointLabel);
            _pitchSelectedPointSpin = new SpinBox
            {
                MinValue = -24.0,
                MaxValue = 24.0,
                Step = 0.1,
                TooltipText = "The selected point's pitch bend, in semitones above (positive) or below (negative) the note.",
            };
            _pitchSelectedPointSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnSelectedPitchPointChanged));
            pointRow.AddChild(_pitchSelectedPointSpin);
            var atLabel = new Label { Text = "at" };
            atLabel.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            pointRow.AddChild(atLabel);
            _pitchSelectedTimeSpin = new SpinBox
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                TooltipText = "Where the selected pitch point sits along the note (0 = start, 1 = end).\nIt can't pass the point before or after it.",
            };
            _pitchSelectedTimeSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnSelectedPitchTimeChanged));
            pointRow.AddChild(_pitchSelectedTimeSpin);
            content.AddChild(pointRow);
            content.MoveChild(pointRow, insertAt + 2);
        }

        // Replaces the pitch envelope with a fresh random curve in the
        // chosen generic shape, with exactly the number of points in the
        // pitch section's own Nodes box, clamped to its own Range box.
        // Doesn't touch the volume envelope at all.
        internal void OnPitchRandomizePressed()
        {
            if (_editedTarget == null)
                return;
            var rangeSemitones = (int)_pitchRangeSpin.Value;
            _editedTarget.PitchRange = rangeSemitones;
            var result = PitchArchetypes.Generate(_pitchArchetypeOption.Selected, (int)_pitchNodeCountSpin.Value, rangeSemitones, _rng);
            _editedTarget.PitchEnvelope = result.Levels;
            _editedTarget.PitchEnvelopeTimes = result.Times;
            _pitchGraph.SetValues(_editedTarget.PitchEnvelope, _editedTarget.ResolvedPitchTimes());
            _pitchGraph.SetSelectedIndex(0);
            UpdatePitchCountLabel();
            SyncSelectedPitchPointUi();
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnPitchGraphPointSelected(long index) => SyncSelectedPitchPointUi();

        // Mirrors OnEnvelopeGraphPointChanged, for the pitch graph/array.
        void OnPitchGraphPointChanged(long index, float value)
        {
            if (_editedTarget == null || index < 0 || index >= _editedTarget.PitchEnvelope.Count)
                return;
            _editedTarget.PitchEnvelope[(int)index] = value;
            if (index == _pitchGraph.SelectedIndex)
            {
                _updating = true;
                _pitchSelectedPointSpin.Value = value;
                _updating = false;
            }
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnSelectedPitchPointChanged(double value)
        {
            if (_updating || _editedTarget == null || _editedTarget.PitchEnvelope.Count == 0)
                return;
            var index = _pitchGraph.SelectedIndex;
            if (index < 0 || index >= _editedTarget.PitchEnvelope.Count)
                return;
            _editedTarget.PitchEnvelope[index] = (float)value;
            _pitchGraph.SetValues(_editedTarget.PitchEnvelope, _editedTarget.ResolvedPitchTimes());
            _pitchGraph.SetSelectedIndex(index);
            EmitSignal(SignalName.InstrumentEdited);
        }

        // Mirrors OnEnvelopeGraphPointTimeChanged, for the pitch graph/array.
        void OnPitchGraphPointTimeChanged(long index, float time)
        {
            if (_editedTarget == null || index < 0 || index >= _editedTarget.PitchEnvelope.Count)
                return;
            _editedTarget.SetPitchPointTime((int)index, time);
            if (index == _pitchGraph.SelectedIndex)
                SyncPitchTimeSpin((int)index);
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnSelectedPitchTimeChanged(double value)
        {
            if (_updating || _editedTarget == null || _editedTarget.PitchEnvelope.Count == 0)
                return;
            var index = _pitchGraph.SelectedIndex;
            if (index < 0 || index >= _editedTarget.PitchEnvelope.Count)
                return;
            _editedTarget.SetPitchPointTime(index, (float)value);
            _pitchGraph.SetValues(_editedTarget.PitchEnvelope, _editedTarget.ResolvedPitchTimes());
            _pitchGraph.SetSelectedIndex(index);
            EmitSignal(SignalName.InstrumentEdited);
        }

        // Mirrors SyncTimeSpin, for the pitch point selector.
        void SyncPitchTimeSpin(int index)
        {
            var earliest = index > 0 ? _editedTarget.PitchPointTime(index - 1) : 0.0f;
            var latest = index < _editedTarget.PitchEnvelope.Count - 1 ? _editedTarget.PitchPointTime(index + 1) : 1.0f;
            _updating = true;
            _pitchSelectedTimeSpin.MinValue = earliest;
            _pitchSelectedTimeSpin.MaxValue = latest;
            _pitchSelectedTimeSpin.Value = _editedTarget.PitchPointTime(index);
            _updating = false;
        }

        internal void OnAddPitchPointPressed()
        {
            if (_editedTarget == null)
                return;
            var index = _editedTarget.AddPitchPoint();
            _pitchGraph.SetValues(_editedTarget.PitchEnvelope, _editedTarget.ResolvedPitchTimes());
            _pitchGraph.SetSelectedIndex(index);
            UpdatePitchCountLabel();
            SyncSelectedPitchPointUi();
            SyncPitchNodeCountSpin();
            EmitSignal(SignalName.InstrumentEdited);
        }

        internal void OnRemovePitchPointPressed()
        {
            if (_editedTarget == null || _editedTarget.PitchEnvelope.Count <= MinEnvelopePoints)
                return;
            var index = Mathf.Clamp(_pitchGraph.SelectedIndex, 0, _editedTarget.PitchEnvelope.Count - 1);
            _editedTarget.RemovePitchPoint(index);
            _pitchGraph.SetValues(_editedTarget.PitchEnvelope, _editedTarget.ResolvedPitchTimes());
            _pitchGraph.SetSelectedIndex(Mathf.Clamp(index, 0, _editedTarget.PitchEnvelope.Count - 1));
            UpdatePitchCountLabel();
            SyncSelectedPitchPointUi();
            SyncPitchNodeCountSpin();
            EmitSignal(SignalName.InstrumentEdited);
        }

        // Keeps the Nodes box (RANDOMIZE PITCH's target count) matching
        // the pitch envelope's actual point count after Add/Remove.
        void SyncPitchNodeCountSpin() =>
            _pitchNodeCountSpin.Value = Mathf.Clamp(_editedTarget.PitchEnvelope.Count, EnvelopeArchetypes.MinNodes, EnvelopeArchetypes.MaxNodes);

        void UpdatePitchCountLabel()
        {
            var count = _editedTarget != null ? _editedTarget.PitchEnvelope.Count : DefaultEnvelopePoints;
            _pitchCountLabel.Text = $"Pitch Envelope ({count} point{(count == 1 ? "" : "s")})";
        }

        // Mirrors SyncSelectedPointUi, for the pitch point selector.
        void SyncSelectedPitchPointUi()
        {
            if (_editedTarget == null || _editedTarget.PitchEnvelope.Count == 0)
                return;
            var index = _pitchGraph.SelectedIndex;
            _pitchSelectedPointLabel.Text = $"Point {index}";
            _updating = true;
            _pitchSelectedPointSpin.MinValue = -(float)_editedTarget.PitchRange;
            _pitchSelectedPointSpin.MaxValue = _editedTarget.PitchRange;
            _pitchSelectedPointSpin.Value = _editedTarget.PitchEnvelope[index];
            _updating = false;
            SyncPitchTimeSpin(index);
            _removePitchPointButton.Disabled = _editedTarget.PitchEnvelope.Count <= MinEnvelopePoints;
        }

        // Builds the tab bar itself, right below the title. Populated/
        // selected by RebuildLayerTabs, called from EditInstrument and
        // whenever a layer is added or removed.
        void BuildLayerTabs()
        {
            _layerTabs = new TabBar { TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowNever };
            _layerTabs.Connect(TabBar.SignalName.TabChanged, new Callable(this, MethodName.OnLayerTabChanged));
            var content = _titleLabel.GetParent();
            content.AddChild(_layerTabs);
            content.MoveChild(_layerTabs, _titleLabel.GetIndex() + 1);
        }

        // Builds the base-only "+ LAYER" button (bottom of the panel),
        // the base-only green STACK preview button (beside the ordinary
        // Preview button), and the layer-only row (Step Offset, Fixed
        // Note, Remove) that sits right below the tab bar. Which of these
        // is visible is toggled by SelectLayerTab.
        void BuildLayerControls()
        {
            _layerControlsRow = new HBoxContainer();
            _layerControlsRow.AddThemeConstantOverride("separation", 8);
            var offsetLabel = new Label { Text = "Step Offset" };
            offsetLabel.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            _layerControlsRow.AddChild(offsetLabel);
            _stepOffsetSpin = new SpinBox
            {
                MinValue = MinLayerStepOffset,
                MaxValue = MaxLayerStepOffset,
                Step = 1,
                Rounded = true,
                Value = 0,
                TooltipText = "Semitones above (positive) or below (negative) the triggering note this layer plays at,\nrelative to the base instrument's note. Ignored if Fixed Note is on.",
            };
            _stepOffsetSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnStepOffsetChanged));
            _layerControlsRow.AddChild(_stepOffsetSpin);
            _fixedNoteCheck = new CheckBox { Text = "Fixed Note", TooltipText = "Always play the same note, ignoring both the triggering note and Step Offset." };
            _fixedNoteCheck.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            _fixedNoteCheck.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnFixedNoteToggled));
            _layerControlsRow.AddChild(_fixedNoteCheck);
            _fixedNoteSpin = new SpinBox
            {
                MinValue = 0,
                MaxValue = 127,
                Step = 1,
                Rounded = true,
                Value = 60,
                Editable = false,
                TooltipText = "The note this layer always plays when Fixed Note is on.",
            };
            _fixedNoteSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.OnFixedNoteChanged));
            _layerControlsRow.AddChild(_fixedNoteSpin);
            var content = _titleLabel.GetParent();
            content.AddChild(_layerControlsRow);
            content.MoveChild(_layerControlsRow, _layerTabs.GetIndex() + 1);

            // + LAYER (base tab) and REMOVE LAYER (a layer's tab) both
            // sit at the very bottom of the panel, below the preview
            // controls, rather than in the layer-only row above --
            // they're never visible at the same time, so they don't need
            // to sit side by side.
            _addLayerButton = new Button
            {
                Text = "+ LAYER",
                TooltipText = "Add another instrument that plays alongside this one, with its own\nwaveform, envelopes, and a semitone offset (or a fixed note) of its own.",
            };
            _addLayerButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddLayerPressed));
            content.AddChild(_addLayerButton); // appends at the very end -- "down the bottom"

            _removeLayerButton = new Button { Text = "REMOVE LAYER", TooltipText = "Remove this layer." };
            _removeLayerButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemoveLayerPressed));
            content.AddChild(_removeLayerButton);

            _previewStackButton = new Button
            {
                Text = "STACK",
                Icon = Icon("Play"),
                // A green tint on the whole button so it reads as
                // visually distinct from the plain Preview button beside
                // it, without needing a StyleBox override that could
                // clash with the editor theme.
                Modulate = new Color(0.55f, 1.0f, 0.55f),
                TooltipText = "Preview the base instrument together with every layer -- what actually plays in a song.\nThe plain Preview button always previews only whichever tab is open, alone.",
            };
            _previewStackButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnPreviewStackPressed));
            _previewButton.GetParent().AddChild(_previewStackButton);
        }

        internal void OnAddLayerPressed()
        {
            if (_instrument == null)
                return;
            _instrument.Layers.Add(new Instrument());
            RebuildLayerTabs(_instrument.Layers.Count); // the new layer's tab: index == its position + 1
            EmitSignal(SignalName.InstrumentEdited);
        }

        internal void OnRemoveLayerPressed()
        {
            if (_instrument == null || _editedTarget == null || _editedTarget == _instrument)
                return;
            var index = _instrument.Layers.IndexOf(_editedTarget);
            if (index == -1)
                return;
            _instrument.Layers.RemoveAt(index);
            RebuildLayerTabs(0); // back to the base tab
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnStepOffsetChanged(double value)
        {
            if (_updating || _editedTarget == null || _editedTarget == _instrument)
                return;
            _editedTarget.StepOffset = (int)value;
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnFixedNoteToggled(bool enabled)
        {
            _fixedNoteSpin.Editable = enabled;
            if (_updating || _editedTarget == null || _editedTarget == _instrument)
                return;
            _editedTarget.FixedNoteEnabled = enabled;
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnFixedNoteChanged(double value)
        {
            if (_updating || _editedTarget == null || _editedTarget == _instrument)
                return;
            _editedTarget.FixedNote = (int)value;
            EmitSignal(SignalName.InstrumentEdited);
        }

        void OnPreviewNoteChanged(double value) => _previewNoteNameLabel.Text = Synth.NoteName((int)value);

        // The ordinary Preview button: previews whichever tab is
        // currently open, alone (no layers summed in) -- for tuning that
        // one oscillator by ear.
        void OnPreviewButtonPressed()
        {
            if (_editedTarget == null)
                return;
            PlayBuffer(Synth.GenerateBuffer(_editedTarget, (int)_previewNoteSpin.Value, PreviewDuration, PlaybackState.SampleRate));
        }

        // The green STACK button (base tab only): previews the base
        // instrument together with every layer, via the same path as
        // external callers use -- see PreviewNote.
        void OnPreviewStackPressed() => PreviewNote((int)_previewNoteSpin.Value);

        // Independent of song/pattern playback -- synthesizes the base
        // instrument together with every layer (identical to a plain
        // instrument when it has none) and plays it directly. This is
        // what actually plays in a song, so it's used regardless of
        // which tab is currently open: by the green STACK button, and by
        // keyboard note entry in Edit mode.
        public void PreviewNote(int note)
        {
            if (_instrument == null)
                return;
            PlayBuffer(Synth.GenerateLayeredBuffer(_instrument, note, PreviewDuration, PlaybackState.SampleRate));
        }

        void PlayBuffer(float[] samples)
        {
            var stream = new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = PlaybackState.SampleRate,
                Stereo = false,
                Data = Synth.ToPcm16(samples),
            };
            _previewPlayer.Stream = stream;
            _previewPlayer.Play();
        }
    }
}
#endif

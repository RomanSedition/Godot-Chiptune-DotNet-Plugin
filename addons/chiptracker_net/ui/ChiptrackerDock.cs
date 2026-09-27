#if TOOLS
using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/chiptracker_dock.gd. Covers
    // the "Tracks" tab's channel/instrument/pattern/order-list CRUD, the
    // Groups tab's dynamic per-group columns and channel reassignment,
    // the metronome add/remove/accent controls, and the MIDI Keyboard
    // tab's mapping list/diagram viewer, all against a real Song.
    [Tool]
    public partial class ChiptrackerDock : VBoxContainer
    {
        [Signal] public delegate void InstrumentSelectedEventHandler(Instrument instrument);
        [Signal] public delegate void AddInstrumentRequestedEventHandler();
        [Signal] public delegate void RemoveInstrumentRequestedEventHandler(int index);
        [Signal] public delegate void DuplicateInstrumentRequestedEventHandler(int index);
        [Signal] public delegate void InstrumentRenamedEventHandler(int index);
        [Signal] public delegate void ChannelsChangedEventHandler();
        [Signal] public delegate void ChannelRenamedEventHandler(int index);
        [Signal] public delegate void ChannelMutatedEventHandler();
        [Signal] public delegate void PatternsChangedEventHandler();
        [Signal] public delegate void PatternRenamedEventHandler(int index);
        [Signal] public delegate void OrderListChangedEventHandler();
        [Signal] public delegate void PatternIndexRequestedEventHandler(int patternIndex);

        // A channel moved to an adjacent group via a row's <-/-> button --
        // doesn't change channel/pattern count (unlike ChannelsChanged),
        // just which Group column it's listed under and which band the
        // grid draws it in.
        [Signal] public delegate void ChannelGroupChangedEventHandler();

        // A group itself was added or removed (channel/grid layout affected).
        [Signal] public delegate void GroupsChangedEventHandler();
        [Signal] public delegate void GroupRenamedEventHandler(int index);

        // The metronome's ticks were rewritten without the channel layout
        // changing (the accent toggle) -- the grid needs a redraw and the
        // song is dirty.
        [Signal] public delegate void MetronomeChangedEventHandler();

        // The MIDI Keyboard tab was opened -- the main view answers by
        // calling ShowMidiMappings() with the active keyboard profile.
        [Signal] public delegate void MidiViewShownEventHandler();

        const float RowButtonHeight = 40.0f;
        const float RowVerticalPadding = 2.0f;
        static readonly Color SelectionColor = new(0.3f, 0.5f, 0.8f, 0.5f);

        TabBar _viewTabBar;
        internal HBoxContainer _body;
        Control _channelColumn;
        Control _instrumentColumn;
        Control _patternColumn;
        Control _orderColumn;
        VBoxContainer _groupsView;

        VBoxContainer _channelListContainer;
        Button _addChannelButton;
        Button _removeChannelButton;

        internal VBoxContainer _instrumentListContainer;
        Button _addInstrumentButton;
        Button _removeInstrumentButton;

        internal VBoxContainer _patternListContainer;
        Button _addPatternButton;
        Button _removePatternButton;

        internal VBoxContainer _orderListContainer;
        Button _moveOrderUpButton;
        Button _moveOrderDownButton;
        Button _removeOrderButton;

        internal ScrollContainer _groupsScroll;
        internal HBoxContainer _groupColumnsContainer;
        Button _addGroupButton;
        internal Button _removeGroupButton;
        internal Button _addMetronomeButton;
        internal Button _removeMetronomeButton;
        internal CheckBox _metronomeAccentCheck;

        PanelContainer _midiView;
        internal GridContainer _midiGrid;
        MidiKeyboard _midiProfile;
        internal Button _diagramButton;
        Window _diagramWindow;
        MidiDiagramView _diagramView;

        public Song Song;
        internal int _selectedChannelIndex = -1;
        int _selectedInstrumentIndex = -1;
        internal int _selectedOrderIndex = -1;
        internal int _selectedGroupIndex = -1;

        public override void _Ready()
        {
            _viewTabBar = GetNode<TabBar>("TabBarRow/ViewTabBar");
            _body = GetNode<HBoxContainer>("Body");
            _channelColumn = GetNode<Control>("Body/ChannelColumn");
            _instrumentColumn = GetNode<Control>("Body/InstrumentColumn");
            _patternColumn = GetNode<Control>("Body/PatternColumn");
            _orderColumn = GetNode<Control>("Body/OrderColumn");
            _groupsView = GetNode<VBoxContainer>("Body/GroupsView");

            _channelListContainer = GetNode<VBoxContainer>("Body/ChannelColumn/ChannelScroll/ChannelListContainer");
            _addChannelButton = GetNode<Button>("Body/ChannelColumn/ButtonsMargin/ChannelButtons/AddChannelButton");
            _removeChannelButton = GetNode<Button>("Body/ChannelColumn/ButtonsMargin/ChannelButtons/RemoveChannelButton");

            _instrumentListContainer = GetNode<VBoxContainer>("Body/InstrumentColumn/InstrumentScroll/InstrumentListContainer");
            _addInstrumentButton = GetNode<Button>("Body/InstrumentColumn/ButtonsMargin/InstrumentButtons/AddInstrumentButton");
            _removeInstrumentButton = GetNode<Button>("Body/InstrumentColumn/ButtonsMargin/InstrumentButtons/RemoveInstrumentButton");

            _patternListContainer = GetNode<VBoxContainer>("Body/PatternColumn/PatternScroll/PatternListContainer");
            _addPatternButton = GetNode<Button>("Body/PatternColumn/ButtonsMargin/PatternButtons/AddPatternButton");
            _removePatternButton = GetNode<Button>("Body/PatternColumn/ButtonsMargin/PatternButtons/RemovePatternButton");

            _orderListContainer = GetNode<VBoxContainer>("Body/OrderColumn/OrderScroll/OrderListContainer");
            _moveOrderUpButton = GetNode<Button>("Body/OrderColumn/ButtonsMargin/OrderButtons/MoveOrderUpButton");
            _moveOrderDownButton = GetNode<Button>("Body/OrderColumn/ButtonsMargin/OrderButtons/MoveOrderDownButton");
            _removeOrderButton = GetNode<Button>("Body/OrderColumn/ButtonsMargin/OrderButtons/RemoveOrderButton");

            _groupsScroll = GetNode<ScrollContainer>("Body/GroupsView/GroupsScroll");
            _groupColumnsContainer = GetNode<HBoxContainer>("Body/GroupsView/GroupsScroll/GroupColumnsContainer");
            _addGroupButton = GetNode<Button>("Body/GroupsView/ButtonsMargin/GroupButtons/AddGroupButton");
            _removeGroupButton = GetNode<Button>("Body/GroupsView/ButtonsMargin/GroupButtons/RemoveGroupButton");

            _viewTabBar.AddTab("Tracks");
            _viewTabBar.AddTab("Groups");
            _viewTabBar.AddTab("MIDI Keyboard");
            _viewTabBar.Connect(TabBar.SignalName.TabChanged, new Callable(this, MethodName.OnViewTabChanged));

            BuildMidiView();

            _addChannelButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddChannelPressed));
            _removeChannelButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemoveChannelPressed));
            _addInstrumentButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddInstrumentButtonPressed));
            _removeInstrumentButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemoveInstrumentPressed));
            _addPatternButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddPatternPressed));
            _removePatternButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemovePatternPressed));
            _moveOrderUpButton.Icon = Icon("MoveUp");
            _moveOrderUpButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnMoveOrderUpPressed));
            _moveOrderDownButton.Icon = Icon("MoveDown");
            _moveOrderDownButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnMoveOrderDownPressed));
            _removeOrderButton.Icon = Icon("Remove");
            _removeOrderButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemoveOrderPressed));

            _addGroupButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddGroupPressed));
            _removeGroupButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemoveGroupPressed));
            BuildMetronomeControls();
            // Group columns are sized off the scroll viewport's width (see
            // RebuildGroupColumns()), not just built once, so they keep
            // matching "one quarter of the panel" as the dock is resized
            // while Groups is the active tab.
            ConnectResized(_groupsScroll, () =>
            {
                if (_groupsView.Visible)
                    RebuildGroupColumns();
            });

            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

            foreach (var column in Columns())
            {
                var header = (Label)column.GetChild(0);
                header.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
                var headerStyle = new StyleBoxEmpty();
                headerStyle.ContentMarginLeft = 8;
                headerStyle.ContentMarginTop = 5;
                headerStyle.ContentMarginBottom = 5;
                header.AddThemeStyleboxOverride("normal", headerStyle);
                column.Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

                var buttons = (HBoxContainer)column.GetChild(column.GetChildCount() - 1).GetChild(0);
                buttons.AddThemeConstantOverride("separation", 8);
                foreach (var child in buttons.GetChildren())
                {
                    if (child is Button button)
                        button.CustomMinimumSize = new Vector2(button.CustomMinimumSize.X, RowButtonHeight);
                }
            }

            OnViewTabChanged(_viewTabBar.CurrentTab);
        }

        internal List<Control> Columns() => new() { _channelColumn, _instrumentColumn, _patternColumn, _orderColumn };

        internal void OnViewTabChanged(long tab)
        {
            var showingGroups = tab == 1;
            var showingMidi = tab == 2;
            foreach (var column in Columns())
                column.Visible = !(showingGroups || showingMidi);
            _groupsView.Visible = showingGroups;
            _midiView.Visible = showingMidi;
            if (showingGroups)
                RebuildGroupColumns();
            if (showingMidi)
                EmitSignal(SignalName.MidiViewShown);
            QueueRedraw();
        }

        // Tab 2 (MIDI Keyboard): a two-column list, function on the left
        // and the input that triggers it on the right, filled in by
        // ShowMidiMappings().
        void BuildMidiView()
        {
            _midiView = new PanelContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                Visible = false,
            };
            var panelStyle = new StyleBoxFlat
            {
                BgColor = ChiptrackerPalette.TealBlue,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 8,
                ContentMarginBottom = 8,
            };
            _midiView.AddThemeStyleboxOverride("panel", panelStyle);
            var layout = new VBoxContainer();
            _midiView.AddChild(layout);
            var headerRow = new HBoxContainer();
            var headerSpacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            headerRow.AddChild(headerSpacer);
            _diagramButton = new Button { Text = "Show Diagram" };
            _diagramButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OpenDiagram));
            headerRow.AddChild(_diagramButton);
            layout.AddChild(headerRow);
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            layout.AddChild(scroll);
            _midiGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _midiGrid.AddThemeConstantOverride("h_separation", 24);
            _midiGrid.AddThemeConstantOverride("v_separation", 6);
            scroll.AddChild(_midiGrid);
            _body.AddChild(_midiView);
        }

        // Lists every mapping the profile makes, or a hint when no profile is set.
        public void ShowMidiMappings(MidiKeyboard profile)
        {
            _midiProfile = profile;
            _diagramButton.Disabled = profile == null || string.IsNullOrEmpty(profile.GetDiagramPath());
            foreach (var child in _midiGrid.GetChildren())
                child.QueueFree();
            if (profile == null)
            {
                AddMidiRow("No keyboard profile", "Add a ChiptrackerMidiNode under the song node and assign a profile", true);
                return;
            }
            AddMidiRow("Function", $"Input ({profile.DeviceName})", true);
            foreach (var mapping in profile.GetMappings())
                AddMidiRow(mapping.Function, mapping.Input, false);
        }

        // Opens (creating on first use) a popup window showing the
        // profile's diagram; hovering a control outlines it and shows
        // what it's assigned to.
        void OpenDiagram()
        {
            if (_midiProfile == null)
                return;
            var texture = GD.Load<Texture2D>(_midiProfile.GetDiagramPath());
            if (texture == null)
            {
                GD.PushWarning($"Chiptracker: couldn't load the keyboard diagram at '{_midiProfile.GetDiagramPath()}'.");
                return;
            }
            if (_diagramWindow == null)
            {
                _diagramWindow = new Window
                {
                    Transient = true,
                    MinSize = new Vector2I(600, 240),
                };
                _diagramWindow.Connect(Window.SignalName.CloseRequested, new Callable(_diagramWindow, Window.MethodName.Hide));
                _diagramView = new MidiDiagramView();
                _diagramView.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                _diagramWindow.AddChild(_diagramView);
                AddChild(_diagramWindow);
            }
            _diagramWindow.Title = $"{_midiProfile.DeviceName}: Diagram";
            _diagramView.Setup(texture, _midiProfile.GetDiagramHotspots());
            _diagramWindow.PopupCentered(new Vector2I(1300, 470));
        }

        void AddMidiRow(string functionText, string inputText, bool isHeader)
        {
            foreach (var text in new[] { functionText, inputText })
            {
                var label = new Label
                {
                    Text = text,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                label.AddThemeColorOverride("font_color", isHeader ? ChiptrackerPalette.SkyBlue : ChiptrackerPalette.PaleIce);
                _midiGrid.AddChild(label);
            }
        }

        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), ChiptrackerPalette.DeepNavy);
            foreach (var column in Columns())
            {
                if (!column.Visible || column.GetChildCount() == 0)
                    continue;
                var columnRect = new Rect2(_body.Position + column.Position, column.Size);
                DrawRect(columnRect, ChiptrackerPalette.TealBlue);
                var header = (Control)column.GetChild(0);
                var headerRect = new Rect2(columnRect.Position + header.Position, new Vector2(column.Size.X, header.Size.Y));
                DrawRect(headerRect, ChiptrackerPalette.SteelBlue);
            }
        }

        Texture2D Icon(string name)
        {
            if (!Godot.Engine.IsEditorHint())
                return null;
            return EditorInterface.Singleton.GetBaseControl().GetThemeIcon(name, "EditorIcons");
        }

        // Godot's C# Callable has no GDScript-style Bind(args) to close a
        // per-row signal connection over a loop index -- these route the
        // closure through a small DockRowHandler instance instead (kept
        // alive via SetMeta on the control it's attached to), so the
        // actual Connect() call stays a plain object+method Callable. See
        // DockRowHandler's own comment for why that matters.
        static void ConnectPressed(Button button, System.Action action)
        {
            var handler = new DockRowHandler { Clicked = action };
            button.SetMeta("_pressed_handler", handler);
            button.Connect(BaseButton.SignalName.Pressed, new Callable(handler, DockRowHandler.MethodName.OnPressedNotify));
        }

        static void ConnectToggled(Button button, System.Action<bool> action)
        {
            var handler = new DockRowHandler { Toggled = action };
            button.SetMeta("_toggled_handler", handler);
            button.Connect(BaseButton.SignalName.Toggled, new Callable(handler, DockRowHandler.MethodName.OnToggledNotify));
        }

        static void ConnectGuiInput(Control control, System.Action<InputEvent> action)
        {
            var handler = new DockRowHandler { GuiInputReceived = action };
            control.SetMeta("_gui_input_handler", handler);
            control.Connect(Control.SignalName.GuiInput, new Callable(handler, DockRowHandler.MethodName.OnGuiInputNotify));
        }

        static void ConnectTextSubmitted(LineEdit lineEdit, System.Action<string> action)
        {
            var handler = new DockRowHandler { TextSubmitted = action };
            lineEdit.SetMeta("_text_submitted_handler", handler);
            lineEdit.Connect(LineEdit.SignalName.TextSubmitted, new Callable(handler, DockRowHandler.MethodName.OnTextSubmittedNotify));
        }

        static void ConnectFocusExited(Control control, System.Action action)
        {
            var handler = new DockRowHandler { FocusExited = action };
            control.SetMeta("_focus_exited_handler", handler);
            control.Connect(Control.SignalName.FocusExited, new Callable(handler, DockRowHandler.MethodName.OnFocusExitedNotify));
        }

        static void ConnectResized(Control control, System.Action action)
        {
            var handler = new DockRowHandler { Clicked = action };
            control.SetMeta("_resized_handler", handler);
            control.Connect(Control.SignalName.Resized, new Callable(handler, DockRowHandler.MethodName.OnPressedNotify));
        }

        void ApplySelectionStyle(Button button)
        {
            button.ClipText = true;
            var empty = new StyleBoxEmpty { ContentMarginLeft = 8, ContentMarginTop = RowVerticalPadding, ContentMarginBottom = RowVerticalPadding };
            button.AddThemeStyleboxOverride("normal", empty);
            button.AddThemeStyleboxOverride("hover", empty);
            button.AddThemeStyleboxOverride("focus", empty);
            var highlight = new StyleBoxFlat { BgColor = SelectionColor };
            highlight.SetCornerRadiusAll(3);
            highlight.ContentMarginLeft = 8;
            highlight.ContentMarginRight = 8;
            highlight.ContentMarginTop = RowVerticalPadding;
            highlight.ContentMarginBottom = RowVerticalPadding;
            button.AddThemeStyleboxOverride("pressed", highlight);
            button.AddThemeStyleboxOverride("hover_pressed", (StyleBox)highlight.Duplicate());
        }

        void ApplyCompactStyle(Button button)
        {
            foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
            {
                var baseStyle = button.GetThemeStylebox(state);
                if (baseStyle == null)
                    continue;
                var compact = (StyleBox)baseStyle.Duplicate();
                compact.ContentMarginTop = RowVerticalPadding;
                compact.ContentMarginBottom = RowVerticalPadding;
                button.AddThemeStyleboxOverride(state, compact);
            }
        }

        public void SetSong(Song song)
        {
            Song = song;
            _selectedChannelIndex = -1;
            _selectedInstrumentIndex = -1;
            _selectedOrderIndex = -1;
            Refresh();
        }

        public void Refresh()
        {
            RebuildChannelList();
            RebuildInstrumentList();
            RebuildPatternList();
            RebuildOrderList();
            RebuildGroupColumns();
            QueueRedraw();
        }

        static string PatternDisplayName(Pattern pattern, int index) =>
            string.IsNullOrEmpty(pattern.Name) ? $"Pattern {index}" : pattern.Name;

        static string ChannelDisplayName(Channel channel, int index) =>
            string.IsNullOrEmpty(channel.Name) ? $"Ch {index}" : channel.Name;

        static string InstrumentDisplayName(Instrument instrument) =>
            string.IsNullOrEmpty(instrument.Name) ? $"Instrument {instrument.Id}" : instrument.Name;

        // ---------- Channels ----------

        void RebuildChannelList()
        {
            foreach (var child in _channelListContainer.GetChildren())
                child.QueueFree();
            if (Song == null)
                return;
            var group = new ButtonGroup();
            for (var i = 0; i < Song.Channels.Count; i++)
            {
                var channel = Song.Channels[i];
                var row = new HBoxContainer();

                var selectButton = new Button
                {
                    Text = $"{i}: {ChannelDisplayName(channel, i)}",
                    ToggleMode = true,
                    ButtonGroup = group,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left,
                    TooltipText = "Click to select, double-click to rename",
                };
                ApplySelectionStyle(selectButton);
                ConnectPressed(selectButton, () => OnChannelRowPressed(i));
                ConnectGuiInput(selectButton, e => OnChannelRowGuiInput(e, i, row, selectButton));
                row.AddChild(selectButton);

                var muteButton = new Button
                {
                    Icon = Icon("AudioBusMute"),
                    TooltipText = "Mute this channel",
                    ToggleMode = true,
                    ButtonPressed = channel.Muted,
                };
                ApplyCompactStyle(muteButton);
                ConnectToggled(muteButton, pressed => OnChannelMuteToggled(pressed, i));
                row.AddChild(muteButton);

                var soloButton = new Button
                {
                    Icon = Icon("AudioBusSolo"),
                    TooltipText = "Solo this channel (mute every other channel)",
                    ToggleMode = true,
                    ButtonPressed = channel.Solo,
                };
                ApplyCompactStyle(soloButton);
                ConnectToggled(soloButton, pressed => OnChannelSoloToggled(pressed, i));
                row.AddChild(soloButton);

                _channelListContainer.AddChild(row);
            }
        }

        void OnChannelRowPressed(int index) => _selectedChannelIndex = index;

        void OnChannelMuteToggled(bool pressed, int index)
        {
            Song.Channels[index].Muted = pressed;
            EmitSignal(SignalName.ChannelMutated);
        }

        void OnChannelSoloToggled(bool pressed, int index)
        {
            Song.Channels[index].Solo = pressed;
            EmitSignal(SignalName.ChannelMutated);
        }

        void OnChannelRowGuiInput(InputEvent @event, int index, HBoxContainer row, Button selectButton)
        {
            if (@event is InputEventMouseButton { Pressed: true, DoubleClick: true })
                BeginChannelRename(index, row, selectButton);
        }

        void BeginChannelRename(int index, HBoxContainer row, Button selectButton)
        {
            selectButton.Hide();
            var channel = Song.Channels[index];
            var prefix = new Label { Text = $"{index}: " };
            var lineEdit = new LineEdit
            {
                Text = channel.Name,
                PlaceholderText = $"Ch {index}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            ConnectTextSubmitted(lineEdit, _ => CommitChannelRename(index, prefix, lineEdit, selectButton));
            ConnectFocusExited(lineEdit, () => CommitChannelRename(index, prefix, lineEdit, selectButton));
            row.AddChild(prefix);
            row.AddChild(lineEdit);
            row.MoveChild(prefix, 0);
            row.MoveChild(lineEdit, 1);
            lineEdit.GrabFocus();
            lineEdit.SelectAll();
        }

        public void CommitChannelRename(int index, Label prefix, LineEdit lineEdit, Button selectButton)
        {
            if (!GodotObject.IsInstanceValid(lineEdit) || lineEdit.IsQueuedForDeletion())
                return;
            if (Song == null || index < 0 || index >= Song.Channels.Count)
                return;
            var newName = lineEdit.Text.Trim();
            if (RenameAllowed(Song.IsMetronomeChannel(index), newName))
                Song.Channels[index].Name = newName;
            prefix.QueueFree();
            lineEdit.QueueFree();
            if (GodotObject.IsInstanceValid(selectButton))
                selectButton.Show();
            Refresh();
            EmitSignal(SignalName.ChannelRenamed, index);
        }

        internal void OnAddChannelPressed()
        {
            if (Song == null)
                return;
            var channel = new Channel { InstrumentType = "square" };
            Song.AddChannel(channel);
            Refresh();
            EmitSignal(SignalName.ChannelsChanged);
        }

        internal void OnRemoveChannelPressed()
        {
            if (Song == null || Song.Channels.Count <= 1)
                return;
            var index = _selectedChannelIndex >= 0 && _selectedChannelIndex < Song.Channels.Count
                ? _selectedChannelIndex
                : LastIndexNotMetronome(Song.Channels.Count, i => Song.IsMetronomeChannel(i));
            if (index < 0 || Song.IsMetronomeChannel(index))
                return;
            Song.RemoveChannel(index);
            _selectedChannelIndex = -1;
            Refresh();
            EmitSignal(SignalName.ChannelsChanged);
        }

        // ---------- Instruments ----------

        void RebuildInstrumentList()
        {
            foreach (var child in _instrumentListContainer.GetChildren())
                child.QueueFree();
            if (Song == null)
                return;
            var group = new ButtonGroup();
            for (var i = 0; i < Song.Instruments.Count; i++)
            {
                var instrument = Song.Instruments[i];
                var row = new HBoxContainer();

                var selectButton = new Button
                {
                    Text = $"{instrument.Id}: {InstrumentDisplayName(instrument)}",
                    ToggleMode = true,
                    ButtonGroup = group,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left,
                    TooltipText = "Click to edit, double-click to rename",
                };
                ApplySelectionStyle(selectButton);
                ConnectPressed(selectButton, () => OnInstrumentRowPressed(i, instrument));
                ConnectGuiInput(selectButton, e => OnInstrumentRowGuiInput(e, i, row, selectButton));
                row.AddChild(selectButton);

                var duplicateButton = new Button
                {
                    Icon = Icon("Duplicate"),
                    TooltipText = "Duplicate this instrument, including its layers",
                };
                ApplyCompactStyle(duplicateButton);
                ConnectPressed(duplicateButton, () => OnDuplicateInstrumentPressed(i));
                row.AddChild(duplicateButton);

                _instrumentListContainer.AddChild(row);
            }
        }

        void OnDuplicateInstrumentPressed(int index) => EmitSignal(SignalName.DuplicateInstrumentRequested, index);

        void OnInstrumentRowPressed(int index, Instrument instrument)
        {
            _selectedInstrumentIndex = index;
            EmitSignal(SignalName.InstrumentSelected, instrument);
        }

        void OnInstrumentRowGuiInput(InputEvent @event, int index, HBoxContainer row, Button selectButton)
        {
            if (@event is InputEventMouseButton { Pressed: true, DoubleClick: true })
                BeginInstrumentRename(index, row, selectButton);
        }

        void BeginInstrumentRename(int index, HBoxContainer row, Button selectButton)
        {
            selectButton.Hide();
            var instrument = Song.Instruments[index];
            var prefix = new Label { Text = $"{instrument.Id}: " };
            var lineEdit = new LineEdit
            {
                Text = instrument.Name,
                PlaceholderText = $"Instrument {instrument.Id}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            ConnectTextSubmitted(lineEdit, _ => CommitInstrumentRename(index, prefix, lineEdit, selectButton));
            ConnectFocusExited(lineEdit, () => CommitInstrumentRename(index, prefix, lineEdit, selectButton));
            row.AddChild(prefix);
            row.AddChild(lineEdit);
            row.MoveChild(prefix, 0);
            row.MoveChild(lineEdit, 1);
            lineEdit.GrabFocus();
            lineEdit.SelectAll();
        }

        public void CommitInstrumentRename(int index, Label prefix, LineEdit lineEdit, Button selectButton)
        {
            if (!GodotObject.IsInstanceValid(lineEdit) || lineEdit.IsQueuedForDeletion())
                return;
            if (Song == null || index < 0 || index >= Song.Instruments.Count)
                return;
            var newName = lineEdit.Text.Trim();
            if (RenameAllowed(Song.IsMetronomeInstrument(Song.Instruments[index]), newName))
                Song.Instruments[index].Name = newName;
            prefix.QueueFree();
            lineEdit.QueueFree();
            if (GodotObject.IsInstanceValid(selectButton))
                selectButton.Show();
            Refresh();
            EmitSignal(SignalName.InstrumentRenamed, index);
        }

        void OnAddInstrumentButtonPressed() => EmitSignal(SignalName.AddInstrumentRequested);

        internal void OnRemoveInstrumentPressed()
        {
            if (Song == null || Song.Instruments.Count == 0)
                return;
            var index = _selectedInstrumentIndex >= 0 && _selectedInstrumentIndex < Song.Instruments.Count
                ? _selectedInstrumentIndex
                : LastIndexNotMetronome(Song.Instruments.Count, i => Song.IsMetronomeInstrument(Song.Instruments[i]));
            _selectedInstrumentIndex = -1;
            if (index < 0 || Song.IsMetronomeInstrument(Song.Instruments[index]))
                return;
            EmitSignal(SignalName.RemoveInstrumentRequested, index);
        }

        // ---------- Patterns ----------

        void RebuildPatternList()
        {
            foreach (var child in _patternListContainer.GetChildren())
                child.QueueFree();
            if (Song == null)
                return;
            var group = new ButtonGroup();
            for (var i = 0; i < Song.Patterns.Count; i++)
            {
                var pattern = Song.Patterns[i];
                var row = new HBoxContainer();

                var nameButton = new Button
                {
                    Text = PatternDisplayName(pattern, i),
                    ToggleMode = true,
                    ButtonGroup = group,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left,
                    TooltipText = "Click to view, double-click to rename",
                };
                ApplySelectionStyle(nameButton);
                ConnectPressed(nameButton, () => OnPatternNamePressed(i));
                ConnectGuiInput(nameButton, e => OnPatternNameGuiInput(e, i, row, nameButton));
                row.AddChild(nameButton);

                var addToOrderButton = new Button { Icon = Icon("Add"), TooltipText = "Add to order list" };
                ApplyCompactStyle(addToOrderButton);
                ConnectPressed(addToOrderButton, () => OnAddPatternToOrder(i));
                row.AddChild(addToOrderButton);

                var rightSpacer = new Control { CustomMinimumSize = new Vector2(8, 0) };
                row.AddChild(rightSpacer);

                _patternListContainer.AddChild(row);
            }
        }

        void OnPatternNamePressed(int index) => EmitSignal(SignalName.PatternIndexRequested, index);

        void OnPatternNameGuiInput(InputEvent @event, int index, HBoxContainer row, Button nameButton)
        {
            if (@event is InputEventMouseButton { Pressed: true, DoubleClick: true })
                BeginPatternRename(index, row, nameButton);
        }

        void BeginPatternRename(int index, HBoxContainer row, Button nameButton)
        {
            nameButton.Hide();
            var lineEdit = new LineEdit
            {
                Text = Song.Patterns[index].Name,
                PlaceholderText = $"Pattern {index}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            ConnectTextSubmitted(lineEdit, _ => CommitPatternRename(index, lineEdit, nameButton));
            ConnectFocusExited(lineEdit, () => CommitPatternRename(index, lineEdit, nameButton));
            row.AddChild(lineEdit);
            row.MoveChild(lineEdit, 0);
            lineEdit.GrabFocus();
            lineEdit.SelectAll();
        }

        public void CommitPatternRename(int index, LineEdit lineEdit, Button nameButton)
        {
            if (!GodotObject.IsInstanceValid(lineEdit) || lineEdit.IsQueuedForDeletion())
                return;
            if (Song == null || index < 0 || index >= Song.Patterns.Count)
                return;
            Song.Patterns[index].Name = lineEdit.Text.Trim();
            lineEdit.QueueFree();
            if (GodotObject.IsInstanceValid(nameButton))
                nameButton.Show();
            Refresh();
            EmitSignal(SignalName.PatternRenamed, index);
        }

        internal void OnAddPatternPressed()
        {
            if (Song == null)
                return;
            Song.AddPattern();
            Refresh();
            EmitSignal(SignalName.PatternsChanged);
        }

        void OnRemovePatternPressed()
        {
            if (Song == null || Song.Patterns.Count <= 1)
                return;
            Song.RemovePattern(Song.Patterns.Count - 1);
            Refresh();
            EmitSignal(SignalName.PatternsChanged);
        }

        internal void OnAddPatternToOrder(int patternIndex)
        {
            if (Song == null)
                return;
            Song.OrderList.Add(patternIndex);
            Refresh();
            EmitSignal(SignalName.OrderListChanged);
        }

        // ---------- Order list ----------

        void RebuildOrderList()
        {
            foreach (var child in _orderListContainer.GetChildren())
                child.QueueFree();
            if (Song == null)
                return;
            var group = new ButtonGroup();
            for (var i = 0; i < Song.OrderList.Count; i++)
            {
                var patternIndex = Song.OrderList[i];
                var patternName = "?";
                if (patternIndex >= 0 && patternIndex < Song.Patterns.Count)
                    patternName = PatternDisplayName(Song.Patterns[patternIndex], patternIndex);

                var selectButton = new Button
                {
                    Text = $"{i}: {patternName}",
                    ToggleMode = true,
                    ButtonGroup = group,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = HorizontalAlignment.Left,
                };
                ApplySelectionStyle(selectButton);
                ConnectPressed(selectButton, () => OnOrderRowPressed(i, patternIndex));
                _orderListContainer.AddChild(selectButton);
            }
        }

        void OnOrderRowPressed(int index, int patternIndex)
        {
            _selectedOrderIndex = index;
            EmitSignal(SignalName.PatternIndexRequested, patternIndex);
        }

        internal void OnMoveOrderUpPressed()
        {
            if (Song == null || _selectedOrderIndex <= 0 || _selectedOrderIndex >= Song.OrderList.Count)
                return;
            var index = _selectedOrderIndex;
            (Song.OrderList[index], Song.OrderList[index - 1]) = (Song.OrderList[index - 1], Song.OrderList[index]);
            _selectedOrderIndex = index - 1;
            Refresh();
            EmitSignal(SignalName.OrderListChanged);
        }

        internal void OnMoveOrderDownPressed()
        {
            if (Song == null || _selectedOrderIndex < 0 || _selectedOrderIndex >= Song.OrderList.Count - 1)
                return;
            var index = _selectedOrderIndex;
            (Song.OrderList[index], Song.OrderList[index + 1]) = (Song.OrderList[index + 1], Song.OrderList[index]);
            _selectedOrderIndex = index + 1;
            Refresh();
            EmitSignal(SignalName.OrderListChanged);
        }

        internal void OnRemoveOrderPressed()
        {
            if (Song == null || _selectedOrderIndex < 0 || _selectedOrderIndex >= Song.OrderList.Count)
                return;
            Song.OrderList.RemoveAt(_selectedOrderIndex);
            _selectedOrderIndex = -1;
            Refresh();
            EmitSignal(SignalName.OrderListChanged);
        }

        // ---------- Groups ----------

        static string GroupDisplayName(ChannelGroup group, int index) =>
            string.IsNullOrEmpty(group.Name) ? $"Group {index + 1}" : group.Name;

        // Builds one PanelContainer column per Song.Groups entry, each
        // listing the channels currently assigned to it
        // (Channel.GroupIndex) in their original Song.Channels order.
        // One quarter of the scroll viewport's own width (floored, for a
        // very narrow dock) -- so up to 4 groups fill the panel exactly
        // the way the four Channels-tab columns do, and a 5th+ group
        // extends past the visible width instead of squeezing every
        // column thinner and thinner, which is what makes GroupsScroll's
        // horizontal scrollbar kick in.
        internal const float MinGroupColumnWidth = 220.0f;

        void RebuildGroupColumns()
        {
            foreach (var child in _groupColumnsContainer.GetChildren())
                child.QueueFree();
            if (Song == null)
                return;
            var columnWidth = MinGroupColumnWidth;
            if (_groupsScroll.Size.X > 0.0f)
                columnWidth = Mathf.Max(MinGroupColumnWidth, _groupsScroll.Size.X / 4.0f);
            var headerGroup = new ButtonGroup();
            for (var g = 0; g < Song.Groups.Count; g++)
                _groupColumnsContainer.AddChild(MakeGroupColumn(g, headerGroup, columnWidth));
            _removeGroupButton.Disabled = Song.Groups.Count <= 1;
            UpdateMetronomeControls();
        }

        Control MakeGroupColumn(int groupIndex, ButtonGroup headerGroup, float columnWidth)
        {
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(columnWidth, 0) };
            var panelStyle = new StyleBoxFlat { BgColor = ChiptrackerPalette.TealBlue };
            panel.AddThemeStyleboxOverride("panel", panelStyle);

            var content = new VBoxContainer();
            panel.AddChild(content);

            // The header doubles as this group's row-selection target
            // (for the Remove button below) and its rename trigger
            // (double-click).
            var header = new Button
            {
                Text = GroupDisplayName(Song.Groups[groupIndex], groupIndex),
                ToggleMode = true,
                ButtonGroup = headerGroup,
                ClipText = true,
                TooltipText = "Click to select, double-click to rename",
            };
            ApplySelectionStyle(header);
            ConnectPressed(header, () => _selectedGroupIndex = groupIndex);
            ConnectGuiInput(header, e => OnGroupHeaderGuiInput(e, groupIndex, content, header));
            var headerBg = new StyleBoxFlat
            {
                BgColor = ChiptrackerPalette.SteelBlue,
                ContentMarginLeft = 8,
                ContentMarginTop = 5,
                ContentMarginBottom = 5,
            };
            header.AddThemeStyleboxOverride("normal", headerBg);
            header.AddThemeColorOverride("font_color", ChiptrackerPalette.PaleIce);
            content.AddChild(header);

            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            content.AddChild(scroll);
            var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(rows);
            for (var ch = 0; ch < Song.Channels.Count; ch++)
            {
                if (Song.Channels[ch].GroupIndex == groupIndex)
                    rows.AddChild(MakeGroupChannelRow(ch, groupIndex));
            }

            return panel;
        }

        // One row per channel listed under a Group column: <- and ->
        // icon buttons that shift the channel into the adjacent group
        // (disabled at whichever end has no neighbor), with the
        // channel's display name between them.
        Control MakeGroupChannelRow(int channelIndex, int groupIndex)
        {
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 8);
            margin.AddThemeConstantOverride("margin_right", 8);
            margin.AddThemeConstantOverride("margin_top", 4);
            margin.AddThemeConstantOverride("margin_bottom", 4);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            margin.AddChild(row);

            // The Metronome channel stays in the Metronome group, and no
            // other channel can join that group.
            var pinned = Song.IsMetronomeChannel(channelIndex);
            var leftButton = MakeMoveButton(GetThemeIcon("decrement", "TabBar"), "Move to the previous group");
            leftButton.Disabled = groupIndex <= 0 || pinned || Song.IsMetronomeGroup(groupIndex - 1);
            ConnectPressed(leftButton, () => OnMoveChannelGroup(channelIndex, -1));
            row.AddChild(leftButton);

            var nameLabel = new Label
            {
                Text = ChannelDisplayName(Song.Channels[channelIndex], channelIndex),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                ClipText = true,
            };
            row.AddChild(nameLabel);

            var rightButton = MakeMoveButton(GetThemeIcon("increment", "TabBar"), "Move to the next group");
            rightButton.Disabled = groupIndex >= Song.Groups.Count - 1 || pinned || Song.IsMetronomeGroup(groupIndex + 1);
            ConnectPressed(rightButton, () => OnMoveChannelGroup(channelIndex, 1));
            row.AddChild(rightButton);

            return margin;
        }

        // Small round icon-only button -- reuses TabBar's own built-in
        // increment/decrement chevron icons.
        static Button MakeMoveButton(Texture2D icon, string tooltip) => new()
        {
            Icon = icon,
            Flat = true,
            TooltipText = tooltip,
        };

        internal void OnMoveChannelGroup(int channelIndex, int direction)
        {
            if (Song == null || channelIndex < 0 || channelIndex >= Song.Channels.Count)
                return;
            var channel = Song.Channels[channelIndex];
            var newGroup = channel.GroupIndex + direction;
            if (newGroup < 0 || newGroup >= Song.Groups.Count)
                return;
            if (Song.IsMetronomeChannel(channelIndex) || Song.IsMetronomeGroup(newGroup))
                return;
            channel.GroupIndex = newGroup;
            RebuildGroupColumns();
            EmitSignal(SignalName.ChannelGroupChanged);
        }

        void OnGroupHeaderGuiInput(InputEvent @event, int groupIndex, VBoxContainer content, Button header)
        {
            if (@event is InputEventMouseButton { Pressed: true, DoubleClick: true })
                BeginGroupRename(groupIndex, content, header);
        }

        void BeginGroupRename(int groupIndex, VBoxContainer content, Button header)
        {
            header.Hide();
            var lineEdit = new LineEdit
            {
                Text = Song.Groups[groupIndex].Name,
                PlaceholderText = $"Group {groupIndex + 1}",
            };
            ConnectTextSubmitted(lineEdit, _ => CommitGroupRename(groupIndex, lineEdit, header));
            ConnectFocusExited(lineEdit, () => CommitGroupRename(groupIndex, lineEdit, header));
            content.AddChild(lineEdit);
            content.MoveChild(lineEdit, 0);
            lineEdit.GrabFocus();
            lineEdit.SelectAll();
        }

        public void CommitGroupRename(int groupIndex, LineEdit lineEdit, Button header)
        {
            if (!GodotObject.IsInstanceValid(lineEdit) || lineEdit.IsQueuedForDeletion())
                return;
            if (Song == null || groupIndex < 0 || groupIndex >= Song.Groups.Count)
                return;
            var newName = lineEdit.Text.Trim();
            if (RenameAllowed(Song.IsMetronomeGroup(groupIndex), newName))
                Song.Groups[groupIndex].Name = newName;
            lineEdit.QueueFree();
            if (GodotObject.IsInstanceValid(header))
                header.Show();
            Refresh();
            EmitSignal(SignalName.GroupRenamed, groupIndex);
        }

        // Add/Remove Metronome and the accent toggle, sharing the Groups
        // tab's bottom button row. This is the only way the metronome is
        // created or removed; its channel, group and instrument are
        // otherwise protected.
        void BuildMetronomeControls()
        {
            var row = (HBoxContainer)_addGroupButton.GetParent();
            row.AddChild(new VSeparator());
            _addMetronomeButton = new Button
            {
                Text = "Add Metronome",
                TooltipText = "Adds a Metronome instrument, group and channel that ticks on every beat",
            };
            ConnectPressed(_addMetronomeButton, OnAddMetronomePressed);
            row.AddChild(_addMetronomeButton);
            _removeMetronomeButton = new Button { Text = "Remove Metronome" };
            ConnectPressed(_removeMetronomeButton, OnRemoveMetronomePressed);
            row.AddChild(_removeMetronomeButton);
            _metronomeAccentCheck = new CheckBox
            {
                Text = "Accent",
                TooltipText = "Tick the first beat of each bar louder than the others",
            };
            ConnectToggled(_metronomeAccentCheck, OnMetronomeAccentToggled);
            row.AddChild(_metronomeAccentCheck);
        }

        // There can only be one metronome, so Add is disabled while it exists.
        void UpdateMetronomeControls()
        {
            if (_addMetronomeButton == null || Song == null)
                return;
            var hasMetronome = Song.HasMetronome();
            _addMetronomeButton.Disabled = hasMetronome;
            _removeMetronomeButton.Disabled = !hasMetronome || Song.Channels.Count <= 1;
            _metronomeAccentCheck.SetPressedNoSignal(Song.MetronomeAccent);
        }

        void OnAddMetronomePressed()
        {
            if (Song == null || !Song.AddMetronome())
                return;
            Refresh();
            EmitSignal(SignalName.ChannelsChanged);
            EmitSignal(SignalName.GroupsChanged);
        }

        void OnRemoveMetronomePressed()
        {
            if (Song == null || !Song.RemoveMetronome())
                return;
            _selectedChannelIndex = -1;
            _selectedInstrumentIndex = -1;
            _selectedGroupIndex = -1;
            Refresh();
            EmitSignal(SignalName.ChannelsChanged);
            EmitSignal(SignalName.GroupsChanged);
        }

        void OnMetronomeAccentToggled(bool pressed)
        {
            if (Song == null)
                return;
            Song.MetronomeAccent = pressed;
            Song.SyncMetronome();
            EmitSignal(SignalName.MetronomeChanged);
        }

        internal void OnAddGroupPressed()
        {
            if (Song == null)
                return;
            Song.AddGroup();
            Refresh();
            EmitSignal(SignalName.GroupsChanged);
        }

        internal void OnRemoveGroupPressed()
        {
            if (Song == null || Song.Groups.Count <= 1)
                return;
            var index = _selectedGroupIndex >= 0 && _selectedGroupIndex < Song.Groups.Count
                ? _selectedGroupIndex
                : LastIndexNotMetronome(Song.Groups.Count, i => Song.IsMetronomeGroup(i));
            if (index <= 0) // Group 1 (leftmost/default) is never removable
                return;
            if (Song.IsMetronomeGroup(index)) // only Remove Metronome removes the Metronome group
                return;
            Song.RemoveGroup(index);
            _selectedGroupIndex = -1;
            Refresh();
            EmitSignal(SignalName.GroupsChanged);
        }

        // ---------- Shared helpers ----------

        internal bool RenameAllowed(bool isMetronomeItem, string newName)
        {
            if (isMetronomeItem)
                return false;
            return !Song.IsReservedName(newName);
        }

        static int LastIndexNotMetronome(int count, System.Func<int, bool> isMetronome)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (!isMetronome(i))
                    return i;
            }
            return -1;
        }
    }
}
#endif

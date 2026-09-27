#if TOOLS
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.UI
{
    // C# counterpart of addons/chiptracker/ui/chiptracker_main_view.gd.
    // Root of the Chiptracker .Net main-screen tab. Owns the Song being
    // edited and wires the transport bar, pattern grid, cell editor,
    // instrument panel, and dock together, talking to the engine layer
    // directly (PlaybackEngine, Synth, WavRenderer).
    //
    // NOT ported: MIDI input (ChiptrackerMidiNode, hardware keyboard
    // profiles, the Akai AUTOMATION-button-toggles-Record mapping) --
    // that's the next deferred chunk. M8's pattern/song audio cache is
    // wired in (see _audioCache/RebuildCache/OnPlaybackLooped): the Play
    // button streams from CachedPlaybackEngine, rebuilding first if a
    // dirtying edit landed since the last rebuild; bar preview stays on
    // the plain PlaybackEngine since it needs no caching. M9 live
    // recording is wired in too (see _recording/PlayingCursor/
    // LiveRecordTarget/EndTake): REC writes notes at the playing row
    // instead of the cursor, a recorded take groups into one undo step
    // via GridEditHistory's BeginGroup/EndGroup, and a looping pass
    // rebuilds+refreshes the cached stream at each wrap so edits made
    // during it are heard on the next pass.
    [Tool]
    public partial class ChiptrackerMainView : VBoxContainer
    {
        // internal, not private: EnvelopeArchetypesTest-style same-assembly
        // tests poke these directly, the same way m6_ui_load_test.gd pokes
        // GDScript's "private by convention only" @onready vars.
        internal TransportBar _transportBar;
        internal CellEditor _cellEditor;
        internal PatternGrid _patternGrid;
        PatternGridHeader _patternGridHeader;
        ScrollContainer _gridScroll;
        internal InstrumentPanel _instrumentPanel;
        internal AudioStreamPlayer _audioPlayer;
        internal PlaybackEngine _playbackEngine;
        internal CachedPlaybackEngine _cachedPlaybackEngine;

        public Song Song { get; private set; }
        ChiptrackerDock _dock;

        // M8 pattern/song audio cache -- see "Pattern/song audio cache"
        // in CHIPTRACKER_SPEC.md. Full Play and cursor-start playback
        // stream from this instead of live-synthesizing; bar preview
        // (spacebar) still uses _playbackEngine directly since it needs
        // no caching (see OnBarPreviewRequested).
        // internal rather than private: PlaybackLoopTest pokes this
        // directly, the same way m9_rebuild_test.gd/m9_countin_test.gd
        // poke _audio_cache.
        internal PatternAudioCache _audioCache = new();

        public override void _Ready()
        {
            _transportBar = GetNode<TransportBar>("TransportBar");
            _cellEditor = GetNode<CellEditor>("CellEditor");
            _patternGrid = GetNode<PatternGrid>("Split/GridColumn/GridScroll/PatternGrid");
            _patternGridHeader = GetNode<PatternGridHeader>("Split/GridColumn/PatternGridHeader");
            _gridScroll = GetNode<ScrollContainer>("Split/GridColumn/GridScroll");
            _instrumentPanel = GetNode<InstrumentPanel>("Split/InstrumentPanel");
            _audioPlayer = GetNode<AudioStreamPlayer>("AudioStreamPlayer");
            _playbackEngine = GetNode<PlaybackEngine>("PlaybackEngine");
            _cachedPlaybackEngine = GetNode<CachedPlaybackEngine>("CachedPlaybackEngine");
            _cachedPlaybackEngine.Connect(CachedPlaybackEngine.SignalName.Looped, new Callable(this, MethodName.OnPlaybackLooped));

            SetSong(MakeDefaultSong());
            Connect(SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

            _transportBar.Connect(TransportBar.SignalName.PlayPressed, new Callable(this, MethodName.OnPlayPressed));
            _transportBar.Connect(TransportBar.SignalName.StopPressed, new Callable(this, MethodName.OnStopPressed));
            _transportBar.Connect(TransportBar.SignalName.LoopToggled, new Callable(this, MethodName.OnLoopToggled));
            _transportBar.Connect(TransportBar.SignalName.TempoChanged, new Callable(this, MethodName.OnTempoChanged));
            _transportBar.Connect(TransportBar.SignalName.RowsPerPatternChanged, new Callable(this, MethodName.OnRowsPerPatternChanged));
            _transportBar.Connect(TransportBar.SignalName.TapTempoPressed, new Callable(this, MethodName.OnTapTempo));
            _countIn = LoadCountIn();
            _transportBar.SetCountIn(_countIn);
            _transportBar.Connect(TransportBar.SignalName.CountInToggled, new Callable(this, MethodName.OnCountInToggled));

            _patternGrid.Connect(PatternGrid.SignalName.CellSelected, new Callable(this, MethodName.OnCellSelected));
            _patternGrid.Connect(PatternGrid.SignalName.OctaveChanged, new Callable(_transportBar, TransportBar.MethodName.SetOctave));
            _patternGrid.Connect(PatternGrid.SignalName.NoteKeyPressed, new Callable(this, MethodName.OnNoteKeyPressed));
            _patternGrid.Connect(PatternGrid.SignalName.NoteCleared, new Callable(this, MethodName.OnNoteCleared));
            _patternGrid.Connect(PatternGrid.SignalName.NoteMoveRequested, new Callable(this, MethodName.OnNoteMoveRequested));
            _patternGrid.Connect(PatternGrid.SignalName.BarPreviewRequested, new Callable(this, MethodName.OnBarPreviewRequested));
            _patternGrid.Connect(PatternGrid.SignalName.GridUndoRequested, new Callable(this, MethodName.OnUndoPressed));
            _patternGrid.Connect(PatternGrid.SignalName.GridRedoRequested, new Callable(this, MethodName.OnRedoPressed));
            _patternGridHeader.Bind(_patternGrid, _gridScroll);
            _transportBar.SetOctave(_patternGrid.Octave);

            _cellEditor.Connect(CellEditor.SignalName.CellEdited, new Callable(this, MethodName.OnCellEditorCellEdited));
            _cellEditor.Connect(CellEditor.SignalName.EditModeToggled, new Callable(this, MethodName.OnEditModeToggled));
            _cellEditor.Connect(CellEditor.SignalName.RecordToggled, new Callable(this, MethodName.OnRecordToggled));
            _recordLatencyMs = LoadRecordLatency();
            _cellEditor.SetRecordLatency(_recordLatencyMs);
            _cellEditor.Connect(CellEditor.SignalName.RecordLatencyChanged, new Callable(this, MethodName.OnRecordLatencyChanged));
            _cellEditor.Connect(CellEditor.SignalName.UndoRequested, new Callable(this, MethodName.OnUndoPressed));
            _cellEditor.Connect(CellEditor.SignalName.RedoRequested, new Callable(this, MethodName.OnRedoPressed));
            _cellEditor.Connect(CellEditor.SignalName.ExportRequested, new Callable(this, MethodName.OnExportRequested));

            _instrumentPanel.Connect(InstrumentPanel.SignalName.InstrumentEdited, new Callable(this, MethodName.MarkDirty));
        }

        // ---------- Live recording (M9) ----------

        internal bool _recording;

        // Recording latency in milliseconds (the "Lat" box beside REC):
        // how far back from the playhead a recorded note is placed. It
        // depends on the machine's audio hardware, not on the song, so
        // it's kept in the editor settings; until it has been set, it
        // defaults to the audio output latency.
        const string RecordLatencySetting = "chiptracker/record_latency_ms";
        internal float _recordLatencyMs;

        // Whether Record starts with a one-bar count-in (the Count-in
        // box beside REC). A per-machine preference like the latency,
        // kept in the editor settings.
        const string CountInSetting = "chiptracker/record_count_in";
        internal bool _countIn;

        bool LoadCountIn()
        {
            if (Godot.Engine.IsEditorHint())
            {
                var settings = EditorInterface.Singleton.GetEditorSettings();
                if (settings.HasSetting(CountInSetting))
                    return settings.GetSetting(CountInSetting).AsBool();
            }
            return false;
        }

        void OnCountInToggled(bool enabled)
        {
            _countIn = enabled;
            if (Godot.Engine.IsEditorHint())
                EditorInterface.Singleton.GetEditorSettings().SetSetting(CountInSetting, enabled);
        }

        float LoadRecordLatency()
        {
            if (Godot.Engine.IsEditorHint())
            {
                var settings = EditorInterface.Singleton.GetEditorSettings();
                if (settings.HasSetting(RecordLatencySetting))
                    return settings.GetSetting(RecordLatencySetting).AsSingle();
            }
            return (float)Mathf.Round(AudioServer.GetOutputLatency() * 1000.0);
        }

        void OnRecordLatencyChanged(float milliseconds)
        {
            _recordLatencyMs = milliseconds;
            if (Godot.Engine.IsEditorHint())
                EditorInterface.Singleton.GetEditorSettings().SetSetting(RecordLatencySetting, milliseconds);
        }

        internal void OnRecordToggled(bool enabled)
        {
            _recording = enabled;
            if (!enabled)
                EndTake();
            if (enabled && !_patternGrid.EditMode)
                _cellEditor.ToggleEditMode();
            // Record with the count-in on and nothing playing: count in, then go.
            if (enabled && _countIn && PlayingCursor() == null)
                OnPlayPressed();
        }

        // The playback cursor of whichever engine is currently playing, or null.
        PlaybackCursor PlayingCursor()
        {
            var cachedState = _cachedPlaybackEngine.State;
            if (cachedState != null && cachedState.Playing)
                return cachedState;
            var liveState = _playbackEngine.State;
            if (liveState != null && liveState.Playing)
                return liveState;
            return null;
        }

        // (pattern index, row) a recorded note belongs to right now, or
        // (-1, -1) if the playhead isn't on a valid order entry.
        Vector2I LiveRecordTarget(PlaybackCursor cursor)
        {
            var orderCount = Song.OrderList.Count;
            if (cursor.OrderIndex < 0 || cursor.OrderIndex >= orderCount)
                return new Vector2I(-1, -1);
            var rowsInOrder = new System.Collections.Generic.List<int>();
            foreach (var patternIndex in Song.OrderList)
                rowsInOrder.Add(Song.Patterns[patternIndex].Rows.Count);
            var isCached = cursor == _cachedPlaybackEngine.State;
            var looping = isCached ? _cachedPlaybackEngine.Loop : _playbackEngine.Loop;
            // The cursor holds the position from the last frame; for the
            // cached engine, read the playhead as it is right now (this
            // key press) instead.
            var orderIndex = cursor.OrderIndex;
            var rowIndex = cursor.RowIndex;
            var samplesIntoRow = cursor.SamplesIntoRow;
            if (isCached)
            {
                var now = _cachedPlaybackEngine.NowSample();
                if (now >= 0)
                {
                    var located = _cachedPlaybackEngine.Locate(now);
                    orderIndex = located.X;
                    rowIndex = located.Y;
                    samplesIntoRow = located.Z;
                }
            }
            var offsetSamples = Mathf.RoundToInt(_recordLatencyMs * PlaybackState.SampleRate / 1000.0f);
            var target = LiveRecord.Resolve(orderIndex, rowIndex, samplesIntoRow, cursor.SamplesPerRow, rowsInOrder, looping, offsetSamples);
            return new Vector2I(Song.OrderList[target.X], target.Y);
        }

        public void SetDock(ChiptrackerDock dock)
        {
            _dock = dock;
            _dock.SetSong(Song);
            _dock.Connect(ChiptrackerDock.SignalName.InstrumentSelected, new Callable(_instrumentPanel, InstrumentPanel.MethodName.EditInstrument));
            _dock.Connect(ChiptrackerDock.SignalName.AddInstrumentRequested, new Callable(this, MethodName.OnAddInstrumentRequested));
            _dock.Connect(ChiptrackerDock.SignalName.RemoveInstrumentRequested, new Callable(this, MethodName.OnRemoveInstrumentRequested));
            _dock.Connect(ChiptrackerDock.SignalName.DuplicateInstrumentRequested, new Callable(this, MethodName.OnDuplicateInstrumentRequested));
            _dock.Connect(ChiptrackerDock.SignalName.InstrumentRenamed, new Callable(this, MethodName.OnInstrumentRenamed));
            _dock.Connect(ChiptrackerDock.SignalName.ChannelsChanged, new Callable(this, MethodName.OnChannelsChanged));
            _dock.Connect(ChiptrackerDock.SignalName.ChannelRenamed, new Callable(this, MethodName.OnChannelRenamed));
            _dock.Connect(ChiptrackerDock.SignalName.ChannelMutated, new Callable(this, MethodName.MarkDirty));
            _dock.Connect(ChiptrackerDock.SignalName.PatternsChanged, new Callable(this, MethodName.OnPatternsChanged));
            _dock.Connect(ChiptrackerDock.SignalName.PatternRenamed, new Callable(this, MethodName.OnPatternRenamed));
            _dock.Connect(ChiptrackerDock.SignalName.OrderListChanged, new Callable(this, MethodName.MarkDirty));
            _dock.Connect(ChiptrackerDock.SignalName.PatternIndexRequested, new Callable(this, MethodName.OnPatternIndexRequested));
            _dock.Connect(ChiptrackerDock.SignalName.ChannelGroupChanged, new Callable(this, MethodName.OnChannelGroupOrGroupsChanged));
            _dock.Connect(ChiptrackerDock.SignalName.GroupsChanged, new Callable(this, MethodName.OnChannelGroupOrGroupsChanged));
            _dock.Connect(ChiptrackerDock.SignalName.GroupRenamed, new Callable(this, MethodName.OnGroupRenamed));
            _dock.Connect(ChiptrackerDock.SignalName.MetronomeChanged, new Callable(this, MethodName.OnMetronomeChanged));
            _dock.Connect(ChiptrackerDock.SignalName.MidiViewShown, new Callable(this, MethodName.RefreshMidiMappings));
        }

        // ---------- MIDI input ----------

        // Hardware MIDI reaches the editor's own nodes (like this tab)
        // but not nodes inside the edited scene, so the tab forwards each
        // event to the bound song node's ChiptrackerMidiNode child, which
        // applies the keyboard profile and emits NoteOn. The child is
        // looked up per event rather than cached at bind time, so adding
        // the node (or its profile) after the song node is already bound
        // works without a scene reload.
        Engine.ChiptrackerMidiNode FindMidiNode()
        {
            if (BoundSongNode == null || !GodotObject.IsInstanceValid(BoundSongNode))
                return null;
            foreach (var child in BoundSongNode.GetChildren())
            {
                if (child is Engine.ChiptrackerMidiNode midiNode)
                {
                    // Preview only: MIDI here plays the open instrument, it doesn't stamp cells.
                    if (!midiNode.IsConnected(Engine.ChiptrackerMidiNode.SignalName.NoteOn, new Callable(this, MethodName.OnMidiNoteOn)))
                        midiNode.Connect(Engine.ChiptrackerMidiNode.SignalName.NoteOn, new Callable(this, MethodName.OnMidiNoteOn));
                    if (!midiNode.IsConnected(Engine.ChiptrackerMidiNode.SignalName.ActionRequested, new Callable(this, MethodName.OnMidiAction)))
                        midiNode.Connect(Engine.ChiptrackerMidiNode.SignalName.ActionRequested, new Callable(this, MethodName.OnMidiAction));
                    return midiNode;
                }
            }
            return null;
        }

        void RefreshMidiMappings()
        {
            var midiNode = FindMidiNode();
            _dock.ShowMidiMappings(midiNode?.KeyboardProfile);
        }

        internal void OnMidiAction(string action)
        {
            switch (action)
            {
                case Engine.MidiKeyboard.ActionToggleEditMode:
                    _cellEditor.ToggleEditMode();
                    break;
                case Engine.MidiKeyboard.ActionPlay:
                    OnMidiPlay();
                    break;
                case Engine.MidiKeyboard.ActionUndoGridEdit:
                    OnUndoPressed();
                    break;
                case Engine.MidiKeyboard.ActionRedoGridEdit:
                    OnRedoPressed();
                    break;
                case Engine.MidiKeyboard.ActionToggleRecord:
                    _cellEditor.ToggleRecord();
                    break;
                case Engine.MidiKeyboard.ActionTapTempo:
                    OnTapTempo();
                    break;
            }
        }

        const int MidiDoublePressMsec = 350;
        long _lastMidiPlayMsec = -MidiDoublePressMsec * 2;

        // Single press toggles Play/Stop. A second press within
        // MidiDoublePressMsec is a "double press": stop and jump the
        // cursor to row 0 of the first pattern in the order list.
        void OnMidiPlay()
        {
            var now = (long)Time.GetTicksMsec();
            if (now - _lastMidiPlayMsec <= MidiDoublePressMsec)
            {
                _lastMidiPlayMsec = -MidiDoublePressMsec * 2;
                OnStopPressed();
                MoveCursorToTop();
                return;
            }
            _lastMidiPlayMsec = now;
            if (IsPlaying())
                OnStopPressed();
            else
                OnPlayPressed();
        }

        void MoveCursorToTop()
        {
            if (Song == null || Song.Patterns.Count == 0)
                return;
            var firstPattern = Song.OrderList.Count > 0 ? Song.OrderList[0] : 0;
            _patternGrid.SetPatternIndex(firstPattern);
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            UpdatePatternNameLabel();
        }

        // In Edit mode a MIDI key enters the note at the cursor, exactly
        // like typing a note on the computer keyboard (which also
        // previews it); otherwise it only previews the open instrument.
        internal void OnMidiNoteOn(int note, int volume)
        {
            if (_patternGrid.EditMode)
                OnNoteKeyPressed(note);
            else
                _instrumentPanel.PreviewNote(note);
        }

        public void SetSong(Song newSong)
        {
            Song = newSong;
            _audioCache = new PatternAudioCache();
            _transportBar.SetCacheClean(false);
            _gridEditHistory.Clear(); // steps reference indices into the old song
            UpdateUndoRedoButtons();
            _patternGrid.SetSong(Song);
            _transportBar.SetTempo(Song.Tempo);
            _transportBar.SetRowsPerPattern(Song.RowsPerPattern);
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            UpdatePatternNameLabel();
            _dock?.SetSong(Song);
        }

        // Re-syncs UI widgets with the current Song state without
        // resetting selection.
        public void Refresh()
        {
            _patternGrid.UpdateMinSize();
            _patternGrid.QueueRedraw();
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            _transportBar.SetTempo(Song.Tempo);
            _transportBar.SetRowsPerPattern(Song.RowsPerPattern);
            UpdatePatternNameLabel();
            _dock?.Refresh();
        }

        void UpdatePatternNameLabel()
        {
            if (Song == null || Song.Patterns.Count == 0 || _patternGrid.PatternIndex >= Song.Patterns.Count)
            {
                _transportBar.SetPatternName("");
                return;
            }
            var pattern = Song.Patterns[_patternGrid.PatternIndex];
            var label = string.IsNullOrEmpty(pattern.Name) ? $"Pattern {_patternGrid.PatternIndex}" : pattern.Name;
            _transportBar.SetPatternName(label);
        }

        // A small pre-filled demo song (C major arpeggio lead + two-note
        // bass) so the tab has something to click through and play
        // immediately, rather than opening to 16 empty rows.
        static Song MakeDefaultSong()
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 4, RowsPerPattern = 16 };

            var lead = new Channel { Name = "Lead", InstrumentType = "square" };
            var bass = new Channel { Name = "Bass", InstrumentType = "triangle" };
            song.Channels.Add(lead);
            song.Channels.Add(bass);

            var leadInstrument = new Instrument { Id = 0, Waveform = "square", DutyCycle = 0.5f };
            var bassInstrument = new Instrument { Id = 1, Waveform = "triangle" };
            song.Instruments.Add(leadInstrument);
            song.Instruments.Add(bassInstrument);

            var pattern = new Pattern(song.RowsPerPattern, song.Channels.Count);
            var leadNotes = new System.Collections.Generic.Dictionary<int, int> { [0] = 60, [4] = 64, [8] = 67, [12] = 72 };
            foreach (var (row, note) in leadNotes)
                pattern.Rows[row][0] = new Cell { Note = note, InstrumentId = 0, Volume = 12 };
            var bassNotes = new System.Collections.Generic.Dictionary<int, int> { [0] = 36, [8] = 41 };
            foreach (var (row, note) in bassNotes)
                pattern.Rows[row][1] = new Cell { Note = note, InstrumentId = 1, Volume = 15 };

            song.Patterns.Add(pattern);
            song.OrderList.Add(0);
            return song;
        }

        internal void OnCellSelected(int row, int channel) => _cellEditor.SetCell(_patternGrid.GetSelectedCell());

        // Keyboard note entry while Edit mode is on: stamps the note +
        // current volume + whatever instrument is currently open in the
        // instrument panel onto the cell at the cursor (or, while
        // recording during playback, onto the playing row) then previews
        // it.
        internal void OnNoteKeyPressed(int note)
        {
            var patternIndex = _patternGrid.PatternIndex;
            var row = _patternGrid.SelectedRow;
            var channel = _patternGrid.SelectedChannel;
            var atCursor = true;
            var playingCursor = PlayingCursor();
            // During the count-in there's no playhead in the song yet: just preview.
            if (_recording && playingCursor != null && playingCursor.CountingIn)
            {
                _instrumentPanel.PreviewNote(note);
                return;
            }
            if (_recording && playingCursor != null)
            {
                var target = LiveRecordTarget(playingCursor);
                if (target.X >= 0)
                {
                    patternIndex = target.X;
                    row = target.Y;
                    atCursor = false;
                }
            }
            var cell = atCursor ? _patternGrid.GetSelectedCell() : CellAt(patternIndex, row, channel);
            if (cell == null)
                return;
            var before = cell.Snapshot();
            cell.Note = note;
            cell.Volume = _cellEditor.GetVolume();
            var instrument = _instrumentPanel.GetEditedInstrument();
            if (instrument != null)
                cell.InstrumentId = instrument.Id;
            if (atCursor)
                _cellEditor.SetCell(cell);
            _patternGrid.QueueRedraw();
            RecordCellEdit(patternIndex, row, channel, before, cell.Snapshot(), !atCursor);
            MarkPatternDirty(patternIndex);
            _instrumentPanel.PreviewNote(note);
            // The step box: drop the cursor after a note entered at it,
            // but not while the song is playing (live recording writes
            // at the playhead, not the cursor).
            if (atCursor && !IsPlaying())
                _patternGrid.MoveCursorDown(_transportBar.GetCursorStep());
        }

        void OnNoteCleared()
        {
            var cell = _patternGrid.GetSelectedCell();
            if (cell == null)
                return;
            var before = cell.Snapshot();
            cell.Note = -1;
            _cellEditor.SetCell(cell);
            _patternGrid.QueueRedraw();
            RecordCellEdit(_patternGrid.PatternIndex, _patternGrid.SelectedRow, _patternGrid.SelectedChannel, before, cell.Snapshot());
            MarkDirty();
        }

        // Shift+Up/Down while Edit mode is on: relocates the cell's
        // snapshot fields from the cursor to the adjacent row the grid
        // already confirmed is in-bounds and empty. Recorded as two
        // separate undo steps (clear the source, then set the
        // destination) since GridEditHistory.Step only holds one cell
        // each; undoing a move takes two presses of Undo but each one
        // still leaves the pattern in a consistent state.
        void OnNoteMoveRequested(int fromRow, int toRow, int channel)
        {
            var patternIndex = _patternGrid.PatternIndex;
            var fromCell = CellAt(patternIndex, fromRow, channel);
            var toCell = CellAt(patternIndex, toRow, channel);
            if (fromCell == null || toCell == null)
                return;
            var fromBefore = fromCell.Snapshot();
            var toBefore = toCell.Snapshot();
            toCell.ApplySnapshot(fromBefore);
            fromCell.Note = -1;
            _patternGrid.SelectedRow = toRow;
            _cellEditor.SetCell(toCell);
            _patternGrid.QueueRedraw();
            _patternGrid.EnsureSelectionVisible();
            RecordCellEdit(patternIndex, fromRow, channel, fromBefore, fromCell.Snapshot());
            RecordCellEdit(patternIndex, toRow, channel, toBefore, toCell.Snapshot());
            MarkDirty();
        }

        // CellEditor's note/instrument/volume spins and its Clear button
        // all funnel through here -- CellEditor already did the mutation
        // and hands back the before/after snapshot from the exact moment
        // it happened, so this only needs to record it and refresh
        // dependent UI.
        void OnCellEditorCellEdited(Dictionary before, Dictionary after)
        {
            _patternGrid.QueueRedraw();
            RecordCellEdit(_patternGrid.PatternIndex, _patternGrid.SelectedRow, _patternGrid.SelectedChannel, before, after);
            MarkDirty();
        }

        // Leaving Edit mode is the M8 cache's main rebuild trigger -- a
        // clean, deliberate signal that active note-entry has paused.
        void OnEditModeToggled(bool enabled)
        {
            _patternGrid.EditMode = enabled;
            if (!enabled)
            {
                if (_recording)
                {
                    _recording = false;
                    _cellEditor.SetRecord(false);
                    EndTake();
                }
                RebuildCache();
            }
        }

        internal void RebuildCache()
        {
            _transportBar.SetRebuilding(true);
            _audioCache.Rebuild(Song);
            _transportBar.SetRebuilding(false);
            _transportBar.SetCacheClean(_audioCache.IsClean(Song));
        }

        // Each time a looping pass wraps, rebuild whatever notes were
        // recorded during it and swap the new audio in, so they are
        // heard on the next pass.
        void OnPlaybackLooped()
        {
            if (_audioCache.IsClean(Song))
                return;
            RebuildCache();
            _cachedPlaybackEngine.RefreshStream();
        }

        Cell CellAt(int patternIndex, int row, int channel)
        {
            if (Song == null || patternIndex < 0 || patternIndex >= Song.Patterns.Count)
                return null;
            var pattern = Song.Patterns[patternIndex];
            if (row < 0 || row >= pattern.Rows.Count)
                return null;
            var rowCells = pattern.Rows[row];
            if (channel < 0 || channel >= rowCells.Count)
                return null;
            return rowCells[channel].As<Cell>();
        }

        // ---------- Grid-edit undo/redo ----------

        // Kept separate from the Godot editor's own undo/redo -- see
        // DECISIONS.md and GridEditHistory in the GDScript reference.
        // internal rather than private: LiveRecordTest pokes it directly,
        // the same way m9_take_test.gd pokes _grid_edit_history.
        internal readonly GridEditHistory _gridEditHistory = new();

        // Records one grid-edit undo step if `before` and `after`
        // actually differ (a spin box can fire ValueChanged without the
        // value having moved, e.g. clamping) and refreshes the undo/redo
        // buttons either way.
        //
        // `recording` is true for a note written at the playing row:
        // those join the current take (one undo step for all of them).
        // Any other edit closes the take instead of joining it.
        internal void RecordCellEdit(int patternIndex, int row, int channel, Dictionary before, Dictionary after, bool recording = false)
        {
            if (recording)
                _gridEditHistory.BeginGroup();
            else
                _gridEditHistory.EndGroup();
            if (!SnapshotsEqual(before, after))
            {
                _gridEditHistory.Push(patternIndex, row, channel, before, after,
                    DescribeCellEdit(patternIndex, row, channel, before, after));
            }
            UpdateUndoRedoButtons();
        }

        static bool SnapshotsEqual(Dictionary a, Dictionary b) =>
            a["note"].AsInt32() == b["note"].AsInt32() &&
            a["instrument_id"].AsInt32() == b["instrument_id"].AsInt32() &&
            a["volume"].AsInt32() == b["volume"].AsInt32();

        // Builds the undo/redo button tooltip text for one edit, e.g.
        // "Pattern 0 row 03, Lead: note --- -> C4".
        string DescribeCellEdit(int patternIndex, int row, int channel, Dictionary before, Dictionary after)
        {
            var patternLabel = $"Pattern {patternIndex}";
            if (Song != null && patternIndex >= 0 && patternIndex < Song.Patterns.Count)
            {
                var pattern = Song.Patterns[patternIndex];
                if (!string.IsNullOrEmpty(pattern.Name))
                    patternLabel = pattern.Name;
            }
            var channelLabel = $"Ch {channel}";
            if (Song != null && channel >= 0 && channel < Song.Channels.Count)
            {
                var ch = Song.Channels[channel];
                if (!string.IsNullOrEmpty(ch.Name))
                    channelLabel = ch.Name;
            }

            var parts = new System.Collections.Generic.List<string>();
            if (before["note"].AsInt32() != after["note"].AsInt32())
                parts.Add($"note {Synth.NoteName(before["note"].AsInt32())} -> {Synth.NoteName(after["note"].AsInt32())}");
            if (before["instrument_id"].AsInt32() != after["instrument_id"].AsInt32())
                parts.Add($"instr {before["instrument_id"].AsInt32()} -> {after["instrument_id"].AsInt32()}");
            if (before["volume"].AsInt32() != after["volume"].AsInt32())
                parts.Add($"vol {before["volume"].AsInt32()} -> {after["volume"].AsInt32()}");
            var changeText = parts.Count > 0 ? string.Join(", ", parts) : "no change";
            return $"{patternLabel} row {row:D2}, {channelLabel}: {changeText}";
        }

        internal void OnUndoPressed()
        {
            EndTake();
            foreach (var step in _gridEditHistory.UndoGroup())
                ApplyGridEditStep(step, step.Before);
            UpdateUndoRedoButtons();
        }

        internal void OnRedoPressed()
        {
            EndTake();
            foreach (var step in _gridEditHistory.RedoGroup())
                ApplyGridEditStep(step, step.After);
            UpdateUndoRedoButtons();
        }

        // Closes the current take, so the next recorded note starts a new one.
        void EndTake() => _gridEditHistory.EndGroup();

        // Applies `snapshot` (either side of `step`) to the cell it was
        // recorded against, and follows the edit visually -- jumps the
        // grid to that pattern/cell if the user has since navigated
        // elsewhere.
        void ApplyGridEditStep(GridEditHistory.Step step, Dictionary snapshot)
        {
            var cell = CellAt(step.PatternIndex, step.Row, step.Channel);
            if (cell == null)
                return;
            cell.ApplySnapshot(snapshot);
            if (_patternGrid.PatternIndex != step.PatternIndex)
                _patternGrid.SetPatternIndex(step.PatternIndex);
            _patternGrid.SelectedRow = step.Row;
            _patternGrid.SelectedChannel = step.Channel;
            _cellEditor.SetCell(cell);
            _patternGrid.QueueRedraw();
            _patternGrid.EnsureSelectionVisible();
            UpdatePatternNameLabel();
            MarkDirty();
        }

        void UpdateUndoRedoButtons()
        {
            var undoLabel = _gridEditHistory.PeekUndoLabel();
            var redoLabel = _gridEditHistory.PeekRedoLabel();
            _cellEditor.SetUndoRedoState(
                _gridEditHistory.CanUndo(), _gridEditHistory.CanRedo(),
                !string.IsNullOrEmpty(undoLabel) ? $"Undo: {undoLabel}" : "",
                !string.IsNullOrEmpty(redoLabel) ? $"Redo: {redoLabel}" : "");
        }

        // Invalidates the whole audio cache -- most edits (instrument,
        // channel, rows-per-pattern, ...) can affect every pattern that
        // uses them, so this is the correct behavior, not just the
        // simplest one. Also handles scene-dirty marking: Godot's scene
        // "unsaved changes" indicator doesn't notice in-place mutations
        // to an exported Resource's own fields (only edits made directly
        // through the Inspector trigger it), so flag the scene dirty
        // ourselves on every tracker edit while bound to a node, so
        // Ctrl+S actually offers to save what was just changed.
        void MarkDirty()
        {
            _audioCache.MarkAllDirty();
            _transportBar.SetCacheClean(false);
            if (BoundSongNode != null && Godot.Engine.IsEditorHint())
                EditorInterface.Singleton.MarkSceneAsUnsaved();
        }

        // Like MarkDirty(), for an edit that can only change one
        // pattern's audio (a note written into it): only that pattern is
        // re-rendered on rebuild.
        void MarkPatternDirty(int patternIndex)
        {
            _audioCache.MarkPatternDirty(patternIndex);
            _transportBar.SetCacheClean(false);
            if (BoundSongNode != null && Godot.Engine.IsEditorHint())
                EditorInterface.Singleton.MarkSceneAsUnsaved();
        }

        // EditorBridge (a different namespace) needs to trigger the same
        // dirty-marking an external tool-call mutation should cause.
        internal void MarkDirtyExternal() => MarkDirty();

        // Set when a ChiptrackerSongNode is selected in the Scene dock
        // (see ChiptrackerNetPlugin's _Handles()/_Edit()) -- Song then IS
        // that node's song (same Resource reference), so edits write
        // straight into scene-persisted data instead of a scratch song
        // that vanishes on restart.
        internal Engine.ChiptrackerSongNode BoundSongNode { get; private set; }

        // Binds the tab directly to a ChiptrackerSongNode's song --
        // called by the plugin when that node type is selected in the
        // Scene dock. Saving the scene afterward persists every edit made
        // through the tab or the editor MCP bridge, since they all
        // mutate this same Resource in place.
        public void BindSongNode(Engine.ChiptrackerSongNode node)
        {
            BoundSongNode = node;
            _patternGrid.SongNode = node;
            SetSong(node.Song);
        }

        void OnAddInstrumentRequested()
        {
            var instrument = new Instrument { Id = Song.NextInstrumentId(), Waveform = "square", DutyCycle = 0.5f };
            Song.Instruments.Add(instrument);
            _dock.Refresh();
            MarkDirty();
        }

        void OnDuplicateInstrumentRequested(int index)
        {
            if (index < 0 || index >= Song.Instruments.Count)
                return;
            var original = Song.Instruments[index];
            var copy = (Instrument)original.Duplicate(true);
            copy.Id = Song.NextInstrumentId();
            if (!string.IsNullOrEmpty(copy.Name))
                copy.Name += " (Copy)";
            Song.Instruments.Add(copy);
            _dock.Refresh();
            MarkDirty();
        }

        void OnRemoveInstrumentRequested(int index)
        {
            if (index < 0 || index >= Song.Instruments.Count)
                return;
            var removed = Song.Instruments[index];
            if (Song.IsMetronomeInstrument(removed))
                return;
            Song.Instruments.RemoveAt(index);
            if (_instrumentPanel.GetEditedInstrument() == removed)
                _instrumentPanel.EditInstrument(null);
            _dock.Refresh();
            MarkDirty();
        }

        void OnInstrumentRenamed(int index)
        {
            _instrumentPanel.RefreshTitle();
            MarkDirty();
        }

        void OnChannelsChanged()
        {
            var edited = _instrumentPanel.GetEditedInstrument();
            if (edited != null && !Song.Instruments.Contains(edited))
                _instrumentPanel.EditInstrument(null);
            _patternGrid.SetSong(Song);
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            UpdatePatternNameLabel();
            _dock.Refresh();
            MarkDirty();
        }

        void OnChannelRenamed(int index)
        {
            _patternGrid.QueueRedraw();
            MarkDirty();
        }

        void OnChannelGroupOrGroupsChanged()
        {
            _patternGrid.QueueRedraw();
            MarkDirty();
        }

        void OnGroupRenamed(int index)
        {
            _patternGrid.QueueRedraw();
            MarkDirty();
        }

        void OnMetronomeChanged()
        {
            _patternGrid.QueueRedraw();
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            MarkDirty();
        }

        void OnPatternsChanged()
        {
            if (_patternGrid.PatternIndex >= Song.Patterns.Count)
                _patternGrid.SetPatternIndex(Mathf.Max(0, Song.Patterns.Count - 1));
            _patternGrid.UpdateMinSize();
            _patternGrid.QueueRedraw();
            UpdatePatternNameLabel();
            MarkDirty();
        }

        void OnPatternRenamed(int index)
        {
            UpdatePatternNameLabel();
            MarkDirty();
        }

        void OnPatternIndexRequested(int index)
        {
            _patternGrid.SetPatternIndex(index);
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            UpdatePatternNameLabel();
        }

        void OnTempoChanged(int tempo)
        {
            Song.Tempo = tempo;
            MarkDirty();
        }

        // Tap tempo (the TAP TEMPO button, or the ` key): each tap
        // re-works the tempo from the recent taps and applies it to the
        // song and the tempo field. Like editing the tempo field, it
        // doesn't change audio that is already playing; it applies from
        // the next Play.
        readonly TapTempo _tapTempo = new();

        internal void OnTapTempo()
        {
            var bpm = _tapTempo.Tap((long)Time.GetTicksMsec());
            if (bpm < 0 || Song == null)
                return;
            _transportBar.SetTempo(bpm);
            OnTempoChanged(bpm);
        }

        // Keyboard shortcuts that belong to the whole tab rather than the
        // pattern grid, so they work with or without Edit mode: the `
        // key taps the tempo. Skipped while the tab isn't showing, while
        // a text field has focus, for a held-key repeat, and with any
        // modifier held.
        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventKey keyEvent)
            {
                HandleShortcutKey(keyEvent);
                return;
            }
            if (@event is not InputEventMidi midiEvent)
                return;
            var midiNode = FindMidiNode();
            midiNode?.HandleMidiEvent(midiEvent);
        }

        void HandleShortcutKey(InputEventKey keyEvent)
        {
            if (!keyEvent.Pressed || keyEvent.Echo || keyEvent.Keycode != Key.Quoteleft)
                return;
            if (keyEvent.CtrlPressed || keyEvent.AltPressed || keyEvent.MetaPressed || keyEvent.ShiftPressed)
                return;
            if (!IsVisibleInTree())
                return;
            var focus = GetViewport().GuiGetFocusOwner();
            if (focus is LineEdit or TextEdit)
                return;
            GetViewport().SetInputAsHandled();
            OnTapTempo();
        }

        void OnRowsPerPatternChanged(int rows)
        {
            Song.SetRowsPerPattern(rows);
            _patternGrid.SelectedRow = Mathf.Min(_patternGrid.SelectedRow, rows - 1);
            _patternGrid.UpdateMinSize();
            _patternGrid.QueueRedraw();
            _patternGrid.EnsureSelectionVisible();
            _cellEditor.SetCell(_patternGrid.GetSelectedCell());
            MarkDirty();
        }

        void OnLoopToggled(bool enabled)
        {
            _playbackEngine.Loop = enabled; // bar preview: wraps the whole song
            _cachedPlaybackEngine.Loop = enabled; // full Play: repeats the current pattern
        }

        internal bool IsPlaying() => PlayingCursor() != null;

        // M8: Play always streams the pre-rendered cache (rebuilding it
        // first, synchronously, if a dirtying edit landed since the last
        // rebuild) rather than live-synthesizing. Bar preview is the one
        // exception; it stays on _playbackEngine.
        internal void OnPlayPressed()
        {
            EndTake(); // pressing Play starts a fresh pass, so a fresh take
            if (Song == null || Song.OrderList.Count == 0)
                return;
            if (!_audioCache.IsClean(Song))
                RebuildCache();
            _cachedPlaybackEngine.Setup(Song, _audioPlayer, _audioCache);
            _patternGrid.PlaybackState = _cachedPlaybackEngine.State;
            _cachedPlaybackEngine.State.RowAdvanced += OnRowAdvanced;
            _cachedPlaybackEngine.State.Finished += OnPlaybackFinished;
            // Start from wherever the currently-displayed pattern sits in
            // the order list, not always the very start of the song --
            // Play should continue from what you're looking at, not jump
            // back to position 0.
            var orderIndex = Song.OrderList.IndexOf(_patternGrid.PatternIndex);
            if (orderIndex == -1)
                orderIndex = 0;
            // Recording with the count-in on: a bar of clicks first, then
            // the pass from the top of the pattern.
            var countingIn = _recording && _countIn;
            _cachedPlaybackEngine.Play(orderIndex, _patternGrid.SelectedRow, countingIn);
        }

        const int BarSize = 16;

        // Spacebar in Edit mode: plays just the 16-row "bar" the cursor
        // is currently in, then stops automatically at the end of that
        // range instead of continuing through the rest of the pattern.
        internal void OnBarPreviewRequested()
        {
            // While recording, Space is a stop key: starting a bar
            // preview would fight the recording pass (both engines drive
            // the same playhead and stream).
            if (_recording)
            {
                OnStopPressed();
                return;
            }
            if (Song == null || Song.Patterns.Count == 0)
                return;
            var pattern = Song.Patterns[_patternGrid.PatternIndex];
            var barStart = _patternGrid.SelectedRow / BarSize * BarSize;
            var barEnd = Mathf.Min(barStart + BarSize - 1, pattern.Rows.Count - 1);

            _playbackEngine.Setup(Song, _audioPlayer);
            _patternGrid.PlaybackState = _playbackEngine.State;
            _playbackEngine.State.RowAdvanced += OnRowAdvanced;
            _playbackEngine.State.Finished += OnPlaybackFinished;

            var orderIndex = Song.OrderList.IndexOf(_patternGrid.PatternIndex);
            if (orderIndex == -1)
                orderIndex = 0;
            var startingOrderIndex = orderIndex;
            // Also stop if playback crosses into a different pattern
            // (order_index changes) -- a bar sitting at the end of its
            // pattern would otherwise roll into the next one instead of
            // stopping, since a fresh pattern's row_index restarts at 0
            // and would never exceed bar_end on its own.
            _playbackEngine.State.RowAdvanced += (currentOrderIndex, rowIndex) =>
                OnBarPreviewRowAdvanced(currentOrderIndex, rowIndex, startingOrderIndex, barEnd);
            _playbackEngine.Play(orderIndex, barStart);
        }

        internal void OnBarPreviewRowAdvanced(int currentOrderIndex, int rowIndex, int startingOrderIndex, int barEnd)
        {
            if (currentOrderIndex != startingOrderIndex || rowIndex > barEnd)
                OnStopPressed();
        }

        // Opens a save dialog (the same EditorFileDialog class used for
        // scene Save As, restricted to the project like it is) suggesting
        // a filename matching the currently edited scene, and renders the
        // whole song to whatever WAV path is chosen.
        void OnExportRequested()
        {
            if (!Godot.Engine.IsEditorHint() || Song == null)
                return;
            var dialog = new EditorFileDialog
            {
                FileMode = EditorFileDialog.FileModeEnum.SaveFile,
                Access = EditorFileDialog.AccessEnum.Resources,
            };
            dialog.AddFilter("*.wav", "WAV Audio");
            var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            var suggestedName = "song";
            if (sceneRoot != null && !string.IsNullOrEmpty(sceneRoot.SceneFilePath))
                suggestedName = sceneRoot.SceneFilePath.GetFile().GetBaseName();
            dialog.CurrentFile = suggestedName + ".wav";

            // DockRowHandler's fields are generically shaped (Action<string>/
            // Action), not LineEdit-specific, so they double as this
            // dialog's FileSelected/Canceled handlers -- keeps the actual
            // Connect() calls plain object+method Callables (see
            // DockRowHandler's own comment) even though this dialog is
            // short-lived rather than part of the persistent dock UI.
            var handler = new DockRowHandler
            {
                TextSubmitted = path =>
                {
                    var err = WavRenderer.RenderSong(Song, path);
                    if (err != Error.Ok)
                        GD.PushError($"Chiptracker: WAV export to '{path}' failed with error code {err}");
                    dialog.QueueFree();
                },
                Clicked = dialog.QueueFree,
            };
            dialog.SetMeta("_export_handler", handler);
            dialog.Connect(EditorFileDialog.SignalName.FileSelected, new Callable(handler, DockRowHandler.MethodName.OnTextSubmittedNotify));
            dialog.Connect(EditorFileDialog.SignalName.Canceled, new Callable(handler, DockRowHandler.MethodName.OnPressedNotify));
            EditorInterface.Singleton.GetBaseControl().AddChild(dialog);
            dialog.PopupCenteredRatio(0.5f);
        }

        internal void OnStopPressed()
        {
            EndTake();
            _playbackEngine.Stop();
            _cachedPlaybackEngine.Stop();
            _patternGrid.SetPlaybackRow(-1);
            _transportBar.SetStatus(0, 0, false);
        }

        internal void OnRowAdvanced(int orderIndex, int rowIndex)
        {
            if (orderIndex < Song.OrderList.Count)
            {
                var playingPatternIndex = Song.OrderList[orderIndex];
                if (playingPatternIndex != _patternGrid.PatternIndex)
                {
                    _patternGrid.FollowPattern(playingPatternIndex);
                    UpdatePatternNameLabel();
                }
            }
            _patternGrid.SetPlaybackRow(rowIndex);
            _transportBar.SetStatus(orderIndex, rowIndex, true);
        }

        void OnPlaybackFinished()
        {
            EndTake();
            _patternGrid.SetPlaybackRow(-1);
            _transportBar.SetStatus(0, 0, false);
        }

        // "Deep Sea" base background behind the whole tab.
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), ChiptrackerPalette.DeepNavy);
        }
    }
}
#endif

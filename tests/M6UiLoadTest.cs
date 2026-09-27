using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m6_ui_load_test.gd -- not a full interaction
    // test (that needs a human clicking around the editor UI), just
    // verifies the M6 UI scenes load and instantiate without errors, and
    // that the main view's default song round-trips through the
    // grid/dock/cell-editor wiring.
    //
    // NOT ported (per the "core: Song binding + pattern grid + cell
    // editing" M6 scope, and the same reasons M5BridgeCoreTest skips
    // play_preview's success path): MIDI-specific assertions from the
    // reference file (instrument panel envelope/pitch/layer editing now
    // has its own coverage in InstrumentPanelTest). Play now streams from
    // CachedPlaybackEngine (M8's cache); bar preview still uses
    // PlaybackEngine directly. This test's _Initialize() is async
    // specifically so `await` can yield real
    // frames for AddChild()'s deferred _Ready() -- seetests
    // /M5BridgeCoreTest.cs's MakeDispatcher() comment for why a
    // synchronous test can't just call _Ready() once here: this test
    // needs MULTIPLE real nodes (main view + dock, each with several
    // @-ready-style children) to finish entering the tree, not one
    // isolated node, so awaiting real frames is more faithful than
    // forcing every _Ready() by hand.
    public partial class M6UiLoadTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            Check(failures, mainViewScene != null, "main view scene loads");
            var dockScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_dock.tscn");
            Check(failures, dockScene != null, "dock scene loads");

            if (mainViewScene == null || dockScene == null)
            {
                Finish(failures);
                return;
            }

            var mainView = mainViewScene.Instantiate<ChiptrackerMainView>();
            Check(failures, mainView != null, "main view instantiates");
            var dock = dockScene.Instantiate<ChiptrackerDock>();
            Check(failures, dock != null, "dock instantiates");

            Root.AddChild(mainView);
            Root.AddChild(dock);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            mainView.SetDock(dock);

            Check(failures, mainView.Song != null, "main view has a default song");
            Check(failures, mainView.Song.Channels.Count == 2, "default demo song has 2 channels");
            Check(failures, mainView.Song.Instruments.Count == 2, "default demo song has 2 instruments");
            Check(failures, mainView._patternGrid.Song == mainView.Song, "grid received the song");
            Check(failures, (mainView._patternGrid.SizeFlagsVertical & Control.SizeFlags.Expand) == 0,
                "PatternGrid must not have SIZE_EXPAND on its vertical size flag, or its ScrollContainer parent can't scroll for tall patterns");
            mainView.Song.SetRowsPerPattern(32);
            mainView._patternGrid.UpdateMinSize();
            Check(failures, mainView._patternGrid.CustomMinimumSize.Y > 360,
                $"the grid's minimum size grows tall enough to need scrolling at 32 rows (got {mainView._patternGrid.CustomMinimumSize.Y})");
            mainView.Song.SetRowsPerPattern(16); // restore for the rest of this test
            Check(failures, dock._instrumentListContainer.GetChildCount() == 2, "dock lists the default instruments");
            Check(failures, dock._orderListContainer.GetChildCount() == 1, "dock lists the default order entry");

            // Simulate selecting the first cell and editing it, as a click
            // + typed edit would.
            mainView._patternGrid.SelectedRow = 0;
            mainView._patternGrid.SelectedChannel = 0;
            mainView.OnCellSelected(0, 0);
            var cell = mainView._patternGrid.GetSelectedCell();
            Check(failures, cell != null, "selected cell resolves");
            if (cell != null)
            {
                cell.Note = 60;
                mainView._cellEditor.SetCell(cell);
                Check(failures, mainView._cellEditor._noteSpin.Value == 60, "cell editor reflects edited note");
            }

            // Edit mode toggle: propagates to the grid, and gates arrow-key
            // navigation (synthetic InputEventKey, no real windowing input headlessly).
            mainView._cellEditor._editModeButton.ButtonPressed = true;
            mainView._cellEditor.OnEditModeToggled(true);
            Check(failures, mainView._patternGrid.EditMode, "toggling Edit mode on the cell editor propagates to the grid");
            Check(failures, mainView._cellEditor._editModeButton.Icon != null, "the Edit mode button generates a status-dot icon without erroring");

            mainView._patternGrid.SelectedRow = 5;
            mainView._patternGrid.SelectedChannel = 0;
            var downEvent = new InputEventKey { Keycode = Key.Down, Pressed = true };
            mainView._patternGrid._Input(downEvent);
            Check(failures, mainView._patternGrid.SelectedRow == 6, $"arrow-key Down moves the grid cursor while Edit mode is on (got {mainView._patternGrid.SelectedRow})");

            mainView._cellEditor._editModeButton.ButtonPressed = false;
            mainView._cellEditor.OnEditModeToggled(false);
            Check(failures, !mainView._patternGrid.EditMode, "toggling Edit mode off propagates to the grid");
            var rowBeforeIgnoredKey = mainView._patternGrid.SelectedRow;
            mainView._patternGrid._Input(downEvent);
            Check(failures, mainView._patternGrid.SelectedRow == rowBeforeIgnoredKey, "arrow keys are ignored while Edit mode is off");

            // Keyboard note entry (piano-style keyboard, gated on Edit
            // mode): 'Z' at the default octave 4 is note 60 (C4); '='
            // bumps the octave, making 'Z' note 72 (C5).
            mainView._cellEditor._editModeButton.ButtonPressed = true;
            mainView._cellEditor.OnEditModeToggled(true);
            Check(failures, mainView._patternGrid.Octave == 4, "default octave is 4");
            Check(failures, mainView._transportBar._octaveLabel.Text == "Octave: 4", "transport bar shows the default octave");

            mainView._instrumentPanel.EditInstrument(mainView.Song.Instruments[1]);
            mainView._patternGrid.SelectedRow = 2;
            mainView._patternGrid.SelectedChannel = 0;
            mainView._cellEditor._volumeSpin.Value = 9;
            var zEvent = new InputEventKey { Keycode = Key.Z, Pressed = true };
            mainView._patternGrid._Input(zEvent);
            var enteredCell = mainView._patternGrid.GetSelectedCell();
            Check(failures, enteredCell.Note == 60, $"'Z' at octave 4 records note 60 / C4 (got {enteredCell.Note})");
            Check(failures, enteredCell.InstrumentId == mainView.Song.Instruments[1].Id, "the note is stamped with the instrument currently open in the instrument panel");
            Check(failures, enteredCell.Volume == 9, "the note is stamped with the cell editor's current volume");
            Check(failures, mainView._cellEditor._noteSpin.Value == 60, "the cell editor's Note field reflects the just-entered note");

            var equalEvent = new InputEventKey { Keycode = Key.Equal, Pressed = true };
            mainView._patternGrid._Input(equalEvent);
            Check(failures, mainView._patternGrid.Octave == 5, "'=' raises the octave by 1");
            Check(failures, mainView._transportBar._octaveLabel.Text == "Octave: 5", "transport bar updates when the octave changes");
            mainView._patternGrid._Input(zEvent);
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == 72, "'Z' at octave 5 now records note 72 / C5");

            var minusEvent = new InputEventKey { Keycode = Key.Minus, Pressed = true };
            mainView._patternGrid._Input(minusEvent);
            Check(failures, mainView._patternGrid.Octave == 4, "'-' lowers the octave back down");

            // Backspace while Edit mode is on clears just the note at the
            // cursor (the cell entered above, at row 2 channel 0, is still selected here).
            var backspaceEvent = new InputEventKey { Keycode = Key.Backspace, Pressed = true };
            mainView._patternGrid._Input(backspaceEvent);
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == -1, "Backspace clears the note at the cursor while Edit mode is on");
            Check(failures, mainView._cellEditor._noteSpin.Value == -1, "the cell editor's Note field reflects the cleared note");

            // A LineEdit holding focus should keep its keystrokes -- Edit
            // mode must not hijack them.
            var guardEdit = new LineEdit();
            Root.AddChild(guardEdit);
            guardEdit.GrabFocus();
            mainView._patternGrid.SelectedRow = 3;
            mainView._patternGrid._Input(zEvent);
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == -1, "note keys are ignored while a LineEdit has focus (row 3 is empty in the demo song)");
            guardEdit.QueueFree();

            mainView._cellEditor._editModeButton.ButtonPressed = false;
            mainView._cellEditor.OnEditModeToggled(false);

            // Instrument panel edit.
            mainView._instrumentPanel.EditInstrument(mainView.Song.Instruments[0]);
            mainView._instrumentPanel._dutySlider.Value = 0.75;
            mainView._instrumentPanel.OnDutyChanged(0.75);
            Check(failures, mainView.Song.Instruments[0].DutyCycle == 0.75f, "instrument panel edit writes through to the instrument");

            // Add-instrument round trip through the dock signal.
            dock.EmitSignal(ChiptrackerDock.SignalName.AddInstrumentRequested);
            Check(failures, mainView.Song.Instruments.Count == 3, "add-instrument request grows the song's instrument list");
            var newInstrumentId = mainView.Song.Instruments[2].Id;
            Check(failures, newInstrumentId == 2, $"new instrument gets a fresh id, not a reused one (got {newInstrumentId})");

            // Remove-instrument round trip: removing a middle instrument
            // shouldn't collide with a later NextInstrumentId() call.
            dock.EmitSignal(ChiptrackerDock.SignalName.RemoveInstrumentRequested, 1); // removes the original id=1 "triangle"
            Check(failures, mainView.Song.Instruments.Count == 2, "remove-instrument request shrinks the song's instrument list");
            dock.EmitSignal(ChiptrackerDock.SignalName.AddInstrumentRequested);
            var ids = mainView.Song.Instruments.Select(i => i.Id).ToList();
            Check(failures, ids.Count(id => id == ids[^1]) == 1, $"next_instrument_id avoids colliding with a remaining id after removal (ids={string.Join(",", ids)})");

            // Duplicate-instrument round trip: deep-copies layers too,
            // independent of the original, with a fresh id and a "(Copy)" suffix.
            var toDuplicate = mainView.Song.Instruments[0];
            toDuplicate.Name = "Original";
            var layer = new Engine.Instrument { Waveform = "noise", StepOffset = 7 };
            toDuplicate.Layers.Add(layer);
            var beforeCount = mainView.Song.Instruments.Count;
            dock.EmitSignal(ChiptrackerDock.SignalName.DuplicateInstrumentRequested, 0);
            Check(failures, mainView.Song.Instruments.Count == beforeCount + 1, "duplicate-instrument request grows the song's instrument list");
            var duplicated = mainView.Song.Instruments[^1];
            Check(failures, duplicated.Id != toDuplicate.Id, "the duplicate gets a fresh id");
            Check(failures, duplicated.Name == "Original (Copy)", "and a (Copy) suffix on its name");
            Check(failures, duplicated.Layers.Count == 1 && duplicated.Layers[0] != layer, "its layer is copied too, as an independent instrument");
            duplicated.Layers[0].StepOffset = 99;
            Check(failures, layer.StepOffset == 7, "mutating the duplicate's layer doesn't affect the original's");

            // Instrument rename: exercise the commit logic directly (the
            // actual double-click gui_input needs real window input) with
            // synthetic widgets standing in for the real row.
            mainView._instrumentPanel.EditInstrument(mainView.Song.Instruments[0]);
            var instrumentRenameEdit = new LineEdit { Text = "Kick" };
            dock.CommitInstrumentRename(0, new Label(), instrumentRenameEdit, new Button());
            Check(failures, mainView.Song.Instruments[0].Name == "Kick", "instrument rename commit writes the new name");

            // Channel add/remove: row widths across every pattern must
            // track Channels.Count.
            var channelCountBefore = mainView.Song.Channels.Count;
            dock.OnAddChannelPressed();
            Check(failures, mainView.Song.Channels.Count == channelCountBefore + 1, "add-channel grows channels");
            foreach (var pattern in mainView.Song.Patterns)
            {
                foreach (var row in pattern.Rows)
                    Check(failures, row.Count == mainView.Song.Channels.Count, "every pattern row width matches channel count after add");
            }

            dock.OnRemoveChannelPressed();
            Check(failures, mainView.Song.Channels.Count == channelCountBefore, "remove-channel shrinks channels back");
            foreach (var pattern in mainView.Song.Patterns)
            {
                foreach (var row in pattern.Rows)
                    Check(failures, row.Count == mainView.Song.Channels.Count, "every pattern row width matches channel count after remove");
            }

            // Channel rename: same commit-logic exercise as instruments above.
            var channelRenameEdit = new LineEdit { Text = "Melody" };
            dock.CommitChannelRename(0, new Label(), channelRenameEdit, new Button());
            Check(failures, mainView.Song.Channels[0].Name == "Melody", "channel rename commit writes the new name");

            // Remove targets whichever row was last clicked
            // (_selectedChannelIndex), not always the last one.
            dock.OnAddChannelPressed();
            dock.OnAddChannelPressed();
            var targetIndex = mainView.Song.Channels.Count - 2;
            mainView.Song.Channels[targetIndex].Name = "SelectMe";
            dock._selectedChannelIndex = targetIndex;
            dock.OnRemoveChannelPressed();
            var remainingNames = mainView.Song.Channels.Select(c => c.Name).ToList();
            Check(failures, !remainingNames.Contains("SelectMe"), "remove-channel removes the selected index, not just the last one");

            // Pattern list: add a new pattern, add it to the order list,
            // reorder, then remove it.
            var patternCountBefore = mainView.Song.Patterns.Count;
            dock.OnAddPatternPressed();
            Check(failures, mainView.Song.Patterns.Count == patternCountBefore + 1, "add-pattern grows the pattern list");
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            Check(failures, dock._patternListContainer.GetChildCount() == mainView.Song.Patterns.Count, "dock builds one row per pattern");

            var orderCountBefore = mainView.Song.OrderList.Count;
            dock.OnAddPatternToOrder(patternCountBefore); // the newly-added pattern's index
            Check(failures, mainView.Song.OrderList.Count == orderCountBefore + 1, "add-to-order grows the order list");
            Check(failures, mainView.Song.OrderList[orderCountBefore] == patternCountBefore, "the new order entry points at the newly-added pattern");

            // Reorder: move the just-added (last) entry up one slot.
            var lastIndex = mainView.Song.OrderList.Count - 1;
            dock._selectedOrderIndex = lastIndex;
            var orderBeforeMove = new Array<int>(mainView.Song.OrderList);
            dock.OnMoveOrderUpPressed();
            Check(failures, mainView.Song.OrderList[lastIndex - 1] == orderBeforeMove[lastIndex], "move-up swaps the selected entry with the one above it");
            Check(failures, mainView.Song.OrderList[lastIndex] == orderBeforeMove[lastIndex - 1], "move-up swaps the entry that was above it into the vacated slot");

            dock._selectedOrderIndex = lastIndex - 1;
            dock.OnRemoveOrderPressed();
            Check(failures, mainView.Song.OrderList.Count == orderCountBefore, "remove shrinks the order list back");

            // Play should start from wherever the currently-viewed pattern
            // sits in the order list, not always order position 0.
            mainView.Song.OrderList.Clear();
            mainView.Song.OrderList.Add(0);
            mainView.Song.OrderList.Add(1);
            mainView._patternGrid.SetPatternIndex(1);
            mainView._patternGrid.SelectedRow = 3;
            mainView.OnPlayPressed();
            // Whichever engine Play used (cached when clean, live when
            // dirty -- this song's cache hasn't been rebuilt since this
            // mainView was created, so it's dirty here), the grid's
            // playback cursor points at it.
            Check(failures, mainView._patternGrid.PlaybackState.OrderIndex == 1, $"Play starts from the viewed pattern's order-list position (got {mainView._patternGrid.PlaybackState.OrderIndex})");
            mainView.OnStopPressed();

            // Bar preview (spacebar in Edit mode): plays just the 16-row
            // bar containing the cursor and auto-stops at its edge.
            mainView.Song.SetRowsPerPattern(32);
            mainView._patternGrid.SetPatternIndex(0);
            mainView._patternGrid.SelectedRow = 24;
            mainView.OnBarPreviewRequested();
            Check(failures, mainView._playbackEngine.State.RowIndex == 16, $"bar preview starts at row 16 for a cursor on row 24 (bar 16-31, got {mainView._playbackEngine.State.RowIndex})");
            Check(failures, mainView._playbackEngine.State.Playing, "bar preview is playing right after starting");
            mainView.OnBarPreviewRowAdvanced(0, 20, 0, 31);
            Check(failures, mainView._playbackEngine.State.Playing, "bar preview keeps playing for rows still inside the bar");
            mainView.OnBarPreviewRowAdvanced(1, 0, 0, 31);
            Check(failures, !mainView._playbackEngine.State.Playing, "bar preview stops when playback crosses into a different pattern, even though the new row_index (0) isn't past bar_end on its own");
            mainView.Song.SetRowsPerPattern(16);
            mainView._patternGrid.SelectedRow = 0;

            // FollowPattern() switches the displayed pattern without
            // resetting selection, unlike SetPatternIndex().
            mainView._patternGrid.SetPatternIndex(0);
            mainView._patternGrid.SelectedRow = 5;
            mainView._patternGrid.FollowPattern(1);
            Check(failures, mainView._patternGrid.PatternIndex == 1, "follow_pattern switches the displayed pattern");
            Check(failures, mainView._patternGrid.SelectedRow == 5, "follow_pattern preserves the selected row");

            // OnRowAdvanced should make the grid follow playback across a
            // pattern boundary, not stay fixed on whatever was last clicked.
            mainView._patternGrid.SetPatternIndex(0);
            mainView.OnRowAdvanced(1, 2); // order position 1 resolves to pattern 1
            Check(failures, mainView._patternGrid.PatternIndex == 1, "row_advanced follows playback into a different pattern");

            Finish(failures);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        void Finish(List<string> failures)
        {
            if (failures.Count == 0)
            {
                GD.Print("M6 ui_load_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M6 ui_load_test: FAIL - {f}");
                GD.PrintErr($"M6 ui_load_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}

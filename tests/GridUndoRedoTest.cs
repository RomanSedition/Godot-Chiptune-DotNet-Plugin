using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/grid_undo_redo_test.gd -- covers
    // GridEditHistory directly (headless) plus the wiring through
    // ChiptrackerMainView/PatternGrid/CellEditor: keyboard note entry,
    // Backspace-clear, CellEditor field edits, the Undo/Redo buttons,
    // and the Ctrl+Z/Ctrl+Y shortcuts.
    public partial class GridUndoRedoTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            TestHistoryBasics(failures);
            TestHistoryCap(failures);
            await TestMainViewIntegration(failures);

            if (failures.Count == 0)
            {
                GD.Print("grid_undo_redo_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"grid_undo_redo_test: FAIL - {f}");
                GD.PrintErr($"grid_undo_redo_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static Dictionary Snapshot(int note, int instrumentId, int volume) => new()
        {
            ["note"] = note,
            ["instrument_id"] = instrumentId,
            ["volume"] = volume,
        };

        void TestHistoryBasics(List<string> failures)
        {
            var history = new GridEditHistory();
            Check(failures, !history.CanUndo(), "a fresh history has nothing to undo");
            Check(failures, !history.CanRedo(), "a fresh history has nothing to redo");

            history.Push(0, 3, 1, Snapshot(-1, 0, 15), Snapshot(60, 0, 15), "row 03: note --- -> C4");
            Check(failures, history.CanUndo(), "push() makes the history undoable");
            Check(failures, !history.CanRedo(), "push() doesn't create anything to redo");

            var undoStep = history.Undo();
            Check(failures, undoStep != null, "undo() returns the pushed step");
            Check(failures, undoStep.After["note"].AsInt32() == 60, "the undone step's `after` is what was applied");
            Check(failures, !history.CanUndo(), "undoing the only step empties the undo stack");
            Check(failures, history.CanRedo(), "undoing a step makes it available to redo");

            var redoStep = history.Redo();
            Check(failures, redoStep == undoStep, "redo() returns the same step that was just undone");
            Check(failures, history.CanUndo() && !history.CanRedo(), "redoing moves the step back to the undo stack");

            // A new push after an undo should drop the abandoned redo branch.
            history.Undo();
            Check(failures, history.CanRedo(), "sanity: redo is available again before the new push");
            history.Push(0, 4, 0, Snapshot(-1, 0, 15), Snapshot(64, 0, 15), "row 04: note --- -> E4");
            Check(failures, !history.CanRedo(), "a new push discards the old redo branch");
        }

        void TestHistoryCap(List<string> failures)
        {
            var history = new GridEditHistory();
            for (var i = 0; i < GridEditHistory.MaxSteps + 10; i++)
                history.Push(0, i, 0, Snapshot(-1, 0, 15), Snapshot(i, 0, 15), $"step {i}");

            var undoneRows = new List<int>();
            while (history.CanUndo())
                undoneRows.Add(history.Undo().Row);
            Check(failures, undoneRows.Count == GridEditHistory.MaxSteps, $"history caps at MAX_STEPS undo entries (got {undoneRows.Count}, want {GridEditHistory.MaxSteps})");
            Check(failures, undoneRows[0] == GridEditHistory.MaxSteps + 9, "the most recent push is still the first thing undone after capping");
            Check(failures, undoneRows[^1] == 10, "the oldest surviving step is the 11th push (rows 0-9 were dropped when the cap was exceeded)");
        }

        async System.Threading.Tasks.Task TestMainViewIntegration(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var mainView = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(mainView);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            Check(failures, mainView._cellEditor.GetUndoButtonDisabled(), "Undo button starts disabled (no edits yet)");
            Check(failures, mainView._cellEditor.GetRedoButtonDisabled(), "Redo button starts disabled");

            mainView._cellEditor._editModeButton.ButtonPressed = true;
            mainView._cellEditor.OnEditModeToggled(true);

            // Keyboard note entry at row 5 channel 0 (empty in the demo song).
            mainView._patternGrid.SelectedRow = 5;
            mainView._patternGrid.SelectedChannel = 0;
            mainView.OnCellSelected(5, 0);
            var zEvent = new InputEventKey { Keycode = Key.Z, Pressed = true };
            mainView._patternGrid._Input(zEvent); // 'Z' at default octave 4 -> note 60 (C4)
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == 60, "keyboard note entry lands (sanity check)");
            Check(failures, !mainView._cellEditor.GetUndoButtonDisabled(), "Undo button enables after a keyboard note entry");
            // Synth.NoteName() renders natural notes as "C-4" (matches the
            // grid's own cell text), not "C4".
            Check(failures, mainView._cellEditor.GetUndoButtonTooltip().Contains("C-4"), $"Undo tooltip describes the note that would be undone (got '{mainView._cellEditor.GetUndoButtonTooltip()}')");

            // A second edit: raise the volume via the cell editor's spin box.
            mainView._cellEditor._volumeSpin.Value = 8;
            mainView._cellEditor.OnVolumeChangedNotify(8);
            Check(failures, mainView._patternGrid.GetSelectedCell().Volume == 8, "cell editor volume edit lands (sanity check)");

            // Undo the volume change (Ctrl+Z, not Cmd/meta -- must not
            // collide with the editor's own Cmd+Z).
            var ctrlZ = new InputEventKey { Keycode = Key.Z, Pressed = true, CtrlPressed = true };
            mainView._patternGrid._Input(ctrlZ);
            Check(failures, mainView._patternGrid.GetSelectedCell().Volume == 15, "Ctrl+Z undoes the volume edit (back to the demo song's default 15)");
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == 60, "undoing the volume edit doesn't touch the earlier note edit");
            Check(failures, !mainView._cellEditor.GetRedoButtonDisabled(), "Redo enables after an undo");

            // Cmd+Z (meta, not ctrl) must NOT trigger the grid undo -- it's
            // reserved for the Godot editor's own undo/redo.
            var cmdZ = new InputEventKey { Keycode = Key.Z, Pressed = true, MetaPressed = true };
            mainView._patternGrid._Input(cmdZ);
            Check(failures, mainView._patternGrid.GetSelectedCell().Volume == 15, "Cmd+Z does not trigger the grid's own undo");

            // Redo via the button (signal), not the shortcut, to exercise that path too.
            mainView._cellEditor.EmitSignal(CellEditor.SignalName.RedoRequested);
            Check(failures, mainView._patternGrid.GetSelectedCell().Volume == 8, "the Redo button reapplies the volume edit");

            // Undo everything back to the pristine demo song via the
            // keyboard, then confirm the note itself (not just volume) reverts too.
            mainView._patternGrid._Input(ctrlZ); // undo volume 8 -> 15
            mainView._patternGrid._Input(ctrlZ); // undo note --- -> C4
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == -1, "undoing back through the keyboard note entry restores the empty cell");
            Check(failures, mainView._cellEditor.GetUndoButtonDisabled(), "Undo disables once every edit has been undone");

            // Ctrl+Y redoes both steps back.
            var ctrlY = new InputEventKey { Keycode = Key.Y, Pressed = true, CtrlPressed = true };
            mainView._patternGrid._Input(ctrlY);
            mainView._patternGrid._Input(ctrlY);
            Check(failures, mainView._patternGrid.GetSelectedCell().Note == 60, "Ctrl+Y redoes the note entry");
            Check(failures, mainView._patternGrid.GetSelectedCell().Volume == 8, "Ctrl+Y redoes the volume edit");

            // Plain 'Y' (no ctrl) must still work as ordinary note entry
            // (top-row note key), not be swallowed by the Ctrl+Y redo handling.
            mainView._patternGrid.SelectedRow = 6;
            mainView._patternGrid.SelectedChannel = 0;
            mainView.OnCellSelected(6, 0);
            var yEvent = new InputEventKey { Keycode = Key.Y, Pressed = true };
            mainView._patternGrid._Input(yEvent);
            Check(failures, mainView._patternGrid.GetSelectedCell().Note >= 0, "plain 'Y' still enters a note, unaffected by the Ctrl+Y redo binding");

            TestShiftMove(failures, mainView);

            // Switching songs invalidates old undo history (it references
            // indices into a song that's no longer current).
            var freshSong = new Song { Tempo = 120, RowsPerBeat = 4, RowsPerPattern = 16 };
            freshSong.Channels.Add(new Channel());
            freshSong.Patterns.Add(new Pattern(16, 1));
            freshSong.OrderList.Add(0);
            mainView.SetSong(freshSong);
            Check(failures, mainView._cellEditor.GetUndoButtonDisabled(), "Undo disables when the song changes out from under the history");

            mainView.QueueFree();
        }

        // Shift+Up/Down at the cursor: relocates a note to an adjacent
        // vacant row, blocked dead by an adjacent occupied one, and
        // reversible via two Undos.
        void TestShiftMove(List<string> failures, ChiptrackerMainView mainView)
        {
            var pattern = mainView.Song.Patterns[mainView._patternGrid.PatternIndex];
            // Rows 9-12 on channel 1 (bass) are empty in the 16-row demo
            // song; put a note at row 9 with an occupied neighbor at row
            // 11, mirroring the bug report's "blocked by the next
            // occupied row" shape.
            var row9 = pattern.Rows[9][1].As<Cell>();
            row9.Note = 62;
            row9.Volume = 10;
            pattern.Rows[11][1].As<Cell>().Note = 64;
            mainView._patternGrid.SelectedRow = 9;
            mainView._patternGrid.SelectedChannel = 1;
            mainView.OnCellSelected(9, 1);

            var shiftDown = new InputEventKey { Keycode = Key.Down, Pressed = true, ShiftPressed = true };

            mainView._patternGrid._Input(shiftDown); // 9 -> 10 (vacant)
            Check(failures, pattern.Rows[9][1].As<Cell>().Note == -1, "Shift+Down vacates the source row");
            Check(failures, pattern.Rows[10][1].As<Cell>().Note == 62, "Shift+Down moves the note into the vacant row below");
            Check(failures, pattern.Rows[10][1].As<Cell>().Volume == 10, "the moved note keeps its volume");
            Check(failures, mainView._patternGrid.SelectedRow == 10, "the cursor follows the note to its new row");

            mainView._patternGrid._Input(shiftDown); // 10 -> 11 is occupied: blocked
            Check(failures, pattern.Rows[10][1].As<Cell>().Note == 62, "Shift+Down is blocked when the next row is already occupied");
            Check(failures, pattern.Rows[11][1].As<Cell>().Note == 64, "the blocking note at the occupied row is untouched");
            Check(failures, mainView._patternGrid.SelectedRow == 10, "the cursor stays put when the move is blocked");

            var ctrlZ = new InputEventKey { Keycode = Key.Z, Pressed = true, CtrlPressed = true };
            mainView._patternGrid._Input(ctrlZ); // undoes the 10 <- 9 half of the move
            mainView._patternGrid._Input(ctrlZ); // undoes the 9 -> empty half
            Check(failures, pattern.Rows[9][1].As<Cell>().Note == 62, "two Undos fully reverses a Shift+Down move");
            Check(failures, pattern.Rows[10][1].As<Cell>().Note == -1, "two Undos leaves the destination row empty again");
        }
    }
}

using System.Collections.Generic;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m9_record_test.gd and tests/m9_take_test.gd
    // -- covers live recording (M9): LiveRecord's nearest-row targeting,
    // GridEditHistory's take-grouping (BeginGroup/EndGroup/UndoGroup/
    // RedoGroup/labels), and how ChiptrackerMainView wires REC/count-in/
    // latency into note entry, undo/redo, and Play/Stop/bar-preview.
    //
    // NOT ported: the MIDI-specific tail of m9_record_test.gd (Akai
    // AUTOMATION-button-toggles-Record via AkaiMPKMini4/MidiKeyboard) --
    // MIDI input is its own still-deferred chunk.
    public partial class LiveRecordTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            TestTargetRow(failures);
            TestResolve(failures);
            TestHistoryGroups(failures);
            await TestRecordingInMainView(failures);
            await TestMainViewTakes(failures);

            if (failures.Count == 0)
            {
                GD.Print("live_record_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"live_record_test: FAIL - {f}");
                GD.PrintErr($"live_record_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static List<int> Rows(params int[] counts) => new(counts);

        void TestTargetRow(List<string> failures)
        {
            // One 16-row pattern, 100 samples per row: the halfway point
            // is 50, and 50 rounds up.
            var one = Rows(16);
            Check(failures, LiveRecord.Resolve(0, 3, 0, 100, one, true) == new Vector2I(0, 3), "right at the start of a row -> that row");
            Check(failures, LiveRecord.Resolve(0, 3, 49, 100, one, true) == new Vector2I(0, 3), "just under half a row in -> that row");
            Check(failures, LiveRecord.Resolve(0, 3, 50, 100, one, true) == new Vector2I(0, 4), "exactly half a row in rounds up to the next row");
            Check(failures, LiveRecord.Resolve(0, 3, 99, 100, one, true) == new Vector2I(0, 4), "almost a whole row in -> the next row");
            Check(failures, LiveRecord.Resolve(0, 3, 30, 0, one, true) == new Vector2I(0, 3), "a zero-length row never divides or rounds");
        }

        void TestResolve(List<string> failures)
        {
            // Three order entries of 16 rows each.
            var three = Rows(16, 16, 16);
            Check(failures, LiveRecord.Resolve(1, 5, 10, 100, three, true) == new Vector2I(1, 5), "mid-pattern: same entry, same row");
            Check(failures, LiveRecord.Resolve(1, 5, 60, 100, three, true) == new Vector2I(1, 6), "mid-pattern, late in the row: same entry, next row");
            Check(failures, LiveRecord.Resolve(1, 15, 10, 100, three, true) == new Vector2I(1, 15), "the last row, early in it: stays on the last row");
            Check(failures, LiveRecord.Resolve(1, 15, 60, 100, three, true) == new Vector2I(1, 0), "looping, past the end: wraps to row 0 of the same entry");
            Check(failures, LiveRecord.Resolve(1, 15, 60, 100, three, false) == new Vector2I(2, 0), "not looping, past the end: row 0 of the next entry");
            Check(failures, LiveRecord.Resolve(2, 15, 60, 100, three, false) == new Vector2I(2, 15), "not looping, past the very end: clamps to the last row");

            // Latency offset: the playhead is taken back before the row is chosen.
            Check(failures, LiveRecord.Resolve(1, 5, 10, 100, three, true, 0) == new Vector2I(1, 5), "zero offset changes nothing");
            Check(failures, LiveRecord.Resolve(1, 5, 60, 100, three, true, 20) == new Vector2I(1, 5), "an offset that pulls a late press back under half a row keeps the earlier row");
            Check(failures, LiveRecord.Resolve(1, 5, 10, 100, three, true, 100) == new Vector2I(1, 4), "an offset of a whole row goes one row earlier");
            Check(failures, LiveRecord.Resolve(1, 5, 10, 100, three, true, 250) == new Vector2I(1, 3), "a large offset goes several rows earlier");
            Check(failures, LiveRecord.Resolve(1, 0, 10, 100, three, true, 100) == new Vector2I(1, 15), "looping, an offset before row 0 wraps to the end of the same pattern");
            Check(failures, LiveRecord.Resolve(1, 0, 10, 100, three, false, 100) == new Vector2I(0, 15), "not looping, an offset before row 0 goes into the previous entry");
            Check(failures, LiveRecord.Resolve(0, 0, 10, 100, three, false, 500) == new Vector2I(0, 0), "not looping, an offset before the very start clamps to row 0");
            Check(failures, LiveRecord.Resolve(0, 15, 60, 100, three, true, 30) == new Vector2I(0, 15), "an offset can pull a press back from the wrap onto the last row");
        }

        static Dictionary Snap(int note) => new() { ["note"] = note, ["instrument_id"] = 0, ["volume"] = 15 };

        static List<int> StepRows(List<GridEditHistory.Step> steps)
        {
            var rows = new List<int>();
            foreach (var step in steps)
                rows.Add(step.Row);
            return rows;
        }

        static bool RowsEqual(List<int> a, IEnumerable<int> b)
        {
            var bList = new List<int>(b);
            if (a.Count != bList.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i] != bList[i])
                    return false;
            return true;
        }

        void TestHistoryGroups(List<string> failures)
        {
            var h = new GridEditHistory();
            h.Push(0, 0, 0, Snap(-1), Snap(60), "typed");
            h.BeginGroup();
            h.Push(0, 1, 0, Snap(-1), Snap(61), "take a");
            h.Push(0, 2, 0, Snap(-1), Snap(62), "take b");
            h.Push(0, 1, 0, Snap(61), Snap(63), "take c"); // the same cell again
            h.EndGroup();
            h.Push(0, 9, 0, Snap(-1), Snap(64), "typed after");

            Check(failures, h.PeekUndoLabel() == "typed after", "an ungrouped step is labelled with its own label");
            Check(failures, RowsEqual(StepRows(h.UndoGroup()), new[] { 9 }), "UndoGroup() on an ungrouped step undoes just that step");
            Check(failures, h.PeekUndoLabel() == "Recorded take, 3 notes", "a group is labelled with its size");
            Check(failures, RowsEqual(StepRows(h.UndoGroup()), new[] { 1, 2, 1 }), "UndoGroup() takes the whole group, newest first");
            Check(failures, h.PeekRedoLabel() == "Recorded take, 3 notes", "after undoing a take, the redo label describes the take");
            Check(failures, RowsEqual(StepRows(h.UndoGroup()), new[] { 0 }), "the step before the group is separate");
            Check(failures, h.UndoGroup().Count == 0 && !h.CanUndo(), "nothing left to undo");

            Check(failures, RowsEqual(StepRows(h.RedoGroup()), new[] { 0 }), "RedoGroup() redoes the ungrouped step");
            Check(failures, h.PeekRedoLabel() == "Recorded take, 3 notes", "the redo label counts the group");
            Check(failures, RowsEqual(StepRows(h.RedoGroup()), new[] { 1, 2, 1 }), "RedoGroup() redoes the whole group, oldest first");
            Check(failures, RowsEqual(StepRows(h.RedoGroup()), new[] { 9 }), "then the step after it");
            Check(failures, h.RedoGroup().Count == 0 && !h.CanRedo(), "nothing left to redo");

            // Two takes are two undo steps.
            var two = new GridEditHistory();
            two.BeginGroup();
            two.Push(0, 0, 0, Snap(-1), Snap(60), "a");
            two.Push(0, 1, 0, Snap(-1), Snap(61), "b");
            two.EndGroup();
            two.BeginGroup();
            two.Push(0, 2, 0, Snap(-1), Snap(62), "c");
            two.Push(0, 3, 0, Snap(-1), Snap(63), "d");
            two.EndGroup();
            Check(failures, RowsEqual(StepRows(two.UndoGroup()), new[] { 3, 2 }), "the second take undoes on its own");
            Check(failures, RowsEqual(StepRows(two.UndoGroup()), new[] { 1, 0 }), "then the first");

            // BeginGroup() while a group is open keeps the same group.
            var same = new GridEditHistory();
            same.BeginGroup();
            same.Push(0, 0, 0, Snap(-1), Snap(60), "a");
            same.BeginGroup();
            same.Push(0, 1, 0, Snap(-1), Snap(61), "b");
            Check(failures, same.HasOpenGroup(), "a group stays open across BeginGroup() calls");
            Check(failures, RowsEqual(StepRows(same.UndoGroup()), new[] { 1, 0 }), "so steps pushed across them are one group");

            // A single-step take reads as that step, and a push after undo drops redo.
            var one = new GridEditHistory();
            one.BeginGroup();
            one.Push(0, 5, 0, Snap(-1), Snap(60), "only");
            one.EndGroup();
            Check(failures, one.PeekUndoLabel() == "only", "a one-note take is labelled with that note");
            one.UndoGroup();
            one.Push(0, 6, 0, Snap(-1), Snap(61), "new");
            Check(failures, !one.CanRedo(), "a new edit still clears the redo history");

            // Clear() closes an open group.
            var cleared = new GridEditHistory();
            cleared.BeginGroup();
            cleared.Clear();
            Check(failures, !cleared.HasOpenGroup(), "Clear() closes an open group");
        }

        static Song MakeSong()
        {
            var song = new Song { Tempo = 120, RowsPerBeat = 2, RowsPerPattern = 8 };
            song.Channels.Add(new Channel());
            song.Channels.Add(new Channel());
            var instrument = new Instrument { Id = 5 };
            song.Instruments.Add(instrument);
            song.Patterns.Add(new Pattern(8, 2));
            song.Patterns.Add(new Pattern(8, 2));
            song.OrderList.Add(0);
            song.OrderList.Add(1);
            return song;
        }

        static int NoteAt(Song song, int pattern, int row, int channel) =>
            song.Patterns[pattern].Rows[row][channel].As<Cell>().Note;

        async System.Threading.Tasks.Task TestRecordingInMainView(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var view = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(view);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            var song = MakeSong();
            view.SetSong(song);
            view._recordLatencyMs = 0.0f; // the default comes from the audio driver; pin it for the test
            view._patternGrid.SelectedRow = 0;
            view._patternGrid.SelectedChannel = 1;

            // Fake a playhead: pattern 1 (order entry 1), row 4, a third of the way in.
            var cursor = new PlaybackCursor
            {
                SamplesPerRow = 100,
                OrderIndex = 1,
                RowIndex = 4,
                SamplesIntoRow = 30,
                Playing = true,
            };
            view._cachedPlaybackEngine.State = cursor;

            // Not recording: a note goes to the cursor, as always in Edit mode.
            view._cellEditor.ToggleEditMode();
            view.OnNoteKeyPressed(60);
            Check(failures, NoteAt(song, 0, 0, 1) == 60, "Edit mode, not recording: written at the cursor");
            Check(failures, NoteAt(song, 1, 4, 1) == -1, "and not at the playing row");

            // Recording: a note goes to the playing row of the playing pattern, same channel.
            view._cellEditor.ToggleRecord();
            Check(failures, view._recording, "the REC button turns recording on");
            view.OnNoteKeyPressed(64);
            Check(failures, NoteAt(song, 1, 4, 1) == 64, "recording: written at the playing row of the playing pattern");
            Check(failures, NoteAt(song, 0, 0, 1) == 60, "the cursor cell is left alone");
            Check(failures, view._patternGrid.SelectedRow == 0, "the cursor doesn't move");

            // Late in the row it rounds to the next row.
            cursor.SamplesIntoRow = 70;
            view.OnNoteKeyPressed(67);
            Check(failures, NoteAt(song, 1, 5, 1) == 67, "late in the row, the note lands on the next row");

            // The notes recorded so far are one take, so one undo removes both.
            view.OnUndoPressed();
            Check(failures, NoteAt(song, 1, 4, 1) == -1 && NoteAt(song, 1, 5, 1) == -1, "undo removes the take");
            view.OnRedoPressed();
            Check(failures, NoteAt(song, 1, 4, 1) == 64 && NoteAt(song, 1, 5, 1) == 67, "redo brings it back");

            // The latency setting pulls the note back: 2 ms is 88 samples,
            // so 30 samples into row 4 of a 100-sample row is 342 samples
            // in, which rounds to row 3.
            cursor.SamplesIntoRow = 30;
            view._recordLatencyMs = 2.0f;
            view.OnNoteKeyPressed(69);
            Check(failures, NoteAt(song, 1, 3, 1) == 69, "a latency offset moves the note to an earlier row");
            Check(failures, NoteAt(song, 1, 4, 1) == 64, "and leaves the row the playhead is on alone");
            view._recordLatencyMs = 0.0f;

            // Playback stopped: recording falls back to the cursor.
            // (Undo/redo above moved the cursor to the cell they touched:
            // pattern 1, row 5.)
            cursor.Playing = false;
            view.OnNoteKeyPressed(72);
            Check(failures, NoteAt(song, 1, 5, 1) == 72, "recording but not playing: written at the cursor");

            // Turning Edit off turns Record off.
            view._cellEditor.ToggleEditMode();
            Check(failures, !view._recording, "turning Edit mode off turns Record off");
            // Turning Record on turns Edit on.
            view._cellEditor.ToggleRecord();
            Check(failures, view._recording && view._patternGrid.EditMode, "turning Record on turns Edit mode on");

            // Space (bar preview) starts a bar preview normally, but while
            // recording it stops playback instead. Uses real playback so
            // Stop() has a player to stop.
            view._cellEditor.ToggleRecord(); // Record off (Edit stays on)
            Check(failures, !view._recording, "sanity: Record is off");
            view.OnPlayPressed();
            Check(failures, view.IsPlaying(), "sanity: Play started playback");
            view.OnStopPressed();
            view.OnBarPreviewRequested();
            Check(failures, view.IsPlaying(), "not recording: Space starts a bar preview");
            view.OnStopPressed();
            view._cellEditor.ToggleRecord(); // Record on
            view.OnPlayPressed();
            Check(failures, view.IsPlaying(), "sanity: playing while recording");
            view.OnBarPreviewRequested();
            Check(failures, !view.IsPlaying(), "recording: Space stops playback");
            view.OnBarPreviewRequested();
            Check(failures, !view.IsPlaying(), "recording: Space with nothing playing doesn't start a bar preview");
            Check(failures, view._recording, "Space doesn't turn Record off");

            view.QueueFree();
        }

        async System.Threading.Tasks.Task TestMainViewTakes(List<string> failures)
        {
            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var view = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(view);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            var song = MakeSong();
            view.SetSong(song);
            view._recordLatencyMs = 0.0f;
            view._patternGrid.SelectedChannel = 1;

            // A fake playhead in pattern 1, early in each row so no rounding happens.
            var cursor = new PlaybackCursor
            {
                SamplesPerRow = 100,
                OrderIndex = 1,
                SamplesIntoRow = 10,
                Playing = true,
            };
            view._cachedPlaybackEngine.State = cursor;
            view._cellEditor.ToggleEditMode();
            view._cellEditor.ToggleRecord();

            foreach (var row in new[] { 4, 5, 6 })
            {
                cursor.RowIndex = row;
                view.OnNoteKeyPressed(60 + row);
            }
            Check(failures, NoteAt(song, 1, 4, 1) == 64 && NoteAt(song, 1, 5, 1) == 65 && NoteAt(song, 1, 6, 1) == 66, "sanity: three notes were recorded");
            Check(failures, view._gridEditHistory.PeekUndoLabel() == "Recorded take, 3 notes", "the undo tooltip describes the take");

            view.OnUndoPressed();
            Check(failures, NoteAt(song, 1, 4, 1) == -1 && NoteAt(song, 1, 5, 1) == -1 && NoteAt(song, 1, 6, 1) == -1, "one Undo removes the whole take");
            Check(failures, !view._gridEditHistory.CanUndo(), "and that was the only undo step");
            view.OnRedoPressed();
            Check(failures, NoteAt(song, 1, 4, 1) == 64 && NoteAt(song, 1, 5, 1) == 65 && NoteAt(song, 1, 6, 1) == 66, "one Redo brings the whole take back");

            // Turning Record off ends the take; the next Record starts a new one.
            view._cellEditor.ToggleRecord();
            Check(failures, !view._gridEditHistory.HasOpenGroup(), "turning Record off closes the take");
            view._cellEditor.ToggleRecord();
            cursor.RowIndex = 7;
            view.OnNoteKeyPressed(70);
            view.OnUndoPressed();
            Check(failures, NoteAt(song, 1, 7, 1) == -1, "the second take undoes");
            Check(failures, NoteAt(song, 1, 4, 1) == 64 && NoteAt(song, 1, 5, 1) == 65, "without touching the first take");

            // A cell edit that isn't a recorded note closes the take instead of joining it.
            cursor.RowIndex = 2;
            view.OnNoteKeyPressed(62);
            Check(failures, view._gridEditHistory.HasOpenGroup(), "a recorded note opens a take");
            view.RecordCellEdit(0, 0, 0, Snap(-1), Snap(50));
            Check(failures, !view._gridEditHistory.HasOpenGroup(), "any other edit closes it");

            // Undo/redo close the take too, so a note recorded after one is not merged.
            cursor.RowIndex = 3;
            view.OnNoteKeyPressed(63);
            view.OnUndoPressed();
            Check(failures, !view._gridEditHistory.HasOpenGroup(), "Undo closes an open take");

            // Play and Stop close it. Real playback here (the position is
            // whatever it is; only the take bookkeeping is checked).
            cursor.Playing = false;
            view.OnPlayPressed();
            view.OnNoteKeyPressed(60);
            Check(failures, view._gridEditHistory.HasOpenGroup(), "sanity: recording during real playback opened a take");
            view.OnStopPressed();
            Check(failures, !view._gridEditHistory.HasOpenGroup(), "Stop closes the take");
            view.OnPlayPressed();
            view.OnNoteKeyPressed(61);
            view.OnPlayPressed();
            Check(failures, !view._gridEditHistory.HasOpenGroup(), "pressing Play again closes the take");
            view.OnStopPressed();

            view.QueueFree();
        }
    }
}

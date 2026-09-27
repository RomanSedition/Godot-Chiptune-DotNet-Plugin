using System;
using System.Collections.Generic;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/grid_edit_history.gd.
    // Capped undo/redo stack for pattern-grid cell edits (note/instrument/
    // volume), kept entirely separate from the Godot editor's own
    // undo/redo. Holds up to MaxSteps entries; pushing past that drops
    // the oldest undo step rather than growing unbounded.
    public class GridEditHistory
    {
        public event Action Changed;

        public const int MaxSteps = 100;

        // One recorded edit: which cell (by pattern/row/channel index, not
        // a direct Cell reference -- indices survive the Step outliving
        // whatever Cell object was selected at push time) changed from
        // Before to After (both Cell.Snapshot() dictionaries), plus a
        // human-readable Label for the undo/redo button tooltips.
        public class Step
        {
            public int PatternIndex;
            public int Row;
            public int Channel;
            public Dictionary Before;
            public Dictionary After;
            public string Label;

            // Non-zero when this edit belongs to a group (see
            // BeginGroup()); all the steps of one group share the id and
            // are undone/redone together.
            public int GroupId;

            public Step(int patternIndex, int row, int channel, Dictionary before, Dictionary after, string label)
            {
                PatternIndex = patternIndex;
                Row = row;
                Channel = channel;
                Before = before;
                After = after;
                Label = label;
            }
        }

        readonly List<Step> _undoStack = new();
        readonly List<Step> _redoStack = new();

        int _openGroup;
        int _nextGroupId = 1;

        // Starts a group: every Push() until EndGroup() joins it, so a
        // whole take (live recording) is one undo step via
        // UndoGroup()/RedoGroup(). Calling it while a group is already
        // open keeps the same group.
        public void BeginGroup()
        {
            if (_openGroup == 0)
            {
                _openGroup = _nextGroupId;
                _nextGroupId++;
            }
        }

        public void EndGroup() => _openGroup = 0;

        public bool HasOpenGroup() => _openGroup != 0;

        // Records a new edit. Any existing redo history is discarded --
        // same branch-cutting behavior as a normal undo/redo stack.
        public void Push(int patternIndex, int row, int channel, Dictionary before, Dictionary after, string label)
        {
            var step = new Step(patternIndex, row, channel, before, after, label) { GroupId = _openGroup };
            _undoStack.Add(step);
            if (_undoStack.Count > MaxSteps)
                _undoStack.RemoveAt(0);
            _redoStack.Clear();
            Changed?.Invoke();
        }

        public bool CanUndo() => _undoStack.Count > 0;
        public bool CanRedo() => _redoStack.Count > 0;

        // The step that would be undone/redone next, or null -- for tooltips.
        public Step PeekUndo() => CanUndo() ? _undoStack[^1] : null;
        public Step PeekRedo() => CanRedo() ? _redoStack[^1] : null;

        // Pops the most recent step onto the redo stack and returns it --
        // the caller is responsible for actually applying Step.Before to
        // the cell at (PatternIndex, Row, Channel); this class only
        // tracks the steps, not the Song they apply to.
        public Step Undo()
        {
            if (_undoStack.Count == 0)
                return null;
            var step = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add(step);
            if (_redoStack.Count > MaxSteps)
                _redoStack.RemoveAt(0);
            Changed?.Invoke();
            return step;
        }

        // Symmetric to Undo(): caller applies Step.After.
        public Step Redo()
        {
            if (_redoStack.Count == 0)
                return null;
            var step = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add(step);
            if (_undoStack.Count > MaxSteps)
                _undoStack.RemoveAt(0);
            Changed?.Invoke();
            return step;
        }

        // Like Undo(), but if the step belongs to a group, pops every
        // step of that group and returns them all, newest first -- the
        // order to revert them in, so a cell written twice in one take
        // ends up as it was before the take. Empty if there is nothing to undo.
        public List<Step> UndoGroup()
        {
            var steps = new List<Step>();
            var first = Undo();
            if (first == null)
                return steps;
            steps.Add(first);
            if (first.GroupId != 0)
            {
                while (CanUndo() && PeekUndo().GroupId == first.GroupId)
                    steps.Add(Undo());
            }
            return steps;
        }

        // Symmetric to UndoGroup(): redoes a whole group, oldest first.
        public List<Step> RedoGroup()
        {
            var steps = new List<Step>();
            var first = Redo();
            if (first == null)
                return steps;
            steps.Add(first);
            if (first.GroupId != 0)
            {
                while (CanRedo() && PeekRedo().GroupId == first.GroupId)
                    steps.Add(Redo());
            }
            return steps;
        }

        // Tooltip text for the step UndoGroup()/RedoGroup() would act on:
        // the step's own label, or a count for a group.
        public string PeekUndoLabel() => StackLabel(_undoStack);
        public string PeekRedoLabel() => StackLabel(_redoStack);

        static string StackLabel(List<Step> stack)
        {
            if (stack.Count == 0)
                return "";
            var top = stack[^1];
            if (top.GroupId == 0)
                return top.Label;
            var count = 0;
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].GroupId != top.GroupId)
                    break;
                count++;
            }
            return count == 1 ? top.Label : $"Recorded take, {count} notes";
        }

        // Called when switching to a different Song (SetSong()) -- steps
        // reference pattern/row/channel indices into whatever Song was
        // current when they were pushed, so they don't mean anything once
        // the song changes out from under them.
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _openGroup = 0;
            Changed?.Invoke();
        }
    }
}

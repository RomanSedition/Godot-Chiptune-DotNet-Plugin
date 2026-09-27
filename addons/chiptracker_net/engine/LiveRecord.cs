using System.Collections.Generic;
using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/live_record.gd. Which
    // row a note pressed during playback should be written to (M9 live
    // recording). A pure function of the playhead, so it's tested on its
    // own and the UI only has to feed in the current position.
    public static class LiveRecord
    {
        // Resolves a press to (orderIndex, row).
        //
        // `offsetSamples` is the delay to take off the playhead before
        // choosing the row: the polled position runs ahead of what is
        // heard (audio output latency), and the key event arrives after
        // the finger moved (input latency), so the note the player meant
        // is a little in the past. It may be larger than the time into
        // the current row, in which case the note goes to an earlier
        // row -- wrapping to the end of the same pattern when looping, or
        // into the previous order entry, or clamping to the start of the
        // song.
        //
        // The row is the nearest one (a press exactly half way rounds
        // up). Past the end of the pattern: looping wraps to row 0 of the
        // same entry; otherwise it is row 0 of the next entry, or the
        // last row of the final entry.
        //
        // `rowsInOrder[i]` is the row count of the pattern at order
        // entry i.
        public static Vector2I Resolve(int orderIndex, int rowIndex, int samplesIntoRow, int samplesPerRow, List<int> rowsInOrder, bool looping, int offsetSamples = 0)
        {
            if (samplesPerRow <= 0 || orderIndex < 0 || orderIndex >= rowsInOrder.Count)
                return new Vector2I(orderIndex, rowIndex);
            var order = orderIndex;
            var position = rowIndex * samplesPerRow + samplesIntoRow - offsetSamples;
            while (position < 0)
            {
                if (looping)
                {
                    var entryLength = rowsInOrder[order] * samplesPerRow;
                    position = entryLength <= 0 ? 0 : position + entryLength;
                }
                else if (order > 0)
                {
                    order -= 1;
                    position += rowsInOrder[order] * samplesPerRow;
                }
                else
                {
                    position = 0;
                }
            }
            var row = (2 * position + samplesPerRow) / (2 * samplesPerRow);
            if (row < rowsInOrder[order])
                return new Vector2I(order, row);
            if (looping)
                return new Vector2I(order, 0);
            if (order + 1 < rowsInOrder.Count)
                return new Vector2I(order + 1, 0);
            return new Vector2I(order, rowsInOrder[order] - 1);
        }
    }
}

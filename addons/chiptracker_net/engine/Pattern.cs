using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/pattern.gd.
    // [GlobalClass]: safe in this project only -- see Song.cs.
    [Tool]
    [GlobalClass]
    public partial class Pattern : Resource
    {
        // Empty means display as "Pattern N" by index; a set name shows instead.
        [Export] public string Name { get; set; } = "";

        // Rows[row][channel] -> Cell. Inner arrays are untyped (holding
        // Cell as Variant) rather than Array<Cell>, mirroring
        // pattern.gd's own Array[Array] -- a nested Array[Array[Cell]]
        // export is unreliable across Godot 4.x inspector/serialization
        // for GDScript, and there is no reason for the two implementations
        // to disagree on wire shape.
        [Export] public Array<Array> Rows { get; set; } = new();

        public Pattern() : this(0, 0) { }

        public Pattern(int rowCount, int channelCount)
        {
            if (rowCount > 0 && channelCount > 0)
                Resize(rowCount, channelCount);
        }

        public void Resize(int rowCount, int channelCount)
        {
            Rows.Clear();
            for (var r = 0; r < rowCount; r++)
            {
                var row = new Array();
                for (var c = 0; c < channelCount; c++)
                    row.Add(new Cell());
                Rows.Add(row);
            }
        }

        // Appends one empty Cell to every row, keeping row widths in sync
        // with a newly-added Song channel.
        public void AddChannel()
        {
            foreach (var row in Rows)
                row.Add(new Cell());
        }

        // Removes the cell at channelIndex from every row, keeping row
        // widths in sync with a removed Song channel.
        public void RemoveChannel(int channelIndex)
        {
            foreach (var row in Rows)
            {
                if (channelIndex >= 0 && channelIndex < row.Count)
                    row.RemoveAt(channelIndex);
            }
        }

        // Grows or shrinks row count in place, preserving existing
        // rows/cells -- unlike Resize(), which always rebuilds from scratch.
        public void SetRowCount(int newRowCount)
        {
            var channelCount = Rows.Count > 0 ? Rows[0].Count : 0;
            while (Rows.Count < newRowCount)
            {
                var row = new Array();
                for (var c = 0; c < channelCount; c++)
                    row.Add(new Cell());
                Rows.Add(row);
            }
            if (Rows.Count > newRowCount)
                Rows.Resize(newRowCount);
        }
    }
}

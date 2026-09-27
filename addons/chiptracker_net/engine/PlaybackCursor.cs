using System;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/playback_cursor.gd. Base
    // for anything that tracks "where playback currently is" for the UI --
    // the pattern grid's playhead line and the transport bar's status
    // readout both only need this shape, not the concrete machinery
    // behind it. Plain C# (no Godot base type): nothing outside this C#
    // vertical needs to observe these events via the engine's own signal
    // system, so a plain event is simpler than a Godot [Signal].
    public class PlaybackCursor
    {
        public event Action<int, int> RowAdvanced;
        public event Action Finished;

        public bool Playing;

        // True while a recording count-in is sounding, before the song
        // itself starts; the position fields below sit at the start row
        // until it ends.
        public bool CountingIn;

        public int OrderIndex;
        public int RowIndex;
        public int SamplesIntoRow;
        public int SamplesPerRow;

        // internal rather than protected: CachedPlaybackEngine drives a
        // plain PlaybackCursor from the outside (polling AudioStreamPlayer
        // position rather than subclassing it the way PlaybackState does).
        internal void EmitRowAdvanced(int orderIndex, int rowIndex) => RowAdvanced?.Invoke(orderIndex, rowIndex);
        internal void EmitFinished() => Finished?.Invoke();
    }
}

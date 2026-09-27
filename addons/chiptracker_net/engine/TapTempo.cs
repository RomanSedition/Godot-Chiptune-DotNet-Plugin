using System.Collections.Generic;
using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/tap_tempo.gd. Works out
    // a tempo from a series of taps. Feed it the time of each tap; it
    // answers with the BPM once there are at least two. Pure logic (the
    // caller supplies the clock), so it's tested on its own.
    public class TapTempo
    {
        // A gap this long between taps starts a fresh count.
        public const int ResetAfterMsec = 2000;

        // Taps closer together than this are ignored (a double trigger,
        // not a beat); it also caps the answer near the tempo field's own maximum.
        public const int MinGapMsec = 100;

        // Only the most recent taps are used, so the tempo follows a change of pace.
        public const int MaxTaps = 8;
        public const int MinTempo = 20;
        public const int MaxTempo = 400;

        readonly List<long> _taps = new();

        // Records a tap at `nowMsec` and returns the tempo in BPM, or -1
        // if there isn't enough to go on yet (the first tap, or one
        // ignored as a double trigger). The tempo is the average over the
        // recent taps, worked out from the span between the first and
        // last of them, so timing jitter on the taps in between doesn't
        // add up. Clamped to the tempo field's range.
        public int Tap(long nowMsec)
        {
            if (_taps.Count > 0)
            {
                var gap = nowMsec - _taps[^1];
                if (gap < MinGapMsec)
                    return -1;
                if (gap > ResetAfterMsec)
                    _taps.Clear();
            }
            _taps.Add(nowMsec);
            if (_taps.Count > MaxTaps)
                _taps.RemoveAt(0);
            if (_taps.Count < 2)
                return -1;
            var span = _taps[^1] - _taps[0];
            var bpm = 60000.0 * (_taps.Count - 1) / span;
            return Mathf.Clamp(Mathf.RoundToInt(bpm), MinTempo, MaxTempo);
        }

        public void Reset() => _taps.Clear();
    }
}

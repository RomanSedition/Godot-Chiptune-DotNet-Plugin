using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/instrument.gd.
    [Tool]
    public partial class Instrument : Resource
    {
        [Export] public int Id { get; set; } = 0;
        [Export] public string Name { get; set; } = ""; // empty means display as "Instrument N" by id
        [Export] public string Waveform { get; set; } = "square"; // "square", "triangle", "noise", "sine"
        [Export] public float DutyCycle { get; set; } = 0.5f;
        [Export] public Array<float> Envelope { get; set; } = new();

        // Where each envelope point sits along the note, as a fraction of
        // the note's length (0-1), one entry per envelope point and never
        // decreasing. Empty means the points are evenly spaced from 0 to 1
        // -- an instrument saved before this field existed (or made over
        // the MCP bridge without it) sounds exactly as it did. A list
        // whose length doesn't match Envelope is ignored the same way.
        [Export] public Array<float> EnvelopeTimes { get; set; } = new();

        // A pitch bend curve, in semitones (signed, 0 = the note's written
        // pitch). Empty means no pitch bend at all. Independent of
        // Envelope: its own point count and, via PitchEnvelopeTimes, its
        // own timing.
        [Export] public Array<float> PitchEnvelope { get; set; } = new();
        [Export] public Array<float> PitchEnvelopeTimes { get; set; } = new();

        // How far the pitch graph's axis reaches above and below 0
        // (semitones). Also the limit a hand-dragged point, or
        // RANDOMIZE's pitch curve, is held to.
        [Export] public int PitchRange { get; set; } = 24;
        [Export] public Array<int> Arpeggio { get; set; } = new();

        // Additional instruments that play alongside this one whenever
        // it's triggered. Layers are private to their parent: they have no
        // id of their own, never appear in the Instruments list, and can't
        // be assigned to a cell directly -- only a base instrument (one
        // that isn't itself somebody else's layer) is ever selectable that
        // way. A layer's own Layers is expected to stay empty: layering is
        // one level deep.
        [Export] public Array<Instrument> Layers { get; set; } = new();

        // Only meaningful on a layer: how many semitones above (positive)
        // or below (negative) the triggering note this layer plays at.
        // Ignored if FixedNoteEnabled is on. See ResolvedLayerNote().
        [Export] public int StepOffset { get; set; } = 0;

        // Only meaningful on a layer: when true, this layer always plays
        // FixedNote instead of the triggering note (StepOffset is then
        // ignored entirely).
        [Export] public bool FixedNoteEnabled { get; set; } = false;
        [Export] public int FixedNote { get; set; } = 60;

        // The note this layer actually plays for a given triggering note.
        public int ResolvedLayerNote(int triggeringNote) =>
            FixedNoteEnabled ? FixedNote : triggeringNote + StepOffset;

        // True when EnvelopeTimes is in use (it has one entry per envelope point).
        public bool HasCustomTimes() => Envelope.Count > 0 && EnvelopeTimes.Count == Envelope.Count;

        // Where envelope point `index` sits (0-1), whichever way times are stored.
        public float PointTime(int index)
        {
            if (HasCustomTimes())
                return EnvelopeTimes[index];
            return Envelope.Count <= 1 ? 0.0f : (float)index / (Envelope.Count - 1);
        }

        // Every point's time, evenly spaced if there are no custom ones.
        public Array<float> ResolvedTimes()
        {
            var times = new Array<float>();
            for (var i = 0; i < Envelope.Count; i++)
                times.Add(PointTime(i));
            return times;
        }

        // Moves envelope point `index` to `time` (0-1), no earlier than the
        // point before it and no later than the one after it, so the
        // points always stay in order. The first point is bounded below by
        // 0 and the last above by 1. Switches the instrument to custom
        // times if it wasn't using them. Returns the time actually stored.
        public float SetPointTime(int index, float time)
        {
            if (index < 0 || index >= Envelope.Count)
                return time;
            if (!HasCustomTimes())
                EnvelopeTimes = ResolvedTimes();
            var earliest = index > 0 ? EnvelopeTimes[index - 1] : 0.0f;
            var latest = index < EnvelopeTimes.Count - 1 ? EnvelopeTimes[index + 1] : 1.0f;
            EnvelopeTimes[index] = Mathf.Clamp(time, earliest, latest);
            return EnvelopeTimes[index];
        }

        // Appends an envelope point with the last point's value, and
        // returns its index. With custom times the new point goes at the
        // end (time 1), and if the old last point was already at 1 it
        // moves to halfway between its neighbour and 1, so no two points
        // are stacked and every one can still be grabbed.
        public int AddEnvelopePoint()
        {
            var value = Envelope.Count > 0 ? Envelope[^1] : 1.0f;
            var custom = HasCustomTimes();
            Envelope.Add(value);
            if (custom)
            {
                if (EnvelopeTimes[^1] >= 1.0f)
                {
                    var before = EnvelopeTimes.Count >= 2 ? EnvelopeTimes[^2] : 0.0f;
                    EnvelopeTimes[^1] = (before + 1.0f) / 2.0f;
                }
                EnvelopeTimes.Add(1.0f);
            }
            else if (EnvelopeTimes.Count > 0)
            {
                EnvelopeTimes.Clear(); // a stale, mismatched list: back to even spacing
            }
            return Envelope.Count - 1;
        }

        // Removes envelope point `index`, keeping the others where they are.
        public void RemoveEnvelopePoint(int index)
        {
            if (index < 0 || index >= Envelope.Count)
                return;
            var custom = HasCustomTimes();
            Envelope.RemoveAt(index);
            if (custom)
                EnvelopeTimes.RemoveAt(index);
            else if (EnvelopeTimes.Count > 0)
                EnvelopeTimes.Clear();
        }

        // The pitch envelope's equivalent of HasCustomTimes/PointTime/
        // ResolvedTimes/SetPointTime/AddEnvelopePoint/RemoveEnvelopePoint
        // -- same rules, but on PitchEnvelope/PitchEnvelopeTimes instead,
        // since the two curves are independent.

        public bool HasCustomPitchTimes() => PitchEnvelope.Count > 0 && PitchEnvelopeTimes.Count == PitchEnvelope.Count;

        public float PitchPointTime(int index)
        {
            if (HasCustomPitchTimes())
                return PitchEnvelopeTimes[index];
            return PitchEnvelope.Count <= 1 ? 0.0f : (float)index / (PitchEnvelope.Count - 1);
        }

        public Array<float> ResolvedPitchTimes()
        {
            var times = new Array<float>();
            for (var i = 0; i < PitchEnvelope.Count; i++)
                times.Add(PitchPointTime(i));
            return times;
        }

        public float SetPitchPointTime(int index, float time)
        {
            if (index < 0 || index >= PitchEnvelope.Count)
                return time;
            if (!HasCustomPitchTimes())
                PitchEnvelopeTimes = ResolvedPitchTimes();
            var earliest = index > 0 ? PitchEnvelopeTimes[index - 1] : 0.0f;
            var latest = index < PitchEnvelopeTimes.Count - 1 ? PitchEnvelopeTimes[index + 1] : 1.0f;
            PitchEnvelopeTimes[index] = Mathf.Clamp(time, earliest, latest);
            return PitchEnvelopeTimes[index];
        }

        // Appends a pitch point at 0 semitones -- unlike volume's
        // AddEnvelopePoint, which repeats the last level (holding a
        // sustain), a new pitch point starts flat rather than carrying
        // forward whatever bend was already there.
        public int AddPitchPoint()
        {
            var custom = HasCustomPitchTimes();
            PitchEnvelope.Add(0.0f);
            if (custom)
            {
                if (PitchEnvelopeTimes[^1] >= 1.0f)
                {
                    var before = PitchEnvelopeTimes.Count >= 2 ? PitchEnvelopeTimes[^2] : 0.0f;
                    PitchEnvelopeTimes[^1] = (before + 1.0f) / 2.0f;
                }
                PitchEnvelopeTimes.Add(1.0f);
            }
            else if (PitchEnvelopeTimes.Count > 0)
            {
                PitchEnvelopeTimes.Clear();
            }
            return PitchEnvelope.Count - 1;
        }

        public void RemovePitchPoint(int index)
        {
            if (index < 0 || index >= PitchEnvelope.Count)
                return;
            var custom = HasCustomPitchTimes();
            PitchEnvelope.RemoveAt(index);
            if (custom)
                PitchEnvelopeTimes.RemoveAt(index);
            else if (PitchEnvelopeTimes.Count > 0)
                PitchEnvelopeTimes.Clear();
        }
    }
}

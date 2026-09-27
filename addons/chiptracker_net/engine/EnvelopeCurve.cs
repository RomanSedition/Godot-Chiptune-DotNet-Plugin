using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // Shared return shape for EnvelopeArchetypes/PitchArchetypes generators:
    // levels (0-1 for volume, signed semitones for pitch) and times (0-1,
    // never decreasing, first at 0), one entry per envelope point.
    public readonly struct EnvelopeCurve
    {
        public readonly Array<float> Levels;
        public readonly Array<float> Times;

        public EnvelopeCurve(Array<float> levels, Array<float> times)
        {
            Levels = levels;
            Times = times;
        }
    }
}

using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/pitch_archetypes.gd.
    // Generic pitch-curve archetypes for the pitch envelope's own
    // RANDOMIZE button -- independent of EnvelopeArchetypes' twenty
    // instrument archetypes (a "chirp down" pitch shape suits a kick as
    // much as a lead, so it isn't tied to one instrument's name the way
    // the volume shapes are).
    //
    // Two kinds:
    // - One-shot shapes (Shapes): a keyframe list in semitones, same
    //   format as EnvelopeArchetypes' PitchKeys -- reuses that class's
    //   keyframe-squeeze and node-picking helpers rather than duplicating
    //   them, since the shape is generated the same way.
    // - Periodic shapes (Periodic): vibrato and trill, which don't reduce
    //   to a handful of keyframes the way a one-shot bend does --
    //   generated directly as a waveform sampled at nodeCount points instead.
    //
    // Both are clamped to +/-rangeSemitones (Instrument.PitchRange, the
    // same Range box the pitch graph's axis uses) and drawn with
    // nodeCount points (the same Nodes box the volume archetypes'
    // RANDOMIZE uses) -- there's no separate node/range control for this.
    public static class PitchArchetypes
    {
        public const int MinNodes = EnvelopeArchetypes.MinNodes;
        public const int MaxNodes = EnvelopeArchetypes.MaxNodes;

        // The periodic shapes' wobble, as a fraction of rangeSemitones --
        // widening the Range box widens the wobble too, rather than
        // needing its own depth control.
        const float PeriodicDepthFraction = 0.15f;

        // How many full cycles a vibrato/trill completes over the whole note.
        const float VibratoCycles = 3.0f;
        const float TrillCycles = 4.0f;

        // How much a periodic shape's cycle count, phase and depth wander per draw.
        const float PeriodicVariance = 0.2f;

        class Shape
        {
            public string Name;
            public (float Ms, float Semitones)[] Keys;
            public string Curve = "linear";
            public float? TimeVariance;
            public float? LevelVarianceSemitones;
        }

        class Periodic
        {
            public string Name;
            public float Delay;
            public float Cycles;
            public bool Square;
        }

        // One-shot shapes, in semitones. Curve "exp" and
        // TimeVariance/LevelVarianceSemitones mean the same as on
        // EnvelopeArchetypes' entries; LevelVarianceSemitones defaults to
        // EnvelopeArchetypes.PitchLevelVariance, same as the instrument
        // archetypes' pitch curves.
        static readonly Shape[] Shapes =
        {
            new() { Name = "Chirp Up", Keys = new[] { (0f, -12f), (80f, 0f) } },
            new() { Name = "Chirp Down", Keys = new[] { (0f, 12f), (80f, 0f) } },
            new() { Name = "Portamento In", Keys = new[] { (0f, -7f), (150f, 0f) } },
            new() { Name = "Pitch Bend Release", Keys = new[] { (0f, 2f), (350f, 2f), (500f, 0f) } },
            new() { Name = "Scoop", Keys = new[] { (0f, -4f), (80f, 1f), (200f, 0f) } },
            new() { Name = "Fall Off", Keys = new[] { (0f, 0f), (400f, 0f), (500f, -24f) } },
            new() { Name = "Overshoot", Keys = new[] { (0f, 0f), (40f, 3f), (150f, -1f), (300f, 0f) } },
            new() { Name = "Detune Drift", Keys = new[] { (0f, 0f), (150f, 0.6f), (300f, -0.5f), (450f, 0.3f), (500f, 0f) }, LevelVarianceSemitones = 0.3f },
        };

        // Periodic shapes: Delay is how much of the note (0-1) stays flat
        // before the oscillation starts (0 for a vibrato that runs the
        // whole note); Square makes it alternate hard between two levels
        // (a trill) instead of a smooth wobble.
        static readonly Periodic[] PeriodicShapes =
        {
            new() { Name = "Vibrato", Delay = 0.0f, Cycles = VibratoCycles },
            new() { Name = "Delayed Vibrato", Delay = 0.3f, Cycles = VibratoCycles },
            new() { Name = "Trill", Delay = 0.0f, Cycles = TrillCycles, Square = true },
        };

        public static int Count() => Shapes.Length + PeriodicShapes.Length;

        public static string ArchetypeName(int index)
        {
            var i = Mathf.Clamp(index, 0, Count() - 1);
            return i < Shapes.Length ? Shapes[i].Name : PeriodicShapes[i - Shapes.Length].Name;
        }

        // Makes a pitch curve of exactly `nodeCount` points (1 to
        // MaxNodes) in the shape of archetype `index`, clamped to
        // +/-rangeSemitones, with random variation drawn from `rng`.
        // Same return shape as EnvelopeArchetypes.GeneratePitch.
        public static EnvelopeCurve Generate(int index, int nodeCount, int rangeSemitones, RandomNumberGenerator rng)
        {
            var i = Mathf.Clamp(index, 0, Count() - 1);
            var rangeF = Mathf.Max(rangeSemitones, 0);
            return i < Shapes.Length
                ? GenerateShape(Shapes[i], nodeCount, rangeF, rng)
                : GeneratePeriodic(PeriodicShapes[i - Shapes.Length], nodeCount, rangeF, rng);
        }

        static EnvelopeCurve GenerateShape(Shape archetype, int nodeCount, float rangeSemitones, RandomNumberGenerator rng)
        {
            var keys = EnvelopeArchetypes.KeyframesFrom(archetype.Keys);
            var wanted = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
            var nodes = EnvelopeArchetypes.PickNodes(keys, archetype.Curve, wanted);
            var timeVariance = archetype.TimeVariance ?? EnvelopeArchetypes.DefaultTimeVariance;
            var times = EnvelopeArchetypes.JitteredTimes(nodes, timeVariance, rng);
            var levelVariance = archetype.LevelVarianceSemitones ?? EnvelopeArchetypes.PitchLevelVariance;
            var levels = new Array<float>();
            var outTimes = new Array<float>();
            for (var i = 0; i < nodes.Count; i++)
            {
                levels.Add(Mathf.Clamp(nodes[i].Y + rng.RandfRange(-levelVariance, levelVariance), -rangeSemitones, rangeSemitones));
                outTimes.Add(times[i]);
            }
            return new EnvelopeCurve(levels, outTimes);
        }

        // Samples a sine (or, "square", an alternating step -- a trill)
        // directly at `nodeCount` evenly-spaced points, rather than
        // picking nodes on a curve -- there's no fixed handful of
        // keyframes to work from the way a one-shot bend has. `Delay`
        // holds flat at 0 for that fraction of the note first.
        static EnvelopeCurve GeneratePeriodic(Periodic archetype, int nodeCount, float rangeSemitones, RandomNumberGenerator rng)
        {
            var wanted = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
            var depth = rangeSemitones * PeriodicDepthFraction * (1.0f + rng.RandfRange(-PeriodicVariance, PeriodicVariance));
            var cycles = archetype.Cycles * (1.0f + rng.RandfRange(-PeriodicVariance, PeriodicVariance));
            var delay = archetype.Delay;
            var square = archetype.Square;
            var phase = rng.RandfRange(0.0f, Mathf.Tau);
            var levels = new Array<float>();
            var times = new Array<float>();
            for (var i = 0; i < wanted; i++)
            {
                var t = wanted <= 1 ? 0.0f : (float)i / (wanted - 1);
                times.Add(t);
                if (t < delay)
                {
                    levels.Add(0.0f);
                    continue;
                }
                var active = (t - delay) / Mathf.Max(1.0f - delay, 0.0001f);
                var wave = Mathf.Sin(active * cycles * Mathf.Tau + phase);
                if (square)
                    wave = wave >= 0.0f ? 1.0f : -1.0f;
                levels.Add(Mathf.Clamp(wave * depth, -rangeSemitones, rangeSemitones));
            }
            return new EnvelopeCurve(levels, times);
        }
    }
}

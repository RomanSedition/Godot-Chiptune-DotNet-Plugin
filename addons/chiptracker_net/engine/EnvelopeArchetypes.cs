using System;
using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/envelope_archetypes.gd.
    // The twenty envelope archetypes of CommonEnvelopes.md, and a
    // generator that makes a slightly different envelope of a chosen
    // number of points in the shape of one of them each time it is asked.
    //
    // Mainly the volume envelope (levels and their times, see
    // Instrument.Envelope/EnvelopeTimes). A handful of archetypes -- the
    // ones whose character is a pitch chirp or slide rather than
    // modulation -- also carry a pitch curve (Instrument.PitchEnvelope/
    // PitchEnvelopeTimes, semitones signed around 0), see
    // HasPitch()/GeneratePitch(). The rest of each archetype's
    // description (vibrato, pulse-width swaps, arpeggios) is periodic
    // modulation or note-sequencing, not a one-shot curve, and still
    // isn't something an instrument can express, so it's not part of this.
    //
    // Timing: the archetypes are described in milliseconds; envelope
    // times are fractions of the note's length. A note is taken to be
    // ReferenceMsec long, so a 150ms decay becomes 0.3. A shape longer
    // than that (the crash, the music box, the pad) is squeezed evenly to
    // fit, and one that is shorter simply holds its last level for the
    // rest of the note. The notes have no key-release, so an archetype's
    // release is written into its shape as the tail at the end.
    public static class EnvelopeArchetypes
    {
        public const float ReferenceMsec = 500.0f;
        public const int MinNodes = 1;

        // 60 because the consoles being imitated step an envelope once
        // per video frame, 60 times a second: one point a frame is the
        // finest change they could make, so a second-long note tops out
        // at 60 and more points would add nothing audible. (A hand-drawn
        // envelope can still have more.)
        public const int MaxNodes = 60;

        // How far the times and levels may wander (as a fraction: 0.15 is up to 15%).
        public const float DefaultTimeVariance = 0.15f;
        public const float DefaultLevelVariance = 0.15f;

        // How sharply an "exp" decay drops at first (larger is steeper).
        public const float ExpSteepness = 4.0f;

        // Each point's own sideways wander is this fraction of the time
        // variance, so it adds only a little to the stretch of the whole
        // shape (together they stay within about 20% for a typical shape).
        public const float NodeWander = 0.5f;

        // How far a pitch point may wander, in semitones -- absolute
        // rather than a proportion of the value, since a pitch point
        // often sits at exactly 0 and a proportional wander could never
        // move it.
        public const float PitchLevelVariance = 1.0f;

        internal class Archetype
        {
            public string Name;
            public (float Ms, float Level)[] Keys;
            public (float Ms, float Level)[] PitchKeys;
            public string Curve = "linear";
            public string PitchCurve = "linear";
            public float? LevelVariance;
            public float? TimeVariance;
        }

        // Each archetype: its name, and its shape as [time in ms, level]
        // keyframes. Two keyframes at the same time make a hard step.
        // Curve "exp" makes the falling segments exponential instead of
        // straight. LevelVariance/TimeVariance override the defaults
        // where the archetype must stay steady. PitchKeys, where present,
        // is the same kind of keyframe list but in semitones signed
        // around 0 -- see HasPitch().
        internal static readonly Archetype[] Archetypes =
        {
            new() { Name = "Kick: Standard 8-Bit Kick", Keys = new[] { (0f, 1.0f), (150f, 0.0f) },
                PitchKeys = new[] { (0f, 24f), (16.7f, 12f), (33.3f, 0f) } },
            new() { Name = "Kick: Laser Kick", Keys = new[] { (0f, 1.0f), (120f, 0.0f) },
                PitchKeys = new[] { (0f, 48f), (120f, -24f) }, PitchCurve = "exp" },
            new() { Name = "Kick: Heavy Gated Atari Kick", Keys = new[] { (0f, 1.0f), (30f, 0.6f), (80f, 0.6f), (80f, 0.0f) },
                PitchKeys = new[] { (0f, 12f), (33.3f, 0f) } },
            new() { Name = "Snare: Standard Noise Snare", Keys = new[] { (0f, 1.0f), (250f, 0.0f) } },
            new() { Name = "Snare: Metallic Deep Snare", Keys = new[] { (0f, 1.0f), (200f, 0.0f) } },
            new() { Name = "Snare: Explosion / Impact Snare", Keys = new[] { (0f, 1.0f), (500f, 0.0f) } },
            new() { Name = "Hi-Hat: Closed Hi-Hat", Keys = new[] { (0f, 1.0f), (30f, 0.0f) } },
            new() { Name = "Hi-Hat: Open Hi-Hat", Keys = new[] { (0f, 1.0f), (150f, 0.0f) } },
            new() { Name = "Cymbal: Crash Cymbal", Keys = new[] { (0f, 1.0f), (100f, 0.25f), (1500f, 0.0f) } },
            new() { Name = "Percussion: 8-Bit Tom-Drum", Keys = new[] { (0f, 1.0f), (200f, 0.0f) },
                PitchKeys = new[] { (0f, 0f), (200f, -12f) } },
            new() { Name = "Lead: Mega Duty-Swap Lead", Keys = new[] { (0f, 1.0f), (100f, 0.85f), (450f, 0.85f), (500f, 0.0f) } },
            new() { Name = "Lead: Expressive Theremin / Violin", Keys = new[] { (0f, 0.0f), (150f, 1.0f), (550f, 0.0f) } },
            // The echo: the note, then "ghost notes" three frames (50ms at
            // 60Hz) later at 0.4 and three frames after that at 0.15, each
            // a step up and a quick fade.
            new() { Name = "Lead: Echo / Delay Lead", Keys = new[] { (0f, 1.0f), (50f, 0.0f), (50f, 0.4f), (100f, 0.0f), (100f, 0.15f), (150f, 0.0f) } },
            new() { Name = "Bass: Snappy Slap Bass", Keys = new[] { (0f, 1.0f), (80f, 0.3f), (500f, 0.3f) },
                PitchKeys = new[] { (0f, 12f), (16.7f, 12f), (16.7f, 0f) } },
            new() { Name = "Bass: Heavy Drive Bass", Keys = new[] { (0f, 1.0f), (500f, 1.0f) }, LevelVariance = 0.03f },
            new() { Name = "Bass: Rubber Bass", Keys = new[] { (0f, 0.0f), (50f, 1.0f), (200f, 0.6f), (400f, 0.6f), (500f, 0.0f) },
                PitchKeys = new[] { (0f, -2f), (50f, 0f) } },
            new() { Name = "Pluck: Chiptune Arpeggio Chord", Keys = new[] { (0f, 1.0f), (400f, 1.0f), (500f, 0.0f) } },
            new() { Name = "Pluck: Tiny 8-Bit Music Box", Keys = new[] { (0f, 1.0f), (800f, 0.0f) }, Curve = "exp" },
            new() { Name = "Pad: Soft Chiptune Pad", Keys = new[] { (0f, 0.0f), (400f, 1.0f), (900f, 0.0f) } },
            new() { Name = "FX: Coin / Power-Up Arpeggio", Keys = new[] { (0f, 1.0f), (300f, 0.0f) },
                PitchKeys = new[] { (0f, 0f), (83f, 0f), (83f, 12f) } },
        };

        public static int Count() => Archetypes.Length;

        // The category before the colon in an archetype's name ("Kick", "Lead"...).
        public static string Category(int index) => ArchetypeName(index).Split(":")[0];

        // The waveform that suits the archetype, from CommonEnvelopes.md:
        // noise for snares, hats and cymbals, triangle for kicks, toms,
        // basses and pads, and square for the rest.
        public static string DefaultWaveform(int index) => Category(index) switch
        {
            "Snare" or "Hi-Hat" or "Cymbal" => "noise",
            "Kick" or "Percussion" or "Bass" or "Pad" => "triangle",
            _ => "square",
        };

        // The index of the archetype whose name contains `query`
        // (case-insensitive), or -1. A number in text ("3") is taken as an index.
        public static int Find(string query)
        {
            var wanted = query.Trim().ToLower();
            if (wanted.Length == 0)
                return -1;
            if (int.TryParse(wanted, out var index))
                return index >= 0 && index < Count() ? index : -1;
            for (var i = 0; i < Count(); i++)
            {
                if (ArchetypeName(i).ToLower() == wanted)
                    return i;
            }
            for (var i = 0; i < Count(); i++)
            {
                if (ArchetypeName(i).ToLower().Contains(wanted))
                    return i;
            }
            return -1;
        }

        public static string ArchetypeName(int index) => Archetypes[Mathf.Clamp(index, 0, Count() - 1)].Name;

        // Whether archetype `index` has a pitch curve for GeneratePitch()
        // to draw on -- most don't (their character is noise, a flat
        // pitch, or a kind of modulation no envelope can express).
        public static bool HasPitch(int index) => Archetypes[Mathf.Clamp(index, 0, Count() - 1)].PitchKeys != null;

        // Makes an envelope of exactly `nodeCount` points (1 to MaxNodes)
        // in the shape of archetype `index`, with random variation drawn
        // from `rng` (so the same archetype and count give a different
        // result each time, and the same rng seed gives the same one).
        //
        // With more points than the shape has keyframes, the extra points
        // are added on the shape's curve, always splitting the widest
        // gap. With fewer, the keyframes that matter least (the ones
        // closest to a straight line between their neighbours) are
        // dropped first, so the ends and the corners stay.
        public static EnvelopeCurve Generate(int index, int nodeCount, RandomNumberGenerator rng)
        {
            var archetype = Archetypes[Mathf.Clamp(index, 0, Count() - 1)];
            var keys = KeyframesFrom(archetype.Keys);
            var wanted = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
            var nodes = PickNodes(keys, archetype.Curve, wanted);
            Vary(nodes, archetype, rng);
            var levels = new Array<float>();
            var times = new Array<float>();
            foreach (var node in nodes)
            {
                times.Add(node.X);
                levels.Add(node.Y);
            }
            return new EnvelopeCurve(levels, times);
        }

        // Like Generate(), but for the pitch curve (semitones, signed,
        // clamped to +/-rangeSemitones) of an archetype that has one --
        // see HasPitch(). Returns empty levels/times for one that doesn't.
        public static EnvelopeCurve GeneratePitch(int index, int nodeCount, int rangeSemitones, RandomNumberGenerator rng)
        {
            var archetype = Archetypes[Mathf.Clamp(index, 0, Count() - 1)];
            if (archetype.PitchKeys == null)
                return new EnvelopeCurve(new Array<float>(), new Array<float>());
            var keys = KeyframesFrom(archetype.PitchKeys);
            var wanted = Mathf.Clamp(nodeCount, MinNodes, MaxNodes);
            var nodes = PickNodes(keys, archetype.PitchCurve, wanted);
            VaryPitch(nodes, archetype, rng, Mathf.Max(rangeSemitones, 0));
            var levels = new Array<float>();
            var times = new Array<float>();
            foreach (var node in nodes)
            {
                times.Add(node.X);
                levels.Add(node.Y);
            }
            return new EnvelopeCurve(levels, times);
        }

        // A raw [time in ms, level] keyframe list (an archetype's Keys or
        // PitchKeys) as Vector2(time 0-1, level), squeezed to fit the
        // reference note if the shape is longer than it.
        internal static Vector2[] KeyframesFrom((float Ms, float Level)[] raw)
        {
            var totalMsec = raw[^1].Ms;
            var squeeze = totalMsec > 0.0f ? Mathf.Min(1.0f, ReferenceMsec / totalMsec) : 1.0f;
            var keys = new Vector2[raw.Length];
            for (var i = 0; i < raw.Length; i++)
                keys[i] = new Vector2(raw[i].Ms * squeeze / ReferenceMsec, raw[i].Level);
            return keys;
        }

        // The level of the shape at `time`, holding the last level after its end.
        static float LevelAt(Vector2[] keys, string curve, float time)
        {
            for (var i = 0; i < keys.Length - 1; i++)
            {
                var from = keys[i];
                var to = keys[i + 1];
                if (time <= to.X)
                {
                    var span = to.X - from.X;
                    if (span <= 0.0f)
                        return to.Y;
                    var along = (time - from.X) / span;
                    if (curve == "exp" && to.Y < from.Y)
                    {
                        var drop = (Mathf.Exp(-ExpSteepness * along) - Mathf.Exp(-ExpSteepness)) / (1.0f - Mathf.Exp(-ExpSteepness));
                        return to.Y + (from.Y - to.Y) * drop;
                    }
                    return Mathf.Lerp(from.Y, to.Y, along);
                }
            }
            return keys[^1].Y;
        }

        internal static System.Collections.Generic.List<Vector2> PickNodes(Vector2[] keys, string curve, int wanted)
        {
            if (wanted == 1)
            {
                var peak = 0.0f;
                foreach (var key in keys)
                    peak = Mathf.Max(peak, key.Y);
                return new System.Collections.Generic.List<Vector2> { new(0.0f, peak) };
            }
            var nodes = new System.Collections.Generic.List<Vector2>(keys);
            while (nodes.Count < wanted)
            {
                var widest = -1;
                var widestGap = 0.0f;
                for (var i = 0; i < nodes.Count - 1; i++)
                {
                    var gap = nodes[i + 1].X - nodes[i].X;
                    if (gap > widestGap)
                    {
                        widestGap = gap;
                        widest = i;
                    }
                }
                if (widest == -1)
                {
                    nodes.Add(nodes[^1]); // nothing left to split: repeat the last point
                    continue;
                }
                var middle = (nodes[widest].X + nodes[widest + 1].X) / 2.0f;
                nodes.Insert(widest + 1, new Vector2(middle, LevelAt(keys, curve, middle)));
            }
            while (nodes.Count > wanted)
            {
                var least = 1;
                var leastArea = float.PositiveInfinity;
                for (var i = 1; i < nodes.Count - 1; i++)
                {
                    var a = nodes[i - 1];
                    var b = nodes[i];
                    var c = nodes[i + 1];
                    var area = Mathf.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2.0f;
                    if (area < leastArea)
                    {
                        leastArea = area;
                        least = i;
                    }
                }
                nodes.RemoveAt(least);
            }
            return nodes;
        }

        // The nodes' times, stretched a little in time (never past the
        // end of the note) and each wandered a little within the gap to
        // its neighbours (so the order can't break, and a step stays a
        // step) -- shared by Vary (volume, levels 0-1) and VaryPitch
        // (pitch, signed semitones), which only differ in how the level
        // itself is varied.
        internal static float[] JitteredTimes(System.Collections.Generic.List<Vector2> nodes, float timeVariance, RandomNumberGenerator rng)
        {
            var stretch = 1.0f + rng.RandfRange(-timeVariance, timeVariance);
            if (nodes[^1].X > 0.0f)
                stretch = Mathf.Min(stretch, 1.0f / nodes[^1].X);
            var stretched = new float[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
                stretched[i] = nodes[i].X * stretch;
            var times = new float[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                var time = stretched[i];
                if (i > 0)
                {
                    var later = i < nodes.Count - 1 ? stretched[i + 1] : 1.0f;
                    var room = Mathf.Min(stretched[i] - stretched[i - 1], later - stretched[i]);
                    time = stretched[i] + rng.RandfRange(-timeVariance, timeVariance) * NodeWander * Mathf.Max(room, 0.0f);
                    time = Mathf.Max(time, times[i - 1]);
                }
                times[i] = Mathf.Clamp(time, 0.0f, 1.0f);
            }
            return times;
        }

        // Adds the random variation to a volume shape, in place: times
        // per JitteredTimes, and each level changes by a proportion of
        // itself (so silence stays silent and nothing passes 1).
        static void Vary(System.Collections.Generic.List<Vector2> nodes, Archetype archetype, RandomNumberGenerator rng)
        {
            var timeVariance = archetype.TimeVariance ?? DefaultTimeVariance;
            var levelVariance = archetype.LevelVariance ?? DefaultLevelVariance;
            var times = JitteredTimes(nodes, timeVariance, rng);
            for (var i = 0; i < nodes.Count; i++)
            {
                var level = Mathf.Clamp(nodes[i].Y * (1.0f + rng.RandfRange(-levelVariance, levelVariance)), 0.0f, 1.0f);
                nodes[i] = new Vector2(times[i], level);
            }
        }

        // Adds the random variation to a pitch shape, in place: times per
        // JitteredTimes, and each level (a signed semitone offset)
        // wanders by a fixed amount rather than a proportion of itself,
        // since a pitch point often sits at exactly 0 and a proportional
        // wander could never move it.
        static void VaryPitch(System.Collections.Generic.List<Vector2> nodes, Archetype archetype, RandomNumberGenerator rng, float rangeSemitones)
        {
            var timeVariance = archetype.TimeVariance ?? DefaultTimeVariance;
            var times = JitteredTimes(nodes, timeVariance, rng);
            for (var i = 0; i < nodes.Count; i++)
            {
                var level = Mathf.Clamp(nodes[i].Y + rng.RandfRange(-PitchLevelVariance, PitchLevelVariance), -rangeSemitones, rangeSemitones);
                nodes[i] = new Vector2(times[i], level);
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/envelope_archetypes_test.gd -- covers the
    // envelope archetype generator (EnvelopeArchetypes): exact node
    // counts, valid ordering, staying in the archetype's shape, random
    // variance, determinism for a given seed, and the synth's
    // binary-search lookup.
    //
    // NOT ported: the reference file's _test_panel (the instrument
    // panel's archetype drop-down / RANDOMIZE button) -- that's UI
    // wiring that lands with M6, same reason instrument_panel.gd's
    // envelope/pitch/layer editing wasn't ported in the earlier cosmetic
    // UI pass.
    //
    // Run headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/EnvelopeArchetypesTest.cs
    public partial class EnvelopeArchetypesTest : SceneTree
    {
        static readonly int[] Counts = { 1, 2, 3, 4, 6, 8, 16, 32, 60 };

        public override void _Initialize()
        {
            var failures = new List<string>();

            TestList(failures);
            TestGeneratedEnvelopesAreValid(failures);
            TestShapes(failures);
            TestVariance(failures);
            TestSynthLookup(failures);

            if (failures.Count == 0)
            {
                GD.Print("envelope_archetypes_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"envelope_archetypes_test: FAIL - {f}");
                GD.PrintErr($"envelope_archetypes_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static RandomNumberGenerator Rng(int seedValue) => new() { Seed = (ulong)seedValue };

        static bool ArraysEqual(Array<float> a, Array<float> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        void TestList(List<string> failures)
        {
            Check(failures, EnvelopeArchetypes.Count() == 20, "there are twenty archetypes");
            var seen = new HashSet<string>();
            var allNamed = true;
            for (var i = 0; i < EnvelopeArchetypes.Count(); i++)
            {
                var name = EnvelopeArchetypes.ArchetypeName(i);
                if (string.IsNullOrEmpty(name) || seen.Contains(name))
                    allNamed = false;
                seen.Add(name);
            }
            Check(failures, allNamed, "every archetype has its own name");
        }

        void TestGeneratedEnvelopesAreValid(List<string> failures)
        {
            var allValid = true;
            var bad = "";
            for (var archetype = 0; archetype < EnvelopeArchetypes.Count(); archetype++)
            {
                foreach (var wanted in Counts)
                {
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        var result = EnvelopeArchetypes.Generate(archetype, wanted, Rng(archetype * 1000 + wanted * 10 + attempt));
                        var levels = result.Levels;
                        var times = result.Times;
                        var problem = "";
                        if (levels.Count != wanted || times.Count != wanted)
                        {
                            problem = $"count {levels.Count}/{times.Count}";
                        }
                        else if (!Mathf.IsEqualApprox(times[0], 0.0f))
                        {
                            problem = $"first time {times[0]}";
                        }
                        else
                        {
                            for (var i = 0; i < wanted; i++)
                            {
                                if (levels[i] < 0.0f || levels[i] > 1.0f || float.IsNaN(levels[i]))
                                    problem = $"level {levels[i]}";
                                if (times[i] < 0.0f || times[i] > 1.0f || float.IsNaN(times[i]))
                                    problem = $"time {times[i]}";
                                if (i > 0 && times[i] < times[i - 1])
                                    problem = $"time {times[i]} before {times[i - 1]}";
                            }
                        }
                        if (problem != "")
                        {
                            allValid = false;
                            bad = $"{EnvelopeArchetypes.ArchetypeName(archetype)} x{wanted}: {problem}";
                        }
                    }
                }
            }
            Check(failures, allValid, $"every archetype at every count is valid (exact size, times in order within 0-1, levels within 0-1); first bad: {bad}");
            // Counts outside 1-60 are held to the range.
            Check(failures, EnvelopeArchetypes.Generate(0, 0, Rng(1)).Levels.Count == 1, "a count of 0 is held to 1");
            Check(failures, EnvelopeArchetypes.Generate(0, 999, Rng(1)).Levels.Count == 60, "a count above 60 is held to 60");
        }

        void TestShapes(List<string> failures)
        {
            // A plain decay: starts loud, ends silent, and is over quickly.
            var kick = EnvelopeArchetypes.Generate(0, 8, Rng(5));
            Check(failures, kick.Levels[0] >= 0.75f && kick.Levels[^1] == 0.0f, "the standard kick starts loud and ends silent");
            Check(failures, kick.Times[^1] <= 0.38f, "and its 150 ms decay is done within about a third of the note");
            var falling = true;
            for (var i = 1; i < kick.Levels.Count; i++)
            {
                if (kick.Levels[i] > kick.Levels[i - 1] + 0.35f)
                    falling = false;
            }
            Check(failures, falling, "a decay doesn't climb back up");

            var closed = EnvelopeArchetypes.Generate(6, 4, Rng(5));
            var open = EnvelopeArchetypes.Generate(7, 4, Rng(5));
            Check(failures, closed.Times[^1] < open.Times[^1], "a closed hi-hat is shorter than an open one");
            Check(failures, closed.Times[^1] <= 0.08f, "the closed hi-hat is over almost at once (30 ms)");

            // The gated Atari kick: a step at the end, two points at the same time.
            var gated = EnvelopeArchetypes.Generate(2, 4, Rng(9));
            Check(failures, Mathf.IsEqualApprox(gated.Times[2], gated.Times[3]) && gated.Levels[2] > 0.3f && gated.Levels[3] == 0.0f, "the gated kick holds its level then cuts in a step");

            // The echo lead: the two ghost notes are bumps that come back up.
            var echo = EnvelopeArchetypes.Generate(12, 6, Rng(3));
            Check(failures, echo.Levels[2] > echo.Levels[1] && echo.Levels[4] > echo.Levels[3], "the echo lead has two ghost notes rising after each fade");
            Check(failures, echo.Levels[0] > echo.Levels[2] && echo.Levels[2] > echo.Levels[4], "and each is quieter than the one before");

            // Swells start from silence.
            var theremin = EnvelopeArchetypes.Generate(11, 8, Rng(4));
            Check(failures, theremin.Levels[0] == 0.0f && theremin.Levels.Max() >= 0.75f, "the theremin swells up from silence");
            var pad = EnvelopeArchetypes.Generate(18, 8, Rng(4));
            Check(failures, pad.Levels[0] == 0.0f && pad.Levels.Max() >= 0.75f, "the pad fades in from silence");

            // A steady bass stays steady.
            var drive = EnvelopeArchetypes.Generate(14, 8, Rng(6));
            Check(failures, drive.Levels.Min() >= 0.96f, "the heavy drive bass stays flat");

            // Shapes longer than the reference note are squeezed to fit inside it.
            var crash = EnvelopeArchetypes.Generate(8, 8, Rng(7));
            Check(failures, crash.Times[^1] <= 1.0f && crash.Times[^1] > 0.6f, "the crash's 1.5 second tail is squeezed to fit the note, not cut off");
            var musicBox = EnvelopeArchetypes.Generate(17, 16, Rng(7));
            Check(failures, musicBox.Times[^1] > 0.6f && musicBox.Levels[^1] == 0.0f, "the music box's 800 ms decay is squeezed to fit");
            var middleLevel = 1.0f;
            for (var i = 0; i < musicBox.Times.Count; i++)
            {
                if (musicBox.Times[i] >= musicBox.Times[^1] * 0.5f)
                {
                    middleLevel = musicBox.Levels[i];
                    break;
                }
            }
            Check(failures, middleLevel < 0.5f, "the music box's decay is exponential, so it is well below half by the middle");

            // One point: a flat level at the shape's loudest.
            var single = EnvelopeArchetypes.Generate(0, 1, Rng(8));
            Check(failures, single.Times.Count == 1 && Mathf.IsEqualApprox(single.Times[0], 0.0f) && single.Levels[0] >= 0.75f, "one point is a single steady level near the peak");
            // Two points: the ends of the shape.
            var pair = EnvelopeArchetypes.Generate(0, 2, Rng(8));
            Check(failures, pair.Levels[0] >= 0.75f && pair.Levels[1] == 0.0f, "two points keep the loud start and the silent end");
            // Few points keep the corners: the slap bass's drop, then its sustain.
            var slap = EnvelopeArchetypes.Generate(13, 3, Rng(8));
            Check(failures, slap.Levels[0] >= 0.75f && slap.Levels[1] < 0.5f && slap.Levels[2] < 0.5f, "three points of the slap bass keep the pluck and then the low sustain");

            // Many points follow the curve, not just a straight line.
            var many = EnvelopeArchetypes.Generate(17, 64, Rng(11));
            var monotone = true;
            for (var i = 1; i < many.Levels.Count; i++)
            {
                if (many.Levels[i] > many.Levels[i - 1] + 0.3f)
                    monotone = false;
            }
            Check(failures, monotone, "a 64-point decay is a smooth fall");
        }

        void TestVariance(List<string> failures)
        {
            // Same archetype, same count, different results.
            var different = 0;
            for (var archetype = 0; archetype < EnvelopeArchetypes.Count(); archetype++)
            {
                var a = EnvelopeArchetypes.Generate(archetype, 8, Rng(100));
                var b = EnvelopeArchetypes.Generate(archetype, 8, Rng(101));
                if (!ArraysEqual(a.Levels, b.Levels) || !ArraysEqual(a.Times, b.Times))
                    different++;
            }
            Check(failures, different == EnvelopeArchetypes.Count(), $"every archetype gives a different result for a different random draw ({different} of 20)");

            // The same seed gives the same result.
            var sameA = EnvelopeArchetypes.Generate(3, 8, Rng(42));
            var sameB = EnvelopeArchetypes.Generate(3, 8, Rng(42));
            Check(failures, ArraysEqual(sameA.Levels, sameB.Levels) && ArraysEqual(sameA.Times, sameB.Times), "the same seed reproduces the same envelope");

            // The default random generator (unseeded) varies from click to click.
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            var first = EnvelopeArchetypes.Generate(5, 10, rng);
            var second = EnvelopeArchetypes.Generate(5, 10, rng);
            Check(failures, !ArraysEqual(first.Levels, second.Levels), "two clicks in a row don't give the same envelope");

            // The variance stays modest: the peak and the end stay where the archetype puts them.
            var stays = true;
            for (var seedValue = 0; seedValue < 50; seedValue++)
            {
                var r = EnvelopeArchetypes.Generate(0, 4, Rng(seedValue));
                if (r.Levels[0] < 0.8f || r.Levels[^1] != 0.0f || r.Times[^1] > 0.38f || r.Times[^1] < 0.23f)
                    stays = false;
            }
            Check(failures, stays, "fifty draws of the standard kick all stay a kick (peak at least 0.8, silent end, decay 0.23 to 0.38)");
        }

        // The straightforward version of Synth.EnvelopeValue's custom-times
        // branch, to hold the binary search to.
        static float ReferenceValue(Array<float> envelope, Array<float> times, float progress)
        {
            var last = envelope.Count - 1;
            if (progress < times[0])
                return envelope[0];
            if (progress >= times[last])
                return envelope[last];
            for (var i = 0; i < last; i++)
            {
                if (progress < times[i + 1])
                {
                    var span = times[i + 1] - times[i];
                    if (span <= 0.0f)
                        return envelope[i];
                    return Mathf.Lerp(envelope[i], envelope[i + 1], (progress - times[i]) / span);
                }
            }
            return envelope[last];
        }

        void TestSynthLookup(List<string> failures)
        {
            var rng = Rng(77);
            var matches = true;
            for (var roundIndex = 0; roundIndex < 200; roundIndex++)
            {
                var size = rng.RandiRange(2, 60);
                var times = new Array<float>();
                var levels = new Array<float>();
                var t = 0.0f;
                for (var i = 0; i < size; i++)
                {
                    // Includes steps (repeated times) and starts away from 0.
                    t = Mathf.Clamp(t + (rng.Randf() < 0.15f ? 0.0f : rng.Randf() * 0.05f), 0.0f, 1.0f);
                    times.Add(t);
                    levels.Add(rng.Randf());
                }
                for (var probe = 0; probe < 25; probe++)
                {
                    var progress = rng.Randf();
                    var expected = ReferenceValue(levels, times, progress);
                    var actual = Synth.EnvelopeValue(levels, progress, 1.0f, times);
                    if (Mathf.Abs(expected - actual) > 0.00001f)
                        matches = false;
                }
            }
            Check(failures, matches, "the binary-search lookup gives the same answer as a plain scan (200 random envelopes with steps)");

            // A hand-drawn envelope can have far more points than RANDOMIZE
            // makes, and must still render quickly: 255 evenly spread
            // points, a slow fall.
            var bigLevels = new Array<float>();
            var bigTimes = new Array<float>();
            for (var i = 0; i < 255; i++)
            {
                bigTimes.Add(i / 254.0f);
                bigLevels.Add(1.0f - i / 254.0f);
            }
            var instrument = new Instrument { Waveform = "square", Envelope = bigLevels, EnvelopeTimes = bigTimes };
            var started = Time.GetTicksMsec();
            var buffer = Synth.GenerateBuffer(instrument, 60, 0.5f, 44100);
            var elapsed = Time.GetTicksMsec() - started;
            Check(failures, buffer.Length == 22050, "a 255-point envelope renders a half-second note");
            Check(failures, elapsed < 1500, $"and does it quickly (took {elapsed} ms)");
        }
    }
}

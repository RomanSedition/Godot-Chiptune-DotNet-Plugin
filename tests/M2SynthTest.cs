using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m2_synth_test.gd -- same checks, same
    // tolerances, run headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/M2SynthTest.cs
    public partial class M2SynthTest : SceneTree
    {
        const int SampleRate = 44100;

        public override void _Initialize()
        {
            var failures = new List<string>();

            TestSquare(failures);
            TestTriangle(failures);
            TestSineFrequency(failures);
            TestNoise(failures);
            TestEnvelope(failures);
            TestBufferLength(failures);

            if (failures.Count == 0)
            {
                GD.Print("M2 synth_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M2 synth_test: FAIL - {f}");
                GD.PrintErr($"M2 synth_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static Instrument MakeInstrument(string waveform, float dutyCycle = 0.5f, IEnumerable<float> envelope = null)
        {
            var instrument = new Instrument { Waveform = waveform, DutyCycle = dutyCycle };
            if (envelope != null)
            {
                instrument.Envelope.Clear();
                foreach (var v in envelope)
                    instrument.Envelope.Add(v);
            }
            return instrument;
        }

        static int CountZeroCrossings(float[] buffer)
        {
            var crossings = 0;
            for (var i = 1; i < buffer.Length; i++)
            {
                if ((buffer[i - 1] < 0.0f) != (buffer[i] < 0.0f))
                    crossings++;
            }
            return crossings;
        }

        static void TestSquare(List<string> failures)
        {
            var instrument = MakeInstrument("square", 0.5f);
            var buffer = Synth.GenerateBuffer(instrument, 69, 1.0f, SampleRate); // A4 = 440Hz
            Check(failures, buffer.Length == SampleRate, "square: buffer length == sample_rate");
            var onlyExtremes = buffer.All(v => Mathf.Abs(Mathf.Abs(v) - 1.0f) <= 0.0001f);
            Check(failures, onlyExtremes, "square: samples are +-1.0");
            // One full cycle at 440Hz crosses zero twice; expect ~880 crossings in 1s, +-5%.
            var crossings = CountZeroCrossings(buffer);
            Check(failures, Mathf.Abs(crossings - 880) <= 44, $"square: zero-crossing count near 880 (got {crossings})");
        }

        static void TestTriangle(List<string> failures)
        {
            var instrument = MakeInstrument("triangle");
            var buffer = Synth.GenerateBuffer(instrument, 69, 1.0f, SampleRate);
            var maxVal = float.NegativeInfinity;
            var minVal = float.PositiveInfinity;
            foreach (var v in buffer)
            {
                maxVal = Mathf.Max(maxVal, v);
                minVal = Mathf.Min(minVal, v);
            }
            Check(failures, maxVal <= 1.0001f && maxVal > 0.9f, $"triangle: peak near +1.0 (got {maxVal})");
            Check(failures, minVal >= -1.0001f && minVal < -0.9f, $"triangle: trough near -1.0 (got {minVal})");
            var crossings = CountZeroCrossings(buffer);
            Check(failures, Mathf.Abs(crossings - 880) <= 44, $"triangle: zero-crossing count near 880 (got {crossings})");
        }

        static void TestSineFrequency(List<string> failures)
        {
            var instrument = MakeInstrument("sine");
            var buffer = Synth.GenerateBuffer(instrument, 69, 1.0f, SampleRate);
            var crossings = CountZeroCrossings(buffer);
            Check(failures, Mathf.Abs(crossings - 880) <= 44, $"sine: zero-crossing count near 880 (got {crossings})");
            foreach (var v in buffer)
            {
                if (v > 1.0001f || v < -1.0001f)
                {
                    failures.Add($"sine: sample out of [-1, 1] range ({v})");
                    break;
                }
            }
        }

        static void TestNoise(List<string> failures)
        {
            var instrument = MakeInstrument("noise");
            var buffer = Synth.GenerateBuffer(instrument, 69, 0.5f, SampleRate);
            var inRange = true;
            var sum = 0.0f;
            var distinct = new HashSet<float>();
            foreach (var v in buffer)
            {
                if (v > 1.0001f || v < -1.0001f)
                    inRange = false;
                sum += v;
                distinct.Add(Mathf.Snapped(v, 0.01f));
            }
            Check(failures, inRange, "noise: all samples within [-1, 1]");
            Check(failures, distinct.Count > 100, "noise: samples vary (not constant)");
            var mean = sum / buffer.Length;
            Check(failures, Mathf.Abs(mean) < 0.05f, $"noise: mean near zero (got {mean})");
        }

        static void TestEnvelope(List<string> failures)
        {
            var instrument = MakeInstrument("sine", 0.5f, new[] { 0.0f, 1.0f });
            var buffer = Synth.GenerateBuffer(instrument, 69, 1.0f, SampleRate);
            Check(failures, Mathf.Abs(buffer[0]) < 0.01f, "envelope: first sample near silent");
            var lastQuarterPeak = 0.0f;
            for (var i = (int)(SampleRate * 0.9); i < buffer.Length; i++)
                lastQuarterPeak = Mathf.Max(lastQuarterPeak, Mathf.Abs(buffer[i]));
            Check(failures, lastQuarterPeak > 0.8f, $"envelope: samples near end approach full volume (got {lastQuarterPeak})");
        }

        static void TestBufferLength(List<string> failures)
        {
            var instrument = MakeInstrument("square");
            var buffer = Synth.GenerateBuffer(instrument, 60, 0.25f, SampleRate);
            Check(failures, buffer.Length == (int)(0.25f * SampleRate), "buffer length matches duration * sample_rate");
        }
    }
}

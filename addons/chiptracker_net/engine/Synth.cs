using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/synth.gd. Pure static
    // utility (like the GDScript version's RefCounted-with-only-static-
    // methods shape) -- no Godot base class, no [Tool]/[GlobalClass]
    // needed, since it holds no state and is never attached to a node.
    public static class Synth
    {
        const float Tau = Mathf.Pi * 2.0f;
        static readonly string[] NoteNames = { "C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-" };

        public static float NoteToFreq(int note) => 440.0f * Mathf.Pow(2.0f, (note - 69) / 12.0f);

        // Human-readable note name (e.g. "A#4") for a MIDI-style note
        // number, or "---" for -1/empty.
        public static string NoteName(int note)
        {
            if (note < 0)
                return "---";
            var octave = note / 12 - 1;
            return $"{NoteNames[note % 12]}{octave}";
        }

        // Renders `duration` seconds of `instrument` playing `note` as mono samples in [-1, 1].
        public static float[] GenerateBuffer(Instrument instrument, int note, float duration, int sampleRate)
        {
            var frameCount = Mathf.RoundToInt(duration * sampleRate);
            var buffer = new float[frameCount];

            var freq = NoteToFreq(note);
            var phase = 0.0f;
            var phaseInc = freq / sampleRate;
            var rng = new RandomNumberGenerator();
            // Empty (evenly spaced points) unless the instrument has its own times.
            var times = instrument.HasCustomTimes() ? instrument.EnvelopeTimes : new Array<float>();
            // Pitch bend: empty means no bend, the common case, so it's
            // skipped entirely rather than costing a pow() every sample for nothing.
            var hasPitch = instrument.PitchEnvelope.Count > 0;
            var pitchTimes = instrument.HasCustomPitchTimes() ? instrument.PitchEnvelopeTimes : new Array<float>();

            for (var i = 0; i < frameCount; i++)
            {
                var raw = WaveformSample(instrument.Waveform, phase, instrument.DutyCycle, rng);
                var t = (float)i / sampleRate;
                var env = EnvelopeValue(instrument.Envelope, t, duration, times);
                buffer[i] = raw * env;
                var step = phaseInc;
                if (hasPitch)
                {
                    var semitones = EnvelopeValue(instrument.PitchEnvelope, t, duration, pitchTimes);
                    step *= Mathf.Pow(2.0f, semitones / 12.0f);
                }
                phase = Mathf.PosMod(phase + step, 1.0f);
            }

            return buffer;
        }

        // Renders `instrument` playing `note` together with every one of
        // its layers (Instrument.Layers), each resolving its own note via
        // Instrument.ResolvedLayerNote and rendered with its own
        // waveform/envelopes, summed sample-by-sample into one buffer. A
        // layer's own layers, if it somehow has any, are ignored (layering
        // is one level deep). An instrument with no layers renders
        // identically to GenerateBuffer -- this is only used for
        // previewing right now (InstrumentPanel), not for song playback,
        // so the sum isn't normalized for headroom; a loud stack of layers
        // can clip, the same as stacking channels at full volume would.
        public static float[] GenerateLayeredBuffer(Instrument instrument, int note, float duration, int sampleRate)
        {
            var buffer = GenerateBuffer(instrument, note, duration, sampleRate);
            foreach (var layer in instrument.Layers)
            {
                var layerBuffer = GenerateBuffer(layer, layer.ResolvedLayerNote(note), duration, sampleRate);
                var count = Mathf.Min(buffer.Length, layerBuffer.Length);
                for (var i = 0; i < count; i++)
                    buffer[i] += layerBuffer[i];
            }
            return buffer;
        }

        // Converts float samples in [-1, 1] to little-endian 16-bit PCM
        // bytes, for anything that hands samples to AudioStreamWav
        // (WavRenderer's file export, InstrumentPanel's single-note preview).
        public static byte[] ToPcm16(float[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var clamped = Mathf.Clamp(samples[i], -1.0f, 1.0f);
                var pcm = (short)Mathf.RoundToInt(clamped * 32767.0f);
                var unsigned = unchecked((ushort)pcm);
                bytes[i * 2] = (byte)(unsigned & 0xFF);
                bytes[i * 2 + 1] = (byte)(unsigned >> 8);
            }
            return bytes;
        }

        static float WaveformSample(string waveform, float phase, float dutyCycle, RandomNumberGenerator rng)
        {
            return waveform switch
            {
                "square" => phase < dutyCycle ? 1.0f : -1.0f,
                "triangle" => phase < 0.5f ? -1.0f + 4.0f * phase : 3.0f - 4.0f * phase,
                "noise" => rng.RandfRange(-1.0f, 1.0f),
                "sine" => Mathf.Sin(phase * Tau),
                _ => 0.0f,
            };
        }

        // Linear-interpolates across `envelope`'s breakpoints (each 0-1)
        // over the sample's position in [0, duration]. Empty envelope
        // means constant full volume.
        //
        // `times` says where each breakpoint sits along the note (0-1, one
        // per point, never decreasing -- see Instrument.EnvelopeTimes).
        // Empty, or the wrong length, means evenly spaced from 0 to 1.
        // Before the first point the level holds its value, and after the
        // last it holds that one; two points at the same time make a step.
        // internal, not private: EnvelopeArchetypesTest.cs holds the
        // binary-search lookup to a plain-scan reference implementation
        // directly, the same way envelope_archetypes_test.gd calls
        // Synth._envelope_value() (GDScript has no real access enforcement).
        internal static float EnvelopeValue(Array<float> envelope, float t, float duration, Array<float> times = null)
        {
            times ??= new Array<float>();
            if (envelope.Count == 0)
                return 1.0f;
            if (envelope.Count == 1)
                return envelope[0];

            var progress = duration > 0.0f ? Mathf.Clamp(t / duration, 0.0f, 1.0f) : 0.0f;
            if (times.Count == envelope.Count)
            {
                var last = envelope.Count - 1;
                if (progress < times[0])
                    return envelope[0];
                if (progress >= times[last])
                    return envelope[last];
                // The segment holding `progress`: the first whose end is
                // after it. A binary search, since an envelope can have up
                // to a few hundred points and this runs for every sample
                // of every note.
                var low = 0;
                var high = last - 1;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (progress < times[middle + 1])
                        high = middle;
                    else
                        low = middle + 1;
                }
                var span = times[low + 1] - times[low];
                if (span <= 0.0f)
                    return envelope[low];
                return Mathf.Lerp(envelope[low], envelope[low + 1], (progress - times[low]) / span);
            }
            var segmentCount = envelope.Count - 1;
            var pos = progress * segmentCount;
            var idx = Mathf.Clamp((int)Mathf.Floor(pos), 0, segmentCount - 1);
            var frac = pos - idx;
            return Mathf.Lerp(envelope[idx], envelope[idx + 1], frac);
        }
    }
}

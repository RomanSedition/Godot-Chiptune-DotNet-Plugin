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

        // Everything one sounding note needs, snapshotted into plain
        // managed types up front.
        //
        // Nothing in here is a Godot object, and that is deliberate: a
        // PlaybackState voice is generated on PlaybackEngine's background
        // audio thread (see "Playback performance" in CHIPTRACKER_SPEC.md),
        // and Godot's C# objects -- Instrument's marshalled property
        // getters, Array<float>, RandomNumberGenerator -- are refcounted
        // native objects that are not safe to create, read, and release off
        // the main thread. Reading them per sample also crosses the
        // marshalling boundary 44100 times a second per channel for values
        // that never change mid-note. Snapshotting fixes both.
        internal readonly struct VoiceSetup
        {
            public readonly string Waveform;
            public readonly float DutyCycle;
            public readonly float PhaseInc;
            public readonly float[] Envelope;
            public readonly float[] EnvelopeTimes;  // null when evenly spaced
            public readonly float[] PitchEnvelope;  // null when there's no bend
            public readonly float[] PitchEnvelopeTimes;

            public VoiceSetup(Instrument instrument, int note, int sampleRate)
            {
                Waveform = instrument.Waveform;
                DutyCycle = instrument.DutyCycle;
                PhaseInc = NoteToFreq(note) / sampleRate;
                Envelope = ToFloats(instrument.Envelope);
                // Null (evenly spaced points) unless the instrument has its own times.
                EnvelopeTimes = instrument.HasCustomTimes() ? ToFloats(instrument.EnvelopeTimes) : null;
                // Pitch bend: null means no bend, the common case, so it's
                // skipped entirely rather than costing a pow() every sample for nothing.
                PitchEnvelope = instrument.PitchEnvelope.Count > 0 ? ToFloats(instrument.PitchEnvelope) : null;
                PitchEnvelopeTimes = instrument.HasCustomPitchTimes() ? ToFloats(instrument.PitchEnvelopeTimes) : null;
            }
        }

        static float[] ToFloats(Array<float> values)
        {
            var copy = new float[values.Count];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = values[i];
            return copy;
        }

        // One sample of `setup`'s instrument at `phase`, `t` seconds into a
        // note of `duration`, advancing `phase` to the next sample's.
        // The shared body of GenerateBuffer's loop and PlaybackState's
        // streaming voices -- keep it that way, so offline renders and live
        // playback can't drift apart.
        internal static float NextSample(in VoiceSetup setup, ref float phase, float t, float duration, System.Random rng)
        {
            var raw = WaveformSample(setup.Waveform, phase, setup.DutyCycle, rng);
            var env = EnvelopeValue(setup.Envelope, t, duration, setup.EnvelopeTimes);
            var step = setup.PhaseInc;
            if (setup.PitchEnvelope != null)
            {
                var semitones = EnvelopeValue(setup.PitchEnvelope, t, duration, setup.PitchEnvelopeTimes);
                step *= Mathf.Pow(2.0f, semitones / 12.0f);
            }
            phase = Mathf.PosMod(phase + step, 1.0f);
            return raw * env;
        }

        // Renders `duration` seconds of `instrument` playing `note` as mono samples in [-1, 1].
        public static float[] GenerateBuffer(Instrument instrument, int note, float duration, int sampleRate)
        {
            var frameCount = Mathf.RoundToInt(duration * sampleRate);
            var buffer = new float[frameCount];

            var setup = new VoiceSetup(instrument, note, sampleRate);
            var phase = 0.0f;
            var rng = new System.Random();

            for (var i = 0; i < frameCount; i++)
                buffer[i] = NextSample(setup, ref phase, (float)i / sampleRate, duration, rng);

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

        // System.Random, not Godot's RandomNumberGenerator: this runs on
        // PlaybackEngine's audio thread, where a refcounted Godot object
        // isn't safe to hold (see VoiceSetup).
        static float WaveformSample(string waveform, float phase, float dutyCycle, System.Random rng)
        {
            return waveform switch
            {
                "square" => phase < dutyCycle ? 1.0f : -1.0f,
                "triangle" => phase < 0.5f ? -1.0f + 4.0f * phase : 3.0f - 4.0f * phase,
                "noise" => (float)(rng.NextDouble() * 2.0 - 1.0),
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
        internal static float EnvelopeValue(Array<float> envelope, float t, float duration, Array<float> times = null) =>
            EnvelopeValue(ToFloats(envelope), t, duration, times == null ? null : ToFloats(times));

        // The real implementation, on plain arrays -- what the per-sample
        // path actually calls (see VoiceSetup on why it never touches
        // Godot collections). `times` is null when the points are evenly
        // spaced.
        internal static float EnvelopeValue(float[] envelope, float t, float duration, float[] times)
        {
            if (envelope.Length == 0)
                return 1.0f;
            if (envelope.Length == 1)
                return envelope[0];

            var progress = duration > 0.0f ? Mathf.Clamp(t / duration, 0.0f, 1.0f) : 0.0f;
            if (times != null && times.Length == envelope.Length)
            {
                var last = envelope.Length - 1;
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
            var segmentCount = envelope.Length - 1;
            var pos = progress * segmentCount;
            var idx = Mathf.Clamp((int)Mathf.Floor(pos), 0, segmentCount - 1);
            var frac = pos - idx;
            return Mathf.Lerp(envelope[idx], envelope[idx + 1], frac);
        }
    }
}

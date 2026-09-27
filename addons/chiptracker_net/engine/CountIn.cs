using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/count_in.gd. The
    // count-in before a recording pass: one bar of built-in clicks, so it
    // works whether or not the song has a Metronome. It uses the same
    // noise click and accent scheme as the metronome (Song.Metronome*),
    // but is rendered here rather than read from the song, so it doesn't
    // depend on any channel.
    public static class CountIn
    {
        const int SampleRate = PlaybackState.SampleRate;

        // The click's instrument: white noise with the metronome's
        // fast-decaying envelope, so a click is short however long the
        // beat is.
        public static Instrument ClickInstrument()
        {
            var instrument = new Instrument { Waveform = "noise" };
            foreach (var value in Song.MetronomeEnvelope)
                instrument.Envelope.Add(value);
            return instrument;
        }

        // One bar of clicks, `beats` beats long, one click at the start
        // of each beat. The result is exactly beats * rowsPerBeat *
        // samplesPerRow samples, so the song that follows it starts on a
        // row boundary. With `accent`, the first click is louder and the
        // rest softer; without it they are all equal.
        public static float[] Render(int samplesPerRow, int rowsPerBeat, int beats, bool accent)
        {
            var beatSamples = samplesPerRow * rowsPerBeat;
            var outSamples = new float[beatSamples * beats];
            var instrument = ClickInstrument();
            for (var beat = 0; beat < beats; beat++)
            {
                var volume = Song.MetronomePlainVolume;
                if (accent)
                    volume = beat == 0 ? Song.MetronomeAccentVolume : Song.MetronomeBeatVolume;
                var click = Synth.GenerateBuffer(instrument, Song.MetronomeNote, (float)beatSamples / SampleRate, SampleRate);
                var scale = volume / 15.0f;
                var start = beat * beatSamples;
                var count = Godot.Mathf.Min(click.Length, beatSamples);
                for (var i = 0; i < count; i++)
                    outSamples[start + i] = click[i] * scale;
            }
            return outSamples;
        }
    }
}

using System.Collections.Generic;
using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/wav_renderer.gd. Plain
    // static utility (like Synth) -- no Godot base type, no state.
    public static class WavRenderer
    {
        const int SampleRate = PlaybackState.SampleRate;

        // Renders `song` to a mono 16-bit PCM WAV file at `outputPath`
        // (res:// or an absolute filesystem path), as fast as possible
        // with no real-time constraint. Always renders exactly one
        // non-looping pass through the song regardless of any loop
        // setting elsewhere -- a WAV file has to end somewhere. Returns
        // the Error from writing the file, or Error.InvalidParameter if
        // the song has no order list to render.
        public static Error RenderSong(Song song, string outputPath)
        {
            if (song.OrderList.Count == 0)
                return Error.InvalidParameter;

            var state = new PlaybackState(song, false) { ExcludeMetronome = true };
            state.PlayFrom();

            var samples = new List<float>();
            while (state.Playing)
                samples.Add(state.AdvanceSample());

            var stream = new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = SampleRate,
                Stereo = false,
                Data = Synth.ToPcm16(samples.ToArray()),
            };

            return stream.SaveToWav(outputPath);
        }
    }
}

using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m2_synth_demo.gd -- manual listening check
    // for M2: cycles through one note on each waveform. Run this scene
    // (F6) and listen -- square/triangle/sine should be clearly pitched at
    // A4, noise should be a short burst of static.
    public partial class M2SynthDemoNet : Node
    {
        const int SampleRate = 44100;
        const int Note = 69; // A4
        const float NoteDuration = 0.6f;
        const float GapDuration = 0.25f;

        static readonly string[] Waveforms = { "square", "triangle", "sine", "noise" };

        AudioStreamPlayer _player;
        AudioStreamGeneratorPlayback _playback;

        readonly System.Collections.Generic.List<float[]> _queue = new();
        int _queueIndex;
        int _samplesPushedForCurrent;

        public override void _Ready()
        {
            _player = GetNode<AudioStreamPlayer>("AudioStreamPlayer");

            var stream = new AudioStreamGenerator
            {
                MixRate = SampleRate,
                BufferLength = 0.5f,
            };
            _player.Stream = stream;

            var envelope = new Godot.Collections.Array<float> { 0.0f, 1.0f, 1.0f, 0.0f };
            foreach (var waveform in Waveforms)
            {
                var instrument = new Instrument { Waveform = waveform, DutyCycle = 0.5f };
                instrument.Envelope.Clear();
                foreach (var v in envelope)
                    instrument.Envelope.Add(v);
                _queue.Add(Synth.GenerateBuffer(instrument, Note, NoteDuration, SampleRate));
                _queue.Add(System.Array.Empty<float>()); // silent gap between notes
                GD.Print($"Queued waveform: {waveform}");
            }

            _player.Play();
            _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
        }

        public override void _Process(double delta)
        {
            if (_playback == null || _queueIndex >= _queue.Count)
                return;

            var toFill = _playback.GetFramesAvailable();
            while (toFill > 0 && _queueIndex < _queue.Count)
            {
                var current = _queue[_queueIndex];
                var targetLen = current.Length > 0 ? current.Length : (int)(GapDuration * SampleRate);
                var remaining = targetLen - _samplesPushedForCurrent;
                var n = Mathf.Min(toFill, remaining);
                for (var i = 0; i < n; i++)
                {
                    var sample = current.Length > 0 ? current[_samplesPushedForCurrent + i] : 0.0f;
                    _playback.PushFrame(new Vector2(sample, sample));
                }
                _samplesPushedForCurrent += n;
                toFill -= n;
                if (_samplesPushedForCurrent >= targetLen)
                {
                    _queueIndex++;
                    _samplesPushedForCurrent = 0;
                    if (_queueIndex >= _queue.Count)
                        GD.Print("M2 demo: done, all waveforms played");
                }
            }
        }
    }
}

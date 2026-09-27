using System.Collections.Generic;
using Godot;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/playback_state.gd. Pure
    // sequencing/mixing logic, decoupled from AudioStreamGenerator so it's
    // testable headlessly. Advances one mixed mono sample at a time; the
    // caller (PlaybackEngine) is responsible for pushing samples to an
    // audio output.
    public class PlaybackState : PlaybackCursor
    {
        public const int SampleRate = 44100;

        public Song Song;
        public bool Loop;

        // Leaves the Metronome channel out of the mix entirely (not
        // counted toward the per-channel level division either). Set for
        // file exports so a practice click never ends up in a finished
        // render; Play and the audio cache leave it off so the click is
        // audible while working.
        public bool ExcludeMetronome;

        // One sounding note on one channel, generated a sample at a time
        // rather than rendered whole at trigger time -- see "Playback
        // performance" in CHIPTRACKER_SPEC.md. A long-sustaining note used
        // to cost its entire length (seconds of audio) inside the
        // TriggerRow() that started it, which is what made dense rows
        // stutter; now each sample costs the same tiny amount whenever it's
        // actually needed.
        class ChannelVoice
        {
            public Synth.VoiceSetup Setup;
            public Synth.VoiceSetup[] LayerSetups = System.Array.Empty<Synth.VoiceSetup>();
            public float[] LayerPhases = System.Array.Empty<float>();
            public System.Random[] LayerRngs = System.Array.Empty<System.Random>();

            public bool Active;
            public float Duration;
            public int TotalSamples;
            public int SampleIndex;
            public float Phase;
            public System.Random Rng;
            public float VolScale = 1.0f;

            // The current, not-yet-consumed sample. Computed on demand so
            // ChannelRawSample() can peek at it before AdvanceSample() has
            // ever run (M3PlaybackTest relies on exactly that), and so it's
            // only ever computed once per sample.
            float _current;
            bool _hasCurrent;

            public float Peek()
            {
                if (_hasCurrent)
                    return _current;
                _current = Generate();
                _hasCurrent = true;
                return _current;
            }

            // The peeked sample, with phase/index advanced past it.
            public void Advance()
            {
                Peek();
                _hasCurrent = false;
                SampleIndex++;
            }

            public void Start(Instrument instrument, int note, float duration, float volScale)
            {
                Setup = new Synth.VoiceSetup(instrument, note, SampleRate);
                var layerCount = instrument.Layers.Count;
                LayerSetups = new Synth.VoiceSetup[layerCount];
                LayerPhases = new float[layerCount];
                LayerRngs = new System.Random[layerCount];
                for (var i = 0; i < layerCount; i++)
                {
                    var layer = instrument.Layers[i];
                    LayerSetups[i] = new Synth.VoiceSetup(layer, layer.ResolvedLayerNote(note), SampleRate);
                    LayerRngs[i] = new System.Random();
                }
                Active = true;
                Duration = duration;
                TotalSamples = Mathf.RoundToInt(duration * SampleRate);
                SampleIndex = 0;
                Phase = 0.0f;
                Rng = new System.Random();
                VolScale = volScale;
                _hasCurrent = false;
                _current = 0.0f;
            }

            // Mirrors Synth.GenerateLayeredBuffer's sum-the-layers shape,
            // one sample at a time. Past the note's planned length it's
            // silent, the same as running off the end of the rendered
            // buffer this replaced.
            float Generate()
            {
                if (!Active || SampleIndex >= TotalSamples)
                    return 0.0f;
                var t = (float)SampleIndex / SampleRate;
                var sample = Synth.NextSample(Setup, ref Phase, t, Duration, Rng);
                for (var i = 0; i < LayerSetups.Length; i++)
                    sample += Synth.NextSample(LayerSetups[i], ref LayerPhases[i], t, Duration, LayerRngs[i]);
                return sample * VolScale;
            }
        }

        readonly List<ChannelVoice> _voices = new();

        public PlaybackState(Song song, bool loop = false)
        {
            Song = song;
            Loop = loop;
            SamplesPerRow = Mathf.RoundToInt(SampleRate * RowDurationSec());
            for (var i = 0; i < song.Channels.Count; i++)
                _voices.Add(new ChannelVoice());
            _audible = new bool[_voices.Count];
        }

        public void PlayFrom(int orderIndex = 0, int rowIndex = 0)
        {
            if (Song.OrderList.Count == 0)
                return;
            OrderIndex = orderIndex;
            RowIndex = rowIndex;
            SamplesIntoRow = 0;
            Playing = true;
            RefreshAudibility();
            TriggerRow();
        }

        public void Stop() => Playing = false;

        // Returns the next mixed mono sample in [-1, 1]; 0.0 once stopped.
        public float AdvanceSample()
        {
            if (!Playing)
                return 0.0f;
            var sample = MixSample();
            foreach (var voice in _voices)
                voice.Advance();
            SamplesIntoRow++;
            if (SamplesIntoRow >= SamplesPerRow)
            {
                SamplesIntoRow = 0;
                AdvanceRow();
            }
            return sample;
        }

        public Pattern CurrentPattern()
        {
            var patternIndex = Song.OrderList[OrderIndex];
            return Song.Patterns[patternIndex];
        }

        // Current unmixed sample for one channel; exposed for testing/metering.
        public float ChannelRawSample(int channelIdx) => _voices[channelIdx].Peek();

        float RowDurationSec() => 60.0f / Song.Tempo / Song.RowsPerBeat;

        // Which channels are in the mix, and how many. Channel.Muted/.Solo
        // and Song.IsMetronomeChannel() are all marshalled Godot reads (the
        // last one compares a marshalled string), so this is snapshotted
        // rather than read while mixing: mixing runs on PlaybackEngine's
        // audio thread, and reading Godot objects there is exactly what
        // Synth.VoiceSetup exists to avoid.
        //
        // RefreshAudibility() must therefore only ever be called from the
        // main thread -- PlayFrom() (every caller of which is main-thread:
        // PlaybackEngine.Play, WavRenderer, PatternAudioCache.RenderPattern)
        // and ChiptrackerMainView when a mute/solo button changes mid-pass.
        // Reading these off the audio thread silently yields stale values,
        // which is what made the dock's M/S buttons look dead during live
        // playback while still working in renders.
        bool[] _audible;
        int _audibleCount;

        public void RefreshAudibility()
        {
            // Song is a mutable field and channels can be added while a
            // state exists, so the voice list isn't necessarily still the
            // width it was constructed at.
            while (_voices.Count < Song.Channels.Count)
                _voices.Add(new ChannelVoice());
            if (_audible.Length != _voices.Count)
                _audible = new bool[_voices.Count];

            var anySolo = false;
            foreach (var channel in Song.Channels)
            {
                if (channel.Solo)
                {
                    anySolo = true;
                    break;
                }
            }
            _audibleCount = 0;
            for (var i = 0; i < _audible.Length && i < Song.Channels.Count; i++)
            {
                var channel = Song.Channels[i];
                var audible = (anySolo ? channel.Solo : !channel.Muted)
                    && !(ExcludeMetronome && Song.IsMetronomeChannel(i));
                _audible[i] = audible;
                if (audible)
                    _audibleCount++;
            }
        }

        float MixSample()
        {
            var total = 0.0f;
            // _audible is resized alongside _voices by RefreshAudibility,
            // but that only runs on the main thread -- so bound by both
            // rather than trusting them to agree on the audio thread.
            var count = Mathf.Min(_voices.Count, _audible.Length);
            for (var i = 0; i < count; i++)
            {
                if (!_audible[i])
                    continue;
                total += _voices[i].Peek();
            }
            return Mathf.Clamp(total / Mathf.Max(1, _audibleCount), -1.0f, 1.0f);
        }

        void TriggerRow()
        {
            lock (Song.PlaybackLock)
            {
                var pattern = CurrentPattern();
                var rowCells = pattern.Rows[RowIndex];
                // Bounded by the voices too, not just the row width: this
                // runs on the audio thread, where an IndexOutOfRange would
                // take the editor down (see PlaybackEngine's generation
                // loop), and a pattern can be wider than the voice list if
                // Song was swapped or grew mid-pass.
                var channelCount = Mathf.Min(rowCells.Count, _voices.Count);
                for (var chIdx = 0; chIdx < channelCount; chIdx++)
                {
                    var cell = rowCells[chIdx].As<Cell>();
                    if (cell.Note >= 0)
                    {
                        var instrument = FindInstrument(cell.InstrumentId);
                        if (instrument != null)
                        {
                            var steps = RowsUntilNextNote(chIdx, OrderIndex, RowIndex);
                            var duration = steps * RowDurationSec();
                            // Only sets the voice up; the samples themselves
                            // are generated one at a time as playback
                            // reaches them (ChannelVoice.Peek/Advance), so a
                            // long note costs nothing extra here.
                            _voices[chIdx].Start(instrument, cell.Note, duration, cell.Volume / 15.0f);
                        }
                    }
                }
            }
            EmitRowAdvanced(OrderIndex, RowIndex);
        }

        Instrument FindInstrument(int id)
        {
            foreach (var instrument in Song.Instruments)
            {
                if (instrument.Id == id)
                    return instrument;
            }
            return null;
        }

        // How many rows until channel `channelIdx` next sounds a new note,
        // starting the search just after (startOrder, startRow). Used to
        // size a triggered note's buffer so its envelope spans exactly how
        // long it will actually sound before being cut off or retriggered.
        // Capped at one full pass over the order list to guarantee
        // termination; a channel with no further notes sustains until the
        // song ends (or wraps, if looping).
        int RowsUntilNextNote(int channelIdx, int startOrder, int startRow)
        {
            var order = startOrder;
            var row = startRow;
            var maxSteps = Song.RowsPerPattern * Song.OrderList.Count;
            for (var step = 1; step <= maxSteps; step++)
            {
                var pattern = Song.Patterns[Song.OrderList[order]];
                row++;
                if (row >= pattern.Rows.Count)
                {
                    row = 0;
                    order++;
                    if (order >= Song.OrderList.Count)
                    {
                        if (Loop)
                        {
                            order = 0;
                        }
                        else
                        {
                            return step;
                        }
                    }
                }
                var cell = Song.Patterns[Song.OrderList[order]].Rows[row][channelIdx].As<Cell>();
                if (cell.Note >= 0)
                    return step;
            }
            return maxSteps;
        }

        void AdvanceRow()
        {
            var pattern = CurrentPattern();
            RowIndex++;
            if (RowIndex >= pattern.Rows.Count)
            {
                RowIndex = 0;
                OrderIndex++;
                if (OrderIndex >= Song.OrderList.Count)
                {
                    if (Loop)
                    {
                        OrderIndex = 0;
                    }
                    else
                    {
                        Playing = false;
                        EmitFinished();
                        return;
                    }
                }
            }
            TriggerRow();
        }
    }
}

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

        readonly List<float[]> _channelBuffers = new();
        readonly List<int> _channelCursors = new();

        public PlaybackState(Song song, bool loop = false)
        {
            Song = song;
            Loop = loop;
            SamplesPerRow = Mathf.RoundToInt(SampleRate * RowDurationSec());
            for (var i = 0; i < song.Channels.Count; i++)
            {
                _channelBuffers.Add(System.Array.Empty<float>());
                _channelCursors.Add(0);
            }
        }

        public void PlayFrom(int orderIndex = 0, int rowIndex = 0)
        {
            if (Song.OrderList.Count == 0)
                return;
            OrderIndex = orderIndex;
            RowIndex = rowIndex;
            SamplesIntoRow = 0;
            Playing = true;
            TriggerRow();
        }

        public void Stop() => Playing = false;

        // Returns the next mixed mono sample in [-1, 1]; 0.0 once stopped.
        public float AdvanceSample()
        {
            if (!Playing)
                return 0.0f;
            var sample = MixSample();
            for (var i = 0; i < _channelCursors.Count; i++)
            {
                if (_channelCursors[i] < _channelBuffers[i].Length)
                    _channelCursors[i]++;
            }
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
        public float ChannelRawSample(int channelIdx)
        {
            var buf = _channelBuffers[channelIdx];
            var cursor = _channelCursors[channelIdx];
            return cursor < buf.Length ? buf[cursor] : 0.0f;
        }

        float RowDurationSec() => 60.0f / Song.Tempo / Song.RowsPerBeat;

        float MixSample()
        {
            var anySolo = false;
            foreach (var channel in Song.Channels)
            {
                if (channel.Solo)
                {
                    anySolo = true;
                    break;
                }
            }

            var total = 0.0f;
            var audibleCount = 0;
            for (var i = 0; i < _channelBuffers.Count; i++)
            {
                var channel = Song.Channels[i];
                var audible = anySolo ? channel.Solo : !channel.Muted;
                if (!audible || (ExcludeMetronome && Song.IsMetronomeChannel(i)))
                    continue;
                audibleCount++;
                var buf = _channelBuffers[i];
                var cursor = _channelCursors[i];
                if (cursor < buf.Length)
                    total += buf[cursor];
            }
            return Mathf.Clamp(total / Mathf.Max(1, audibleCount), -1.0f, 1.0f);
        }

        void TriggerRow()
        {
            var pattern = CurrentPattern();
            var rowCells = pattern.Rows[RowIndex];
            for (var chIdx = 0; chIdx < rowCells.Count; chIdx++)
            {
                var cell = rowCells[chIdx].As<Cell>();
                if (cell.Note >= 0)
                {
                    var instrument = FindInstrument(cell.InstrumentId);
                    if (instrument != null)
                    {
                        var steps = RowsUntilNextNote(chIdx, OrderIndex, RowIndex);
                        var duration = steps * RowDurationSec();
                        var volScale = cell.Volume / 15.0f;
                        var buf = Synth.GenerateLayeredBuffer(instrument, cell.Note, duration, SampleRate);
                        for (var i = 0; i < buf.Length; i++)
                            buf[i] *= volScale;
                        _channelBuffers[chIdx] = buf;
                        _channelCursors[chIdx] = 0;
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

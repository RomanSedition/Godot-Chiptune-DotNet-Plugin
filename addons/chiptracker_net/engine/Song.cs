using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/song.gd.
    [Tool]
    public partial class Song : Resource
    {
        [Export] public int Tempo { get; set; } = 120; // BPM
        [Export] public int RowsPerBeat { get; set; } = 4;
        [Export] public int RowsPerPattern { get; set; } = 64;
        [Export] public Array<Channel> Channels { get; set; } = new();
        [Export] public Array<int> OrderList { get; set; } = new();
        [Export] public Array<Pattern> Patterns { get; set; } = new();
        [Export] public Array<Instrument> Instruments { get; set; } = new();

        // Column groupings for the dock's Groups tab / the pattern grid's
        // group header band -- see Channel.GroupIndex. Always at least one
        // entry (Group 1, index 0); a .tres saved before this field
        // existed loads with an empty array here, which the constructor
        // fills in.
        [Export] public Array<ChannelGroup> Groups { get; set; } = new();

        // Whether the first beat of each bar ticks louder than the other
        // beats. Only meaningful while a metronome exists (see AddMetronome()).
        [Export] public bool MetronomeAccent { get; set; } = true;

        // Reserved (case-insensitively) for the metronome's channel, group
        // and instrument. They are found by this exact name, so nothing
        // else may use it and the metronome's own three can't be renamed.
        public const string MetronomeName = "Metronome";
        public const int MetronomeBeatsPerBar = 4;

        // The click is white noise, which ignores pitch, so the accent is
        // done with volume (0-15). The envelope has many points so it hits
        // silence within the first fifth of the note, however long the
        // beat is: a short tick, not a hiss.
        public const int MetronomeNote = 60;
        public const int MetronomeAccentVolume = 15;
        public const int MetronomeBeatVolume = 9;
        public const int MetronomePlainVolume = 12;

        public static readonly float[] MetronomeEnvelope =
        {
            1.0f, 0.5f, 0.15f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
        };

        // Matches PlaybackState.SAMPLE_RATE on the GDScript side; move to
        // reference the C# PlaybackEngine's own constant once that lands (M2/M3).
        public const int DefaultSampleRate = 44100;

        public Song()
        {
            if (Groups.Count == 0)
                Groups.Add(new ChannelGroup());
        }

        // Appends a new group (channels join it via Channel.GroupIndex) and
        // returns its index.
        public int AddGroup()
        {
            Groups.Add(new ChannelGroup());
            return Groups.Count - 1;
        }

        // Removes the group at `index`, folding any channels it held into
        // the group immediately to its left (index - 1) and shifting every
        // later channel's GroupIndex down by one to match the array's new
        // shape. Refuses to remove Group 1 (index 0, always the
        // leftmost/default group) or to go below one remaining group. The
        // Metronome group is only ever removed by RemoveMetronome(), never
        // through here.
        public void RemoveGroup(int index)
        {
            if (IsMetronomeGroup(index))
                return;
            RemoveGroupAt(index);
        }

        void RemoveGroupAt(int index)
        {
            if (Groups.Count <= 1 || index <= 0 || index >= Groups.Count)
                return;
            foreach (var channel in Channels)
            {
                if (channel.GroupIndex == index)
                    channel.GroupIndex = index - 1;
                else if (channel.GroupIndex > index)
                    channel.GroupIndex--;
            }
            Groups.RemoveAt(index);
        }

        // Appends a new channel and grows every existing pattern to match,
        // so row widths always stay in sync with Channels.Count.
        public void AddChannel(Channel channel)
        {
            Channels.Add(channel);
            foreach (var pattern in Patterns)
                pattern.AddChannel();
        }

        // Removes the channel at `index` and shrinks every pattern to
        // match. Refuses to remove the last remaining channel (a song
        // needs at least one), and never removes the Metronome channel
        // (see RemoveMetronome()).
        public void RemoveChannel(int index)
        {
            if (IsMetronomeChannel(index))
                return;
            RemoveChannelAt(index);
        }

        void RemoveChannelAt(int index)
        {
            if (Channels.Count <= 1 || index < 0 || index >= Channels.Count)
                return;
            Channels.RemoveAt(index);
            foreach (var pattern in Patterns)
                pattern.RemoveChannel(index);
        }

        // Sets RowsPerPattern for the whole song and resizes every
        // existing pattern to match, preserving existing cells (see
        // Pattern.SetRowCount).
        public void SetRowsPerPattern(int newCount)
        {
            if (newCount < 1)
                return;
            RowsPerPattern = newCount;
            foreach (var pattern in Patterns)
                pattern.SetRowCount(newCount);
            SyncMetronome();
        }

        // Sets RowsPerBeat and re-lays the metronome's ticks to match.
        public void SetRowsPerBeat(int newValue)
        {
            if (newValue < 1)
                return;
            RowsPerBeat = newValue;
            SyncMetronome();
        }

        // Creates a new empty pattern (sized to the song's current
        // RowsPerPattern and channel count) and appends it to Patterns.
        // Not automatically added to the order list -- append the returned
        // index to OrderList for that. Ticks are filled in for the
        // metronome, if there is one.
        public int AddPattern()
        {
            Patterns.Add(new Pattern(RowsPerPattern, Channels.Count));
            SyncMetronome();
            return Patterns.Count - 1;
        }

        // ---------- Metronome ----------

        // True if `candidate` would collide with the reserved metronome name.
        public static bool IsReservedName(string candidate) =>
            candidate.Trim().ToLower() == MetronomeName.ToLower();

        public int MetronomeChannelIndex()
        {
            for (var i = 0; i < Channels.Count; i++)
            {
                if (Channels[i].Name == MetronomeName)
                    return i;
            }
            return -1;
        }

        public bool HasMetronome() => MetronomeChannelIndex() != -1;

        public bool IsMetronomeChannel(int index) =>
            index >= 0 && index < Channels.Count && Channels[index].Name == MetronomeName;

        public bool IsMetronomeGroup(int index) =>
            index >= 0 && index < Groups.Count && Groups[index].Name == MetronomeName;

        public bool IsMetronomeInstrument(Instrument instrument) =>
            instrument != null && instrument.Name == MetronomeName;

        public Instrument MetronomeInstrument()
        {
            foreach (var instrument in Instruments)
            {
                if (instrument.Name == MetronomeName)
                    return instrument;
            }
            return null;
        }

        // Creates the metronome: an instrument, a group and a channel, all
        // named Metronome, with a tick on every beat of every pattern.
        // There is only ever one; returns false (changing nothing) if it
        // already exists.
        public bool AddMetronome()
        {
            if (HasMetronome())
                return false;
            var instrument = new Instrument
            {
                Id = NextInstrumentId(),
                Name = MetronomeName,
                Waveform = "noise",
            };
            foreach (var value in MetronomeEnvelope)
                instrument.Envelope.Add(value);
            Instruments.Add(instrument);

            var group = new ChannelGroup { Name = MetronomeName };
            Groups.Add(group);

            var channel = new Channel
            {
                Name = MetronomeName,
                InstrumentType = "noise",
                GroupIndex = Groups.Count - 1,
            };
            AddChannel(channel);
            SyncMetronome();
            return true;
        }

        // Removes the metronome's channel, group and instrument together.
        // Returns false if there's no metronome, or if it is the song's
        // only channel (a song needs at least one channel).
        public bool RemoveMetronome()
        {
            var channelIndex = MetronomeChannelIndex();
            if (channelIndex == -1 || Channels.Count <= 1)
                return false;
            RemoveChannelAt(channelIndex);
            for (var i = Groups.Count - 1; i > 0; i--)
            {
                if (IsMetronomeGroup(i))
                    RemoveGroupAt(i);
            }
            var instrument = MetronomeInstrument();
            if (instrument != null)
                Instruments.Remove(instrument);
            return true;
        }

        // Lays a tick on every beat (every RowsPerBeat rows) of every
        // pattern for the metronome channel, and clears every other row of
        // that channel. With accent on, the first beat of each bar is
        // louder. No-op without a metronome. Rewrites the whole channel,
        // so hand edits to it don't survive a re-sync.
        public void SyncMetronome()
        {
            var channelIndex = MetronomeChannelIndex();
            var instrument = MetronomeInstrument();
            if (channelIndex == -1 || instrument == null || RowsPerBeat < 1)
                return;
            var rowsPerBar = RowsPerBeat * MetronomeBeatsPerBar;
            foreach (var pattern in Patterns)
            {
                for (var rowIndex = 0; rowIndex < pattern.Rows.Count; rowIndex++)
                {
                    var cell = (Cell)pattern.Rows[rowIndex][channelIndex];
                    if (rowIndex % RowsPerBeat == 0)
                    {
                        cell.Note = MetronomeNote;
                        cell.InstrumentId = instrument.Id;
                        cell.Volume = MetronomeVolume(rowIndex % rowsPerBar == 0);
                    }
                    else
                    {
                        cell.Note = -1;
                    }
                }
            }
        }

        int MetronomeVolume(bool isDownbeat)
        {
            if (!MetronomeAccent)
                return MetronomePlainVolume;
            return isDownbeat ? MetronomeAccentVolume : MetronomeBeatVolume;
        }

        // Removes the pattern at `index`. Any OrderList entries referencing
        // it are dropped; entries referencing later patterns shift down by
        // one so they still point at the right pattern after the removal.
        // Refuses to remove the last remaining pattern (a song needs at
        // least one to be editable).
        public void RemovePattern(int index)
        {
            if (Patterns.Count <= 1 || index < 0 || index >= Patterns.Count)
                return;
            Patterns.RemoveAt(index);
            var newOrderList = new Array<int>();
            foreach (var entry in OrderList)
            {
                if (entry == index)
                    continue;
                newOrderList.Add(entry > index ? entry - 1 : entry);
            }
            OrderList = newOrderList;
        }

        // Smallest instrument id not already in use -- since ids aren't
        // simply array-index-based (an instrument can be removed from the
        // middle), a new instrument can't just reuse Instruments.Count.
        public int NextInstrumentId()
        {
            var maxId = -1;
            foreach (var instrument in Instruments)
                maxId = Mathf.Max(maxId, instrument.Id);
            return maxId + 1;
        }

        // Exact expected duration in seconds of one non-looping pass
        // through OrderList -- matches precisely how PlaybackState/
        // WavRenderer actually count samples, not the idealized
        // real-number tempo math: each row advances by
        // round(sampleRate * rowDurationSec) samples (an integer, since a
        // fixed-sample-rate digital audio stream can't have a fractional
        // sample count per row), so this is what an exported WAV's real
        // length should equal, to compare against for verifying render
        // precision.
        public float TotalDurationSec(int sampleRate = DefaultSampleRate)
        {
            var rowDurationSec = 60.0f / Tempo / RowsPerBeat;
            var samplesPerRow = Mathf.RoundToInt(sampleRate * rowDurationSec);
            var totalRows = 0;
            foreach (var patternIndex in OrderList)
            {
                if (patternIndex >= 0 && patternIndex < Patterns.Count)
                    totalRows += Patterns[patternIndex].Rows.Count;
            }
            return (float)(totalRows * samplesPerRow) / sampleRate;
        }
    }
}

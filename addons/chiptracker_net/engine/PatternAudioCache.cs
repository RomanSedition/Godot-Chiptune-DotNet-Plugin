using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Engine
{
    // C# counterpart of addons/chiptracker/engine/pattern_audio_cache.gd.
    // Pre-rendered per-pattern sample cache -- see "Pattern/song audio
    // cache" (M8) in CHIPTRACKER_SPEC.md. Rebuild() is the expensive part
    // (runs the same non-real-time PlaybackState loop WavRenderer uses,
    // once per pattern referenced by OrderList); OffsetTable()/SongStream()
    // are cheap array bookkeeping, cheap enough to call on every Play press
    // once the cache itself is clean.
    //
    // Dirty-tracking is deliberately coarse: a single MarkAllDirty() call
    // covers every ChiptrackerMainView.MarkDirty() call site (cell edits,
    // instrument edits, channel/rows-per-pattern changes, ...) rather than
    // threading a specific pattern index through each of them. Instrument
    // and channel edits can affect every pattern that uses them anyway, so
    // whole-cache invalidation is the correct behavior, not just the
    // simplest one -- and a full pattern re-render is fast enough at
    // realistic pattern sizes that the extra re-rendering this causes for
    // plain single-cell edits isn't worth optimizing away here.
    public class PatternAudioCache
    {
        const int SampleRate = PlaybackState.SampleRate;

        // internal rather than private: PlaybackLoopTest inspects rendered
        // buffers directly, the same way m9_rebuild_test.gd pokes
        // PatternAudioCache._buffers.
        internal readonly System.Collections.Generic.Dictionary<int, float[]> _buffers = new();
        bool _dirty = true;

        // Patterns marked stale on their own, for edits that can only
        // affect that one pattern's audio -- each pattern is rendered in
        // isolation, so a note edit needn't re-render the rest of the
        // song. Used by live recording so the rebuild at the loop point
        // stays small.
        readonly HashSet<int> _dirtyPatterns = new();

        public void MarkAllDirty() => _dirty = true;

        public void MarkPatternDirty(int patternIndex) => _dirtyPatterns.Add(patternIndex);

        public bool IsPatternDirty(int patternIndex) => _dirty || _dirtyPatterns.Contains(patternIndex);

        // True when every pattern referenced by song.OrderList has an
        // up-to-date rendered buffer -- i.e. Rebuild() has nothing left
        // to do.
        public bool IsClean(Song song)
        {
            if (_dirty || _dirtyPatterns.Count > 0)
                return false;
            foreach (var patternIndex in song.OrderList)
                if (!_buffers.ContainsKey(patternIndex))
                    return false;
            return true;
        }

        // Renders every pattern referenced by OrderList. Synchronous/
        // blocking -- moving this to a background Thread is explicitly
        // out of scope for M8. Call before relying on SongStream() or
        // OffsetTable() for playback.
        public void Rebuild(Song song)
        {
            var needed = new HashSet<int>(song.OrderList);
            foreach (var patternIndex in needed)
            {
                if (_dirty || _dirtyPatterns.Contains(patternIndex) || !_buffers.ContainsKey(patternIndex))
                    _buffers[patternIndex] = RenderPattern(song, patternIndex);
            }
            // A stale buffer for a pattern that isn't in the order list
            // must not survive to be reused if the pattern is added to it
            // later.
            foreach (var patternIndex in new List<int>(_buffers.Keys))
            {
                if (!needed.Contains(patternIndex) && (_dirty || _dirtyPatterns.Contains(patternIndex)))
                    _buffers.Remove(patternIndex);
            }
            _dirtyPatterns.Clear();
            _dirty = false;
        }

        // Cumulative starting sample index, in SongStream()'s
        // concatenated buffer, for each position in song.OrderList --
        // e.g. OffsetTable()[2] is where the third order-list entry's
        // audio begins. Mirrors Song.TotalDurationSec()'s per-pattern
        // summation, but in cached-buffer terms rather than idealized
        // tempo math.
        public Array<int> OffsetTable(Song song)
        {
            var offsets = new Array<int>();
            var running = 0;
            foreach (var patternIndex in song.OrderList)
            {
                offsets.Add(running);
                running += _buffers.TryGetValue(patternIndex, out var buf) ? buf.Length : 0;
            }
            return offsets;
        }

        // Length in samples of one pattern's cached buffer (0 if it
        // isn't cached).
        public int PatternSampleCount(int patternIndex) =>
            _buffers.TryGetValue(patternIndex, out var buf) ? buf.Length : 0;

        // Concatenates the cached per-pattern buffers in OrderList order
        // into one streamable WAV -- what an AudioStreamPlayer actually
        // plays from. Assumes Rebuild() has already made the cache
        // clean; a pattern still missing from the cache contributes
        // silence rather than crashing.
        public AudioStreamWav SongStream(Song song)
        {
            var total = 0;
            foreach (var patternIndex in song.OrderList)
                total += _buffers.TryGetValue(patternIndex, out var buf) ? buf.Length : 0;
            var samples = new float[total];
            var cursor = 0;
            foreach (var patternIndex in song.OrderList)
            {
                if (_buffers.TryGetValue(patternIndex, out var buf))
                {
                    System.Array.Copy(buf, 0, samples, cursor, buf.Length);
                    cursor += buf.Length;
                }
            }
            return new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = SampleRate,
                Stereo = false,
                Data = Synth.ToPcm16(samples),
            };
        }

        // Renders one pattern in isolation: a temporary Song that shares
        // this song's channels/instruments/patterns (Duplicate(false)
        // keeps those as the same references, only the top-level Song
        // wrapper and its own Array properties are copied) but with
        // OrderList narrowed to just this pattern, so PlaybackState's
        // normal non-real-time render loop (the same one WavRenderer
        // uses) produces exactly that pattern's audio.
        //
        // Explicitly out of scope per the spec: a note that would
        // normally sustain into the *next* pattern in the real order
        // list gets cut off at this pattern's last row instead, since
        // PlaybackState's row-search only searches within OrderList,
        // which here is just [patternIndex].
        static float[] RenderPattern(Song song, int patternIndex)
        {
            var isoSong = (Song)song.Duplicate(false);
            isoSong.OrderList = new Array<int> { patternIndex };
            var state = new PlaybackState(isoSong, false);
            state.PlayFrom();
            var samples = new List<float>();
            while (state.Playing)
                samples.Add(state.AdvanceSample());
            return samples.ToArray();
        }
    }
}

using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/tap_tempo_test.gd's _test_tap_tempo --
    // TapTempo's averaging/reset/debounce logic in isolation. NOT
    // ported: the reference file's _test_main_view, which needs the M8
    // audio cache and MIDI (AkaiMPKMini4/MidiKeyboard), both still
    // deferred. The TAP TEMPO button/backtick-key wiring itself is
    // exercised manually in the editor instead (see
    // ChiptrackerMainView.OnTapTempo).
    public partial class TapTempoTest : SceneTree
    {
        public override void _Initialize()
        {
            var failures = new List<string>();

            var t = new TapTempo();
            Check(failures, t.Tap(0) == -1, "the first tap has nothing to go on");
            Check(failures, t.Tap(500) == 120, "a second tap 500 ms later is 120 BPM");
            Check(failures, t.Tap(1000) == 120 && t.Tap(1500) == 120, "steady taps stay at 120");

            // Uneven taps average out over the span instead of adding up jitter.
            var jittery = new TapTempo();
            jittery.Tap(0);
            jittery.Tap(540); // late
            jittery.Tap(960); // early
            jittery.Tap(1500);
            Check(failures, jittery.Tap(2000) == 120, "jitter in the middle taps doesn't move the answer (span 0-2000 over four beats)");

            // A long pause starts a fresh count.
            var reset = new TapTempo();
            reset.Tap(0);
            reset.Tap(500);
            Check(failures, reset.Tap(5000) == -1, "after a pause the count starts over");
            Check(failures, reset.Tap(5500) == 120, "and the next tap works from the new start");
            var edge = new TapTempo();
            edge.Tap(0);
            Check(failures, edge.Tap(TapTempo.ResetAfterMsec) == 30, "a gap of exactly the reset time still counts (30 BPM)");

            // Double triggers are ignored and don't disturb the count.
            var debounce = new TapTempo();
            debounce.Tap(0);
            Check(failures, debounce.Tap(50) == -1, "a tap too soon after the last is ignored");
            Check(failures, debounce.Tap(500) == 120, "and doesn't count as a tap");

            // Only the recent taps are used, so it follows a change of pace.
            var pace = new TapTempo();
            long now = 0;
            for (var i = 0; i < 6; i++)
            {
                pace.Tap(now);
                now += 1000; // slow: 60 BPM
            }
            var result = -1;
            now -= 1000; // the loop above already advanced past the last slow tap
            for (var i = 0; i < 10; i++)
            {
                now += 500; // fast: 120 BPM
                result = pace.Tap(now);
            }
            Check(failures, result == 120, $"after speeding up, the old slow taps drop out of the average (got {result})");

            // Clamped to the tempo field's range.
            var fast = new TapTempo();
            fast.Tap(0);
            Check(failures, fast.Tap(TapTempo.MinGapMsec) == 400, "very fast tapping is capped at the tempo field's maximum");
            var slow = new TapTempo();
            slow.Tap(0);
            Check(failures, slow.Tap(1999) == 30, "slow tapping stays above the tempo field's minimum");

            var cleared = new TapTempo();
            cleared.Tap(0);
            cleared.Reset();
            Check(failures, cleared.Tap(500) == -1, "reset() forgets earlier taps");

            if (failures.Count == 0)
            {
                GD.Print("tap_tempo_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"tap_tempo_test: FAIL - {f}");
                GD.PrintErr($"tap_tempo_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }
    }
}

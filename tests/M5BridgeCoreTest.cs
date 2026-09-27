using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;
using ChiptrackerNet.Bridge;

namespace ChiptrackerNet.Tests
{
    // Covers the core MCP bridge commands that tests/bridge_extended_test.gd
    // (ported as M5BridgeExtendedTest.cs) doesn't: create_song, get_song
    // round-trip, add/remove/rename_channel, add/remove/rename_pattern,
    // set_note (including auto-grow), set_tempo, set_rows_per_pattern,
    // add_to_order, play_preview/stop_preview, render_wav, and the two
    // deliberately-unimplemented archetype commands. No single GDScript
    // reference file covers exactly this set (it's spread across
    // pitch_envelope_test.gd/envelope_times_test.gd/layered_instrument_test.gd
    // and the manual command_dispatcher.gd reading), so this is a fresh
    // test written directly against the command contract those files and
    // command_dispatcher.gd document. Run headless via:
    //   Godot_mono --headless --path chiptune-plugin --script res://tests/M5BridgeCoreTest.cs
    public partial class M5BridgeCoreTest : SceneTree
    {
        public override void _Initialize()
        {
            var failures = new List<string>();

            TestCreateSongAndGetSong(failures);
            TestChannelCommands(failures);
            TestPatternAndOrderCommands(failures);
            TestSetNoteAutoGrow(failures);
            TestTempoCommands(failures);
            TestPreviewCommands(failures);
            TestRenderWav(failures);
            TestUnknownAndArchetypeCommands(failures);

            if (failures.Count == 0)
            {
                GD.Print("M5 bridge_core_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M5 bridge_core_test: FAIL - {f}");
                GD.PrintErr($"M5 bridge_core_test: {failures.Count} failure(s)");
            }

            Quit(failures.Count == 0 ? 0 : 1);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        static bool Ok(Dictionary response) => response["ok"].AsBool();
        static Dictionary Result(Dictionary response) => response["result"].AsGodotDictionary();

        CommandDispatcher MakeDispatcher()
        {
            var dispatcher = new CommandDispatcher();
            Root.AddChild(dispatcher);
            // AddChild() defers _Ready() to the next process frame rather
            // than calling it synchronously; this test's _Initialize()
            // never yields one (no `await process_frame` equivalent), so
            // _previewPlayer/_previewEngine would otherwise stay null.
            // Force it deterministically instead of hanging on a frame
            // that will never come.
            dispatcher._Ready();
            return dispatcher;
        }

        void TestCreateSongAndGetSong(List<string> failures)
        {
            var d = MakeDispatcher();
            var created = d.Dispatch("create_song", new Dictionary
            {
                ["tempo"] = 140,
                ["rows_per_beat"] = 4,
                ["rows_per_pattern"] = 8,
                ["channels"] = new Array
                {
                    new Dictionary { ["name"] = "Lead", ["instrument_type"] = "square" },
                    new Dictionary { ["name"] = "Bass", ["instrument_type"] = "triangle" },
                },
            });
            Check(failures, Ok(created), "create_song succeeds with at least one channel");
            Check(failures, d.Song.Tempo == 140 && d.Song.Channels.Count == 2, "create_song applies tempo and channels");
            Check(failures, !Ok(d.Dispatch("create_song", new Dictionary())), "create_song rejects zero channels");

            var songDict = Result(d.Dispatch("get_song", new Dictionary()));
            Check(failures, songDict["tempo"].AsInt32() == 140, "get_song reports tempo");
            var channelsData = songDict["channels"].AsGodotArray();
            Check(failures, channelsData.Count == 2, "get_song reports every channel");
            Check(failures, channelsData[1].AsGodotDictionary()["instrument_type"].AsString() == "triangle", "get_song reports channel fields");
        }

        void TestChannelCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            d.Dispatch("create_song", new Dictionary { ["channels"] = new Array { new Dictionary { ["name"] = "Ch0" } } });

            var added = d.Dispatch("add_channel", new Dictionary { ["name"] = "Ch1", ["instrument_type"] = "noise" });
            Check(failures, Ok(added) && Result(added)["channel_index"].AsInt32() == 1, "add_channel appends");
            Check(failures, d.Song.Channels.Count == 2, "and the song now has two channels");
            Check(failures, !Ok(d.Dispatch("add_channel", new Dictionary { ["name"] = "Metronome" })), "add_channel refuses the reserved name");

            Check(failures, !Ok(d.Dispatch("remove_channel", new Dictionary { ["index"] = 99 })), "remove_channel refuses an out-of-range index");
            var removed = d.Dispatch("remove_channel", new Dictionary { ["index"] = 1 });
            Check(failures, Ok(removed) && d.Song.Channels.Count == 1, "remove_channel removes by index");
            Check(failures, !Ok(d.Dispatch("remove_channel", new Dictionary { ["index"] = 0 })), "remove_channel refuses the last remaining channel");
        }

        void TestPatternAndOrderCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            d.Dispatch("create_song", new Dictionary { ["channels"] = new Array { new Dictionary { ["name"] = "Ch0" } } });
            // create_song leaves order_list/patterns empty -- add one pattern to work with.
            var addedPattern = d.Dispatch("add_pattern", new Dictionary());
            Check(failures, Ok(addedPattern) && Result(addedPattern)["pattern_index"].AsInt32() == 0, "add_pattern appends the first pattern");

            var renamed = d.Dispatch("rename_pattern", new Dictionary { ["index"] = 0, ["name"] = "Intro" });
            Check(failures, Ok(renamed) && d.Song.Patterns[0].Name == "Intro", "rename_pattern renames");

            var addedToOrder = d.Dispatch("add_to_order", new Dictionary { ["pattern"] = 0 });
            Check(failures, Ok(addedToOrder) && Result(addedToOrder)["order_index"].AsInt32() == 0, "add_to_order appends to an empty order list");
            Check(failures, !Ok(d.Dispatch("add_to_order", new Dictionary { ["pattern"] = 5 })), "add_to_order refuses an out-of-range pattern");

            Check(failures, !Ok(d.Dispatch("remove_pattern", new Dictionary { ["index"] = 0 })), "remove_pattern refuses the last remaining pattern");
            d.Dispatch("add_pattern", new Dictionary());
            var removedPattern = d.Dispatch("remove_pattern", new Dictionary { ["index"] = 1 });
            Check(failures, Ok(removedPattern) && d.Song.Patterns.Count == 1, "remove_pattern removes a non-last pattern");
        }

        void TestSetNoteAutoGrow(List<string> failures)
        {
            var d = MakeDispatcher();
            d.Dispatch("create_song", new Dictionary { ["channels"] = new Array { new Dictionary { ["name"] = "Ch0" } } });
            Check(failures, d.Song.Patterns.Count == 0, "a fresh create_song has no patterns yet");

            // set_note on pattern 0 with no patterns yet should auto-grow.
            var set = d.Dispatch("set_note", new Dictionary { ["pattern"] = 0, ["row"] = 0, ["channel"] = 0, ["note"] = 60, ["volume"] = 12 });
            Check(failures, Ok(set), "set_note auto-grows a missing pattern");
            Check(failures, d.Song.Patterns.Count == 1 && d.Song.OrderList.Count == 1, "creating exactly one pattern and appending it to the order list");
            var cell = d.Song.Patterns[0].Rows[0][0].As<Engine.Cell>();
            Check(failures, cell.Note == 60 && cell.Volume == 12, "with the given note/volume");

            // Partial update: omitting a field leaves it as it was.
            d.Dispatch("set_note", new Dictionary { ["pattern"] = 0, ["row"] = 0, ["channel"] = 0, ["volume"] = 5 });
            Check(failures, cell.Note == 60 && cell.Volume == 5, "set_note is a partial update (note untouched, volume changed)");

            Check(failures, !Ok(d.Dispatch("set_note", new Dictionary { ["pattern"] = 0, ["row"] = 99, ["channel"] = 0 })), "set_note refuses an out-of-range row");
            Check(failures, !Ok(d.Dispatch("set_note", new Dictionary { ["pattern"] = 0, ["row"] = 0, ["channel"] = 99 })), "set_note refuses an out-of-range channel");
        }

        void TestTempoCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            d.Dispatch("create_song", new Dictionary { ["channels"] = new Array { new Dictionary { ["name"] = "Ch0" } } });

            Check(failures, Ok(d.Dispatch("set_tempo", new Dictionary { ["tempo"] = 90, ["rows_per_beat"] = 3 })), "set_tempo succeeds");
            Check(failures, d.Song.Tempo == 90 && d.Song.RowsPerBeat == 3, "and applies both fields");
            Check(failures, !Ok(d.Dispatch("set_tempo", new Dictionary())), "set_tempo requires tempo");

            d.Dispatch("add_pattern", new Dictionary());
            Check(failures, Ok(d.Dispatch("set_rows_per_pattern", new Dictionary { ["rows_per_pattern"] = 32 })), "set_rows_per_pattern succeeds");
            Check(failures, d.Song.RowsPerPattern == 32 && d.Song.Patterns[0].Rows.Count == 32, "and resizes existing patterns");
            Check(failures, !Ok(d.Dispatch("set_rows_per_pattern", new Dictionary { ["rows_per_pattern"] = 0 })), "set_rows_per_pattern rejects less than 1");
        }

        // Deliberately does NOT exercise play_preview's success path (which
        // calls the real AudioStreamPlayer.Play()/GetStreamPlayback()):
        // Godot's own timing_regression_test.gd needs `await process_frame`
        // (twice) before driving a real PlaybackEngine for exactly this
        // reason -- those APIs need at least one real engine frame tick to
        // be ready, which a synchronous (non-async) SceneTree._Initialize()
        // override never yields for. M3PlaybackTest already covers
        // PlaybackEngine/PlaybackState's real playback behavior directly;
        // this only checks play_preview's validation paths, which don't
        // touch audio at all.
        void TestPreviewCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            Check(failures, !Ok(d.Dispatch("play_preview", new Dictionary())), "play_preview refuses a song with no patterns");

            d.Dispatch("create_song", new Dictionary { ["channels"] = new Array { new Dictionary { ["name"] = "Ch0" } } });
            d.Dispatch("set_note", new Dictionary { ["pattern"] = 0, ["row"] = 0, ["channel"] = 0, ["note"] = 60 });
            Check(failures, !Ok(d.Dispatch("play_preview", new Dictionary { ["pattern"] = 5 })), "play_preview refuses a pattern not in the order list");

            // stop_preview is a no-op (not an error) when nothing was ever
            // played -- PlaybackEngine.Stop() guards on State == null.
            Check(failures, Ok(d.Dispatch("stop_preview", new Dictionary())), "stop_preview succeeds even with nothing playing");
        }

        void TestRenderWav(List<string> failures)
        {
            var d = MakeDispatcher();
            d.Dispatch("create_song", new Dictionary { ["channels"] = new Array { new Dictionary { ["name"] = "Ch0" } } });
            d.Dispatch("set_note", new Dictionary { ["pattern"] = 0, ["row"] = 0, ["channel"] = 0, ["note"] = 60 });

            const string path = "user://m5_bridge_core_render.wav";
            var rendered = d.Dispatch("render_wav", new Dictionary { ["output_path"] = path });
            Check(failures, Ok(rendered) && Result(rendered)["path"].AsString() == path, "render_wav renders and reports the path");
            Check(failures, FileAccess.FileExists(path), "and the file actually exists");
            Check(failures, !Ok(d.Dispatch("render_wav", new Dictionary())), "render_wav requires output_path");
        }

        void TestUnknownAndArchetypeCommands(List<string> failures)
        {
            var d = MakeDispatcher();
            var unknown = d.Dispatch("not_a_real_command", new Dictionary());
            Check(failures, !Ok(unknown), "an unknown command is rejected");

            // Deep coverage of EnvelopeArchetypes itself lives in
            // EnvelopeArchetypesTest.cs (the port of
            // envelope_archetypes_test.gd) -- this just checks the bridge
            // commands wire into it correctly.
            var archetypes = d.Dispatch("list_archetypes", new Dictionary());
            Check(failures, Ok(archetypes), "list_archetypes succeeds");
            var archetypesResult = Result(archetypes);
            Check(failures, archetypesResult["archetypes"].AsGodotArray().Count == 20, "and lists all twenty archetypes");
            Check(failures, archetypesResult["max_nodes"].AsInt32() == 60, "and reports max_nodes");

            var random = d.Dispatch("random_instrument", new Dictionary { ["archetype"] = "kick", ["seed"] = 1 });
            Check(failures, Ok(random), "random_instrument succeeds for a known archetype");
            var randomResult = Result(random);
            Check(failures, randomResult["archetype"].AsString().StartsWith("Kick"), "and picks a matching archetype by substring");
            Check(failures, randomResult["envelope"].AsGodotArray().Count > 0, "and returns a non-empty envelope");
            Check(failures, d.Song.Instruments.Count == 1, "and actually adds the instrument to the song");

            var repeat = d.Dispatch("random_instrument", new Dictionary { ["archetype"] = "kick", ["seed"] = 1 });
            Check(failures, Result(repeat)["envelope"].AsGodotArray().SequenceEqual(randomResult["envelope"].AsGodotArray()), "the same seed reproduces the same generated envelope");

            Check(failures, !Ok(d.Dispatch("random_instrument", new Dictionary { ["archetype"] = "not-a-real-archetype" })), "random_instrument refuses an unknown archetype");
        }
    }
}

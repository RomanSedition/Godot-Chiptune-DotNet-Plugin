using System.Collections.Generic;
using Godot;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;
using ChiptrackerNet.Bridge;

namespace ChiptrackerNet.Tests
{
    // C# counterpart of tests/m7_song_node_test.gd -- ChiptrackerSongNode:
    // a Node that holds a Song as scene-persisted data. Verifies the node
    // exposes a valid default Song, that binding the main view to it
    // shares the same Resource (so edits land in the node's data), that
    // create_song-style full replacement re-points the node's export
    // rather than leaving it stale, and that EditorBridge.RebindSong()
    // keeps the dispatcher pointed at whichever node is currently bound.
    public partial class M7SongNodeTest : SceneTree
    {
        public override async void _Initialize()
        {
            var failures = new List<string>();

            var node = new ChiptrackerSongNode();
            Check(failures, node.Song != null, "ChiptrackerSongNode has a default song");
            Check(failures, node.Song.Channels.Count == 0, "default song starts with no channels");

            var mainViewScene = GD.Load<PackedScene>("res://addons/chiptracker_net/ui/chiptracker_main_view.tscn");
            var mainView = mainViewScene.Instantiate<ChiptrackerMainView>();
            Root.AddChild(mainView);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);
            await ToSignal(this, SceneTree.SignalName.ProcessFrame);

            var originalDefaultSong = mainView.Song;
            Check(failures, originalDefaultSong != null, "main view starts with its own default song");

            mainView.BindSongNode(node);
            Check(failures, mainView.BoundSongNode == node, "BindSongNode records the bound node");
            Check(failures, mainView.Song == node.Song, "BindSongNode points the tab's song at the node's song (same reference)");
            Check(failures, mainView.Song != originalDefaultSong, "binding replaces the tab's previous scratch song");

            // Mutating through the tab writes straight into the node's
            // data, since it's the same Resource -- this is what makes
            // scene-save persistence work without any explicit copy-back step.
            var channel = new Channel { Name = "Lead", InstrumentType = "square" };
            mainView.Song.AddChannel(channel);
            Check(failures, node.Song.Channels.Count == 1, "mutating mainView.Song is visible on node.Song (shared reference)");
            Check(failures, node.Song.Channels[0].Name == "Lead", "the actual channel data landed on the node");

            // MarkDirty() should no-op safely outside a real editor (this
            // headless harness isn't a real running editor, so
            // EditorInterface isn't the live one -- same guard pattern as
            // ChiptrackerDock.Icon()).
            mainView.MarkDirtyExternal();
            Check(failures, true, "MarkDirty does not crash outside a real editor session");

            // create_song-style full replacement (dispatcher.Song = new
            // Song()) must re-point the node's own export, not just the
            // tab's local Song, or the node would silently keep holding
            // stale data after a full reset.
            var replacementSong = new Song { Tempo = 140 };
            mainView.SetSong(replacementSong);
            mainView.BoundSongNode.Song = replacementSong;
            Check(failures, node.Song == replacementSong, "a full song replacement re-points the bound node's export");
            Check(failures, node.Song.Tempo == 140, "the node's song reflects the replacement's data");

            // Regression: EditorBridge.Setup() snapshots mainView.Song
            // into dispatcher.Song ONCE. Binding a different node later
            // (as ChiptrackerNetPlugin's _Edit() does on Scene-dock
            // selection) must explicitly push the new song into the
            // dispatcher too via RebindSong(), or MCP commands would keep
            // silently operating on the stale original song even though
            // the tab visibly shows the newly bound node's data.
            var bridge = new EditorBridge();
            Root.AddChild(bridge);
            bridge.Setup(mainView);
            Check(failures, bridge._dispatcher.Song == mainView.Song, "editor bridge dispatcher starts pointed at main_view's current song");

            var secondNode = new ChiptrackerSongNode();
            secondNode.Song.Tempo = 200;
            mainView.BindSongNode(secondNode);
            bridge.RebindSong(secondNode.Song);
            Check(failures, bridge._dispatcher.Song == secondNode.Song, "RebindSong re-points the dispatcher at the newly bound node's song");
            Check(failures, bridge._dispatcher.Song.Tempo == 200, "the dispatcher now sees the newly bound node's actual data");
            bridge.QueueFree();

            Finish(failures);
        }

        static void Check(List<string> failures, bool condition, string label)
        {
            if (!condition)
                failures.Add(label);
        }

        void Finish(List<string> failures)
        {
            if (failures.Count == 0)
            {
                GD.Print("M7 song_node_test: PASS");
            }
            else
            {
                foreach (var f in failures)
                    GD.PrintErr($"M7 song_node_test: FAIL - {f}");
                GD.PrintErr($"M7 song_node_test: {failures.Count} failure(s)");
            }
            Quit(failures.Count == 0 ? 0 : 1);
        }
    }
}

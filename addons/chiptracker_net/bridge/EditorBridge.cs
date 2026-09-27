#if TOOLS
using Godot;
using ChiptrackerNet.UI;

namespace ChiptrackerNet.Bridge
{
    // C# counterpart of addons/chiptracker/bridge/editor_bridge.gd. Same
    // wire protocol as McpBridge, but hosted directly by
    // ChiptrackerNetPlugin (added as a child in its _EnterTree) so it's
    // alive whenever the editor is open, not just during Play/headless
    // runs -- and points at the currently-open Chiptracker .Net tab's
    // live Song instead of a private one. External tool calls
    // (create_song/set_note/add_channel/etc.) mutate that shared Song
    // directly and refresh the tab's UI immediately.
    //
    // Port 7780, NOT the GDScript editor bridge's 7778, for the same
    // reason McpBridge uses 7779 instead of 7777.
    [Tool]
    public partial class EditorBridge : Node
    {
        public const int Port = 7780;

        readonly BridgeTransport _transport = new() { LogPrefix = "ChiptrackerNetEditorBridge" };
        internal CommandDispatcher _dispatcher;

        public void Setup(ChiptrackerMainView mainView)
        {
            _dispatcher = new CommandDispatcher { Song = mainView.Song };
            // internal, not public: same-assembly access from here is
            // fine, and these fields don't need to be part of
            // ChiptrackerMainView's own public surface just for this.
            _dispatcher.SetPreviewTarget(mainView._playbackEngine, mainView._audioPlayer);
            _dispatcher.OnSongMutated = fullReset =>
            {
                if (fullReset)
                {
                    mainView.SetSong(_dispatcher.Song);
                    // create_song replaces the Song object outright rather
                    // than mutating it in place -- if the tab is bound to
                    // a scene node, repoint the node's own export at the
                    // new Song too, or its data would silently stop
                    // matching what the tab displays.
                    if (mainView.BoundSongNode != null)
                        mainView.BoundSongNode.Song = _dispatcher.Song;
                }
                else
                {
                    mainView.Refresh();
                }
                mainView.MarkDirtyExternal();
            };
            AddChild(_dispatcher);

            _transport.Dispatcher = _dispatcher;
            _transport.Listen(Port);
        }

        // Dispatcher.Song is a snapshot taken at Setup() time, not a live
        // link to the main view's Song -- when something OUTSIDE the
        // dispatcher's own mutations repoints it (namely
        // ChiptrackerMainView.BindSongNode(), triggered by selecting a
        // ChiptrackerSongNode in the Scene dock), the dispatcher would
        // otherwise keep operating on the old song forever.
        // ChiptrackerNetPlugin calls this right after BindSongNode() to
        // keep them in sync.
        public void RebindSong(Engine.Song newSong) => _dispatcher.Song = newSong;

        public override void _Process(double delta) => _transport.Poll();

        // Frees port 7780 for the next plugin load -- see BridgeTransport.Stop().
        public override void _ExitTree() => _transport.Stop();
    }
}
#endif

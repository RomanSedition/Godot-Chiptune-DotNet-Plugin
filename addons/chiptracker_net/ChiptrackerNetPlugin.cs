#if TOOLS
using Godot;
using ChiptrackerNet.Engine;
using ChiptrackerNet.UI;
using ChiptrackerNet.Bridge;

namespace ChiptrackerNet
{
    // Editor entry point for the C#/.NET port. Referenced by plugin.cfg's
    // "script" field. Mirrors addons/chiptracker/plugin.gd's shell
    // (main-screen tab + bottom dock + bridge autoload + editor bridge +
    // ChiptrackerSongNode scene binding).
    [Tool]
    public partial class ChiptrackerNetPlugin : EditorPlugin
    {
        const string AutoloadName = "ChiptrackerNetBridge";
        const string AutoloadPath = "res://addons/chiptracker_net/bridge/McpBridge.cs";
        const string MainViewScenePath = "res://addons/chiptracker_net/ui/chiptracker_main_view.tscn";
        const string DockScenePath = "res://addons/chiptracker_net/ui/chiptracker_dock.tscn";

        ChiptrackerMainView _mainView;
        ChiptrackerDock _dock;
        EditorBridge _editorBridge;

        // Tracks the last scene root a ChiptrackerSongNode auto-bind was
        // resolved against, so _Process() only re-scans when the
        // actually-edited scene changes (switching scene tabs) rather
        // than every frame.
        Node _lastEditedSceneRoot;

        public override void _EnterTree()
        {
            AddAutoloadSingleton(AutoloadName, AutoloadPath);

            _mainView = GD.Load<PackedScene>(MainViewScenePath).Instantiate<ChiptrackerMainView>();
            EditorInterface.Singleton.GetEditorMainScreen().AddChild(_mainView);
            _mainView.Visible = false;

            _dock = GD.Load<PackedScene>(DockScenePath).Instantiate<ChiptrackerDock>();
            AddControlToBottomPanel(_dock, "Chiptracker .Net");
            _mainView.SetDock(_dock);

            _editorBridge = new EditorBridge();
            AddChild(_editorBridge);
            _editorBridge.Setup(_mainView);

            SetProcess(true);
        }

        // Godot has no direct "current scene tab changed" signal exposed
        // to plugins -- selecting a node in the Scene dock DOES fire
        // _Edit(), but switching scene tabs alone does not touch the
        // selection at all, so a ChiptrackerSongNode bound in scene A
        // stays bound even after switching to scene B's own (different)
        // node. Poll the edited scene root instead and auto-bind the
        // first ChiptrackerSongNode found in whichever scene is actually
        // open, so switching tabs shows that scene's own song.
        public override void _Process(double delta)
        {
            var currentRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            if (currentRoot == _lastEditedSceneRoot)
                return;
            _lastEditedSceneRoot = currentRoot;
            var node = FindSongNode(currentRoot);
            if (node != null)
                BindSongNode(node);
        }

        static ChiptrackerSongNode FindSongNode(Node node)
        {
            if (node == null)
                return null;
            if (node is ChiptrackerSongNode songNode)
                return songNode;
            foreach (var child in node.GetChildren())
            {
                var found = FindSongNode(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        public override void _ExitTree()
        {
            RemoveAutoloadSingleton(AutoloadName);

            if (_editorBridge != null)
            {
                _editorBridge.QueueFree();
                _editorBridge = null;
            }

            if (_dock != null)
            {
                RemoveControlFromBottomPanel(_dock);
                _dock.QueueFree();
                _dock = null;
            }

            if (_mainView != null)
            {
                _mainView.QueueFree();
                _mainView = null;
            }
        }

        public override bool _HasMainScreen() => true;

        public override void _MakeVisible(bool visible)
        {
            if (_mainView != null)
                _mainView.Visible = visible;
        }

        public override string _GetPluginName() => "Chiptracker .Net";

        public override Texture2D _GetPluginIcon() =>
            EditorInterface.Singleton.GetBaseControl().GetThemeIcon("AudioStreamPlayer", "EditorIcons");

        // Selecting a ChiptrackerSongNode in the Scene dock binds the
        // Chiptracker .Net tab to its Song (and switches to the tab, same
        // as selecting a Node3D switches to the 3D tab) -- edits then
        // write straight into that node's data, so saving the scene
        // persists them. Deselecting it (or selecting anything else)
        // leaves the tab on whatever it was last showing rather than
        // resetting it.
        public override bool _Handles(GodotObject @object) => @object is ChiptrackerSongNode;

        public override void _Edit(GodotObject @object)
        {
            if (@object is ChiptrackerSongNode songNode)
                BindSongNode(songNode);
        }

        void BindSongNode(ChiptrackerSongNode node)
        {
            _mainView?.BindSongNode(node);
            _editorBridge?.RebindSong(node.Song);
        }
    }
}
#endif

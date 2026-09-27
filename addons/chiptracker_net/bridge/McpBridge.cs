using Godot;

namespace ChiptrackerNet.Bridge
{
    // C# counterpart of addons/chiptracker/bridge/mcp_bridge.gd. Autoload
    // singleton "ChiptrackerNetBridge". Live only while the project is
    // actually running (Play or headless), operating on a private
    // in-memory Song.
    //
    // Port 7779, NOT the GDScript bridge's 7777 -- both addons can be
    // enabled in this same project simultaneously (see
    // CHIPTRACKER_SPEC.md's port-picking guidance), so they need
    // genuinely different ports to coexist.
    [Tool]
    public partial class McpBridge : Node
    {
        public const int Port = 7779;

        readonly BridgeTransport _transport = new() { LogPrefix = "ChiptrackerNetBridge" };
        CommandDispatcher _dispatcher;

        public override void _Ready()
        {
            _dispatcher = new CommandDispatcher();
            AddChild(_dispatcher);
            _transport.Dispatcher = _dispatcher;
            _transport.Listen(Port);
        }

        public override void _Process(double delta) => _transport.Poll();

        // Frees port 7779 for the next plugin load -- see BridgeTransport.Stop().
        public override void _ExitTree() => _transport.Stop();
    }
}

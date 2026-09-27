using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace ChiptrackerNet.Bridge
{
    // C# counterpart of addons/chiptracker/bridge/bridge_transport.gd.
    // Shared plain WebSocket server (TcpServer + WebSocketPeer, no
    // external transport library), used by both McpBridge (Play-time
    // autoload) and EditorBridge (editor-resident). Parses one JSON
    // command per message, dispatches it to `Dispatcher`, and replies
    // with one JSON response per request.
    //
    // Plain C# class, not a Node -- the owning Node (McpBridge/
    // EditorBridge) calls Poll() from its own _Process, the same way
    // PlaybackEngine drives PlaybackState. This sidesteps the two owners
    // needing different [Tool] behavior (McpBridge is play-time-only in
    // spirit, EditorBridge is editor-resident) without duplicating the
    // socket-handling code.
    public class BridgeTransport
    {
        public CommandDispatcher Dispatcher;
        public string LogPrefix = "ChiptrackerBridgeTransport";

        readonly TcpServer _tcpServer = new();
        readonly List<WebSocketPeer> _peers = new();

        public void Listen(int port)
        {
            var err = _tcpServer.Listen((ushort)port);
            if (err != Error.Ok)
                GD.PushError($"{LogPrefix}: failed to listen on port {port} (error {err})");
            else
                GD.Print($"{LogPrefix}: listening on ws://127.0.0.1:{port}");
        }

        // Closes the listening socket and drops every open peer. The
        // owning Node must call this from its _ExitTree: Godot frees the
        // Node, but the TcpServer is a RefCounted held by this plain C#
        // object and goes on holding the OS port until it is collected --
        // long enough that re-enabling the plugin hits AlreadyInUse and
        // the bridge is dead for the rest of the editor session. Plugin
        // reload is the normal way to pick up a rebuild here, so leaking
        // the port once is enough to break it.
        public void Stop()
        {
            foreach (var peer in _peers)
                peer.Close();
            _peers.Clear();
            _tcpServer.Stop();
        }

        public void Poll()
        {
            while (_tcpServer.IsConnectionAvailable())
            {
                var conn = _tcpServer.TakeConnection();
                var ws = new WebSocketPeer();
                if (ws.AcceptStream(conn) == Error.Ok)
                    _peers.Add(ws);
            }

            for (var i = _peers.Count - 1; i >= 0; i--)
            {
                var peer = _peers[i];
                peer.Poll();
                switch (peer.GetReadyState())
                {
                    case WebSocketPeer.State.Open:
                        while (peer.GetAvailablePacketCount() > 0)
                            HandlePacket(peer, peer.GetPacket());
                        break;
                    case WebSocketPeer.State.Closed:
                        _peers.RemoveAt(i);
                        break;
                }
            }
        }

        void HandlePacket(WebSocketPeer peer, byte[] packet)
        {
            var parsed = Json.ParseString(packet.GetStringFromUtf8());
            if (parsed.VariantType != Variant.Type.Dictionary)
            {
                Send(peer, "", false, new Dictionary(), "malformed request: expected {id, command, params}");
                return;
            }
            var request = parsed.AsGodotDictionary();
            if (!request.ContainsKey("command"))
            {
                Send(peer, "", false, new Dictionary(), "malformed request: expected {id, command, params}");
                return;
            }

            var id = request.ContainsKey("id") ? request["id"].AsString() : "";
            var command = request["command"].AsString();
            var parameters = request.ContainsKey("params") ? request["params"].AsGodotDictionary() : new Dictionary();

            var response = Dispatcher.Dispatch(command, parameters);
            var ok = response.ContainsKey("ok") && response["ok"].AsBool();
            if (ok)
                Send(peer, id, true, response["result"].AsGodotDictionary(), "");
            else
                Send(peer, id, false, new Dictionary(), response.ContainsKey("error") ? response["error"].AsString() : "unknown error");
        }

        void Send(WebSocketPeer peer, string id, bool ok, Dictionary result, string error)
        {
            var response = new Dictionary { ["id"] = id, ["ok"] = ok };
            if (ok)
                response["result"] = result;
            else
                response["error"] = error;
            peer.SendText(Json.Stringify(response));
        }
    }
}

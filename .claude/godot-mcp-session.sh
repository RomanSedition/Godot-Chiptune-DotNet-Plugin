#!/usr/bin/env bash
# Manage the standalone GameDev-MCP server (Godot-MCP's C#-side editor-control
# server) for the lifetime of a Claude session, for the ChipTracke .Net
# project.
#
#   start : if the server isn't already answering on $PORT, launch the cached
#           binary detached (survives this hook process). Records our PID so
#           `stop` only kills a server THIS session started.
#   stop  : kill the server iff we started it (marker present + PID alive).
#
# Emits SessionStart hookSpecificOutput.additionalContext so Claude reports status.
# Why standalone: a C# rebuild makes the godot_mcp addon kill any server IT
# spawned; a server we own survives, and the editor just reconnects.
#
# Port 29650, distinct from the original "Godot Chiptune Plugin" project's
# 29649 (and Ivan Murzak's own default 29648) so both projects can run their
# editors at the same time without colliding on this generic MCP server.
# Note: the chiptracker_net bridge itself (ChiptrackerNetBridge / EditorBridge,
# ports 7779/7780) is NOT similarly isolated -- those ports are hardcoded in
# the copied C# source, so only one of the two projects' editors can be open
# at a time if you want the chiptracker_* MCP tools to work reliably.
set -u

REPO_ROOT="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
GODOT_DIR="$REPO_ROOT"
PORT=29650
BIN="$GODOT_DIR/.godot/mcp-server/osx-arm64/gamedev-mcp-server"
ARGS="port=$PORT plugin-timeout=10000 client-transport=streamableHttp auth=none"
LOG="$GODOT_DIR/.godot/mcp-server/session-server.log"
MARKER="$GODOT_DIR/.godot/mcp-server/.started-by-claude"

emit() { printf '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":%s}}\n' "$1"; }

is_up() {
  curl -sS -m 2 -o /dev/null -X POST \
    "http://localhost:$PORT/api/system-tools/ping" -d '{}' 2>/dev/null
}

case "${1:-start}" in
  start)
    if is_up; then
      emit '"Godot MCP server (ChipTracke .Net) is already running on port 29650. Tell the user it is up; they should NOT click Start Server in the dock."'
      exit 0
    fi
    if [ ! -x "$BIN" ]; then
      emit '"Godot MCP server binary is missing at .godot/mcp-server/osx-arm64/. Tell the user to open this project in Godot_mono.app once (with the godot_mcp plugin enabled) so the addon downloads it, then start a new session."'
      exit 0
    fi
    ( cd "$GODOT_DIR" && nohup "$BIN" $ARGS >"$LOG" 2>&1 & echo $! >"$MARKER"; disown ) >/dev/null 2>&1
    for _ in $(seq 1 24); do is_up && break; sleep 0.25; done
    if is_up; then
      emit '"Started the Godot MCP server (standalone, ChipTracke .Net, port 29650) for this session. Tell the user it is up and that they should NOT click Start Server in the dock -- it now shows External. It is stopped automatically when this session ends."'
    else
      emit '"Tried to start the Godot MCP server but it did not become ready in time. Tell the user to check .godot/mcp-server/session-server.log."'
    fi
    ;;
  stop)
    if [ -f "$MARKER" ]; then
      PID="$(cat "$MARKER" 2>/dev/null || true)"
      if [ -n "${PID:-}" ] && kill -0 "$PID" 2>/dev/null; then
        kill "$PID" 2>/dev/null || true
      fi
      rm -f "$MARKER"
    fi
    ;;
esac
exit 0

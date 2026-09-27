# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "mcp>=1.2.0,<2",
#     "websockets>=13.0",
# ]
# ///
"""Chiptracker MCP server.

Bridges MCP tool calls to the Chiptracker Godot bridge's WebSocket command
protocol (see CHIPTRACKER_SPEC.md, "MCP command protocol"). Two independent
bridges exist on the Godot side, both speaking the same protocol:

- ChiptrackerBridge (ws://127.0.0.1:7777): live only while the project is
  actually running (Play or headless), operating on a private in-memory
  song. Used by chiptracker_* tools.
- ChiptrackerEditorBridge (ws://127.0.0.1:7778): live whenever the editor is
  open, operating directly on whatever Song the Chiptracker main-screen tab
  currently has loaded, so tool calls show up in that tab immediately. Used
  by chiptracker_editor_* tools.

Run with: uv run server.py
"""

import asyncio
import json
import uuid
from typing import Any, Optional

import websockets
from mcp.server.fastmcp import FastMCP

import os

# Overridable so the same script can also front the C#/.NET port's bridge
# (chiptracker_net addon, ports 7779/7780 -- see CHIPTRACKER_SPEC.md's
# "C#/.NET port" section) via a second .mcp.json server entry with
# CHIPTRACKER_PLAY_BRIDGE_URL/CHIPTRACKER_EDITOR_BRIDGE_URL set, instead of
# duplicating this whole file. Defaults match the GDScript addon's ports, so
# the existing "chiptracker" server entry keeps working unchanged.
PLAY_BRIDGE_URL = os.environ.get("CHIPTRACKER_PLAY_BRIDGE_URL", "ws://127.0.0.1:7777")
EDITOR_BRIDGE_URL = os.environ.get("CHIPTRACKER_EDITOR_BRIDGE_URL", "ws://127.0.0.1:7778")

mcp = FastMCP(os.environ.get("CHIPTRACKER_MCP_SERVER_NAME", "chiptracker"))


class BridgeClient:
    """One persistent WebSocket connection to a single Chiptracker bridge."""

    # A dead-but-not-closed connection (e.g. the Godot editor process was
    # quit/killed/relaunched without a clean WebSocket close handshake --
    # routine here, since restarting the editor is the normal way to pick
    # up plugin code changes) leaves ws.recv() waiting forever: it has no
    # built-in timeout, and a half-dead TCP connection doesn't always
    # raise ConnectionClosed on its own. Without this, one stale
    # connection silently wedges every subsequent tool call for the rest
    # of the server process's life.
    RECV_TIMEOUT = 60.0  # seconds

    def __init__(self, url: str):
        self._url = url
        self._lock = asyncio.Lock()
        self._ws = None

    async def _get_connection(self):
        async with self._lock:
            if self._ws is None:
                self._ws = await websockets.connect(self._url)
            return self._ws

    async def _send_and_recv(self, ws, request: dict[str, Any]) -> str:
        await ws.send(json.dumps(request))
        return await asyncio.wait_for(ws.recv(), timeout=self.RECV_TIMEOUT)

    async def call(self, command: str, params: Optional[dict[str, Any]] = None) -> dict[str, Any]:
        request = {"id": str(uuid.uuid4()), "command": command, "params": params or {}}
        ws = await self._get_connection()
        try:
            raw = await self._send_and_recv(ws, request)
        except (websockets.exceptions.ConnectionClosed, asyncio.TimeoutError):
            # One retry against a fresh connection -- covers both a cleanly
            # closed connection and a silently dead one. A second failure
            # (or a second timeout) is real and propagates.
            self._ws = None
            ws = await self._get_connection()
            raw = await self._send_and_recv(ws, request)

        response = json.loads(raw)
        if not response.get("ok"):
            raise RuntimeError(response.get("error", "unknown bridge error"))
        return response.get("result", {})


play_bridge = BridgeClient(PLAY_BRIDGE_URL)
editor_bridge = BridgeClient(EDITOR_BRIDGE_URL)


def _channel_list_or_empty(channels: Optional[list[dict[str, str]]]) -> list[dict[str, str]]:
    return channels or []


# --- Play-time / headless bridge (a private song, only live during Play) ---


@mcp.tool()
async def chiptracker_create_song(
    tempo: int = 120,
    rows_per_beat: int = 4,
    rows_per_pattern: int = 16,
    channels: Optional[list[dict[str, str]]] = None,
) -> dict[str, Any]:
    """Create a new song, replacing whatever the Play-time bridge currently holds.

    channels: list of {"name": str, "instrument_type": "square"|"triangle"|"noise"}.
    Requires at least one channel. Requires the project to be running (Play
    or headless), NOT just the editor open -- see chiptracker_editor_create_song
    for editing the open Chiptracker tab instead.
    """
    return await play_bridge.call("create_song", {
        "tempo": tempo,
        "rows_per_beat": rows_per_beat,
        "rows_per_pattern": rows_per_pattern,
        "channels": _channel_list_or_empty(channels),
    })


@mcp.tool()
async def chiptracker_load_song(path: str) -> dict[str, Any]:
    """Replace whatever the Play-time bridge currently holds by loading a Song
    resource wholesale from `path` (a res:// path to an existing .tres Song).

    For bulk imports where hundreds/thousands of individual set_note calls
    would be impractical (e.g. converting an external tracker/notation
    format) -- build the Song resource first (e.g. a headless GDScript run
    that saves a .tres), then load it here in one call.
    """
    return await play_bridge.call("load_song", {"path": path})


@mcp.tool()
async def chiptracker_get_song() -> dict[str, Any]:
    """Return the Play-time bridge's full current song state as JSON."""
    return await play_bridge.call("get_song")


@mcp.tool()
async def chiptracker_add_instrument(
    waveform: str = "square",
    duty_cycle: float = 0.5,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
    layers: Optional[list[dict[str, Any]]] = None,
) -> dict[str, Any]:
    """Add an instrument (waveform: square|triangle|noise|sine) to the Play-time bridge's song; returns its id.

    envelope is a list of levels (0-1). envelope_times optionally gives where
    each level sits along the note (0-1, one per level, never decreasing);
    leave it out for evenly spaced points.

    pitch_envelope is an optional pitch bend curve, in semitones (signed, 0 =
    the note's written pitch) -- independent of the volume envelope, with its
    own point count and (via pitch_envelope_times) its own timing. Left out,
    there's no pitch bend. pitch_range sets how far the instrument panel's
    pitch graph axis reaches and how far RANDOMIZE's pitch curve is clamped
    (default 24 semitones); it doesn't itself limit pitch_envelope's values.

    layers is an optional list of additional instruments that play alongside
    this one whenever it's triggered (e.g. a power chord's fifth and octave).
    Each entry is a dict with the same fields as this call itself (waveform,
    duty_cycle, envelope, envelope_times, pitch_envelope,
    pitch_envelope_times, pitch_range -- a layer can't have layers of its
    own), plus step_offset (int, semitones above/below the triggering note,
    default 0) and fixed_note_enabled/fixed_note (when enabled, the layer
    always plays fixed_note instead, ignoring both the triggering note and
    step_offset). Layers have no id of their own and can't be assigned to a
    cell directly -- only this instrument can be."""
    params: dict[str, Any] = {
        "waveform": waveform,
        "duty_cycle": duty_cycle,
        "envelope": envelope or [],
        "envelope_times": envelope_times or [],
        "pitch_envelope": pitch_envelope or [],
        "pitch_envelope_times": pitch_envelope_times or [],
    }
    if pitch_range is not None:
        params["pitch_range"] = pitch_range
    if layers is not None:
        params["layers"] = layers
    return await play_bridge.call("add_instrument", params)


@mcp.tool()
async def chiptracker_list_archetypes() -> dict[str, Any]:
    """List the envelope archetypes (kicks, snares, leads, basses, pads...) that random_instrument can make, with each one's usual waveform, whether it has a pitch curve too (has_pitch), and the node-count limit."""
    return await play_bridge.call("list_archetypes", {})


@mcp.tool()
async def chiptracker_random_instrument(
    archetype: str,
    node_count: int = 8,
    pitch_node_count: Optional[int] = None,
    pitch_range: int = 24,
    waveform: Optional[str] = None,
    duty_cycle: Optional[float] = None,
    name: Optional[str] = None,
    seed: Optional[int] = None,
) -> dict[str, Any]:
    """Add an instrument with a randomised envelope in the shape of a named archetype; returns its id and the envelope.

    archetype is a name or part of one ("kick", "open hi-hat", "theremin") or
    an index from list_archetypes. node_count is how many envelope points
    (1-60, default 8). waveform (square|triangle|noise|sine) defaults to the
    one that suits the archetype. Each call varies the shape a little; pass
    seed to repeat one.

    For an archetype with a pitch curve (list_archetypes' has_pitch), a
    matching pitch envelope is generated too, clamped to +/-pitch_range
    semitones (default 24) with pitch_node_count points (defaults to
    node_count). An archetype with no pitch curve leaves pitch flat."""
    params: dict[str, Any] = {"archetype": archetype, "node_count": node_count, "pitch_range": pitch_range}
    if pitch_node_count is not None:
        params["pitch_node_count"] = pitch_node_count
    for key, value in (("waveform", waveform), ("duty_cycle", duty_cycle), ("name", name), ("seed", seed)):
        if value is not None:
            params[key] = value
    return await play_bridge.call("random_instrument", params)


@mcp.tool()
async def chiptracker_update_instrument(
    id: int,
    waveform: Optional[str] = None,
    duty_cycle: Optional[float] = None,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
) -> dict[str, Any]:
    """Change an existing instrument's own fields in place (never its layers -- see update_layer). Only the fields you pass are changed.

    id is the instrument's id (from add_instrument/random_instrument or get_song), not its position in the list. Refused for the reserved Metronome instrument."""
    params: dict[str, Any] = {"id": id}
    for key, value in (
        ("waveform", waveform), ("duty_cycle", duty_cycle), ("envelope", envelope),
        ("envelope_times", envelope_times), ("pitch_envelope", pitch_envelope),
        ("pitch_envelope_times", pitch_envelope_times), ("pitch_range", pitch_range),
    ):
        if value is not None:
            params[key] = value
    return await play_bridge.call("update_instrument", params)


@mcp.tool()
async def chiptracker_rename_instrument(id: int, name: str) -> dict[str, Any]:
    """Rename an instrument by id. Refused for the reserved Metronome instrument, or a name of "Metronome" (reserved)."""
    return await play_bridge.call("rename_instrument", {"id": id, "name": name})


@mcp.tool()
async def chiptracker_remove_instrument(id: int) -> dict[str, Any]:
    """Remove an instrument by id. Refused for the reserved Metronome instrument (use remove_metronome instead). A cell still referencing a removed instrument's id is left as-is and simply plays nothing, same as in the dock."""
    return await play_bridge.call("remove_instrument", {"id": id})


@mcp.tool()
async def chiptracker_duplicate_instrument(id: int) -> dict[str, Any]:
    """Deep-copy an instrument, including its layers (each an independent copy, not a shared reference), as a new instrument with a fresh id and, if the original was named, " (Copy)" appended. Returns the new id."""
    return await play_bridge.call("duplicate_instrument", {"id": id})


@mcp.tool()
async def chiptracker_add_layer(
    instrument_id: int,
    waveform: str = "square",
    duty_cycle: float = 0.5,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
    step_offset: int = 0,
    fixed_note_enabled: bool = False,
    fixed_note: int = 60,
) -> dict[str, Any]:
    """Add a layer to an existing instrument -- an additional instrument that plays alongside it (e.g. a power chord's fifth and octave). Same fields as add_instrument's own (a layer can't have layers of its own), plus step_offset (semitones above/below the triggering note) and fixed_note_enabled/fixed_note (when enabled, this layer always plays fixed_note instead, ignoring both the triggering note and step_offset). Returns the new layer's index (for update_layer/remove_layer). Refused for the reserved Metronome instrument."""
    params: dict[str, Any] = {
        "instrument_id": instrument_id,
        "waveform": waveform,
        "duty_cycle": duty_cycle,
        "envelope": envelope or [],
        "envelope_times": envelope_times or [],
        "pitch_envelope": pitch_envelope or [],
        "pitch_envelope_times": pitch_envelope_times or [],
        "step_offset": step_offset,
        "fixed_note_enabled": fixed_note_enabled,
        "fixed_note": fixed_note,
    }
    if pitch_range is not None:
        params["pitch_range"] = pitch_range
    return await play_bridge.call("add_layer", params)


@mcp.tool()
async def chiptracker_update_layer(
    instrument_id: int,
    layer_index: int,
    waveform: Optional[str] = None,
    duty_cycle: Optional[float] = None,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
    step_offset: Optional[int] = None,
    fixed_note_enabled: Optional[bool] = None,
    fixed_note: Optional[int] = None,
) -> dict[str, Any]:
    """Change one of an instrument's existing layers in place, by its index (from add_layer, or position in get_song's layers list). Only the fields you pass are changed."""
    params: dict[str, Any] = {"instrument_id": instrument_id, "layer_index": layer_index}
    for key, value in (
        ("waveform", waveform), ("duty_cycle", duty_cycle), ("envelope", envelope),
        ("envelope_times", envelope_times), ("pitch_envelope", pitch_envelope),
        ("pitch_envelope_times", pitch_envelope_times), ("pitch_range", pitch_range),
        ("step_offset", step_offset), ("fixed_note_enabled", fixed_note_enabled), ("fixed_note", fixed_note),
    ):
        if value is not None:
            params[key] = value
    return await play_bridge.call("update_layer", params)


@mcp.tool()
async def chiptracker_remove_layer(instrument_id: int, layer_index: int) -> dict[str, Any]:
    """Remove one of an instrument's layers by its index."""
    return await play_bridge.call("remove_layer", {"instrument_id": instrument_id, "layer_index": layer_index})


@mcp.tool()
async def chiptracker_add_pattern() -> dict[str, Any]:
    """Create a new empty pattern in the Play-time bridge's song; returns its index. Not auto-added to the order list."""
    return await play_bridge.call("add_pattern")


@mcp.tool()
async def chiptracker_remove_pattern(index: int) -> dict[str, Any]:
    """Remove the pattern at `index` from the Play-time bridge's song. Order-list entries referencing it are dropped; later entries shift down."""
    return await play_bridge.call("remove_pattern", {"index": index})


@mcp.tool()
async def chiptracker_rename_pattern(index: int, name: str) -> dict[str, Any]:
    """Rename the pattern at `index` in the Play-time bridge's song."""
    return await play_bridge.call("rename_pattern", {"index": index, "name": name})


@mcp.tool()
async def chiptracker_set_note(
    pattern: int,
    row: int,
    channel: int,
    note: int,
    instrument_id: int = 0,
    volume: int = 15,
) -> dict[str, Any]:
    """Set a cell's note (MIDI number, -1 = empty), instrument, and volume (0-15) in the Play-time bridge's song.

    A pattern index one past the current end auto-creates a new pattern and
    appends it to the order list.
    """
    return await play_bridge.call("set_note", {
        "pattern": pattern,
        "row": row,
        "channel": channel,
        "note": note,
        "instrument_id": instrument_id,
        "volume": volume,
    })


@mcp.tool()
async def chiptracker_set_tempo(tempo: int, rows_per_beat: Optional[int] = None) -> dict[str, Any]:
    """Update the Play-time bridge's song tempo (BPM) and optionally rows_per_beat."""
    params: dict[str, Any] = {"tempo": tempo}
    if rows_per_beat is not None:
        params["rows_per_beat"] = rows_per_beat
    return await play_bridge.call("set_tempo", params)


@mcp.tool()
async def chiptracker_set_rows_per_pattern(rows_per_pattern: int, pattern: Optional[int] = None) -> dict[str, Any]:
    """Resize patterns in the Play-time bridge's song, preserving existing notes (growing appends empty rows, shrinking truncates).

    With `pattern`, resizes only that pattern index. Without it, resizes EVERY pattern and sets
    the song's default length for newly-added patterns. Patterns are independently sized, so
    prefer passing `pattern` unless you really mean to re-length the whole song.
    """
    params: dict[str, Any] = {"rows_per_pattern": rows_per_pattern}
    if pattern is not None:
        params["pattern"] = pattern
    return await play_bridge.call("set_rows_per_pattern", params)


@mcp.tool()
async def chiptracker_play_preview(pattern: Optional[int] = None, row: int = 0) -> dict[str, Any]:
    """Start real-time preview playback of the Play-time bridge's song, looping from `pattern`/`row`."""
    params: dict[str, Any] = {"row": row}
    if pattern is not None:
        params["pattern"] = pattern
    return await play_bridge.call("play_preview", params)


@mcp.tool()
async def chiptracker_stop_preview() -> dict[str, Any]:
    """Stop the Play-time bridge's preview playback."""
    return await play_bridge.call("stop_preview")


@mcp.tool()
async def chiptracker_render_wav(output_path: str) -> dict[str, Any]:
    """Render the Play-time bridge's current song to a WAV file.

    output_path is a Godot res:// path (relative to the Godot project) or an
    absolute filesystem path the Godot process can write to.
    """
    return await play_bridge.call("render_wav", {"output_path": output_path})


# --- Editor bridge (the currently-open Chiptracker tab's live song) ---


@mcp.tool()
async def chiptracker_editor_get_song() -> dict[str, Any]:
    """Return the full song state currently open in the Chiptracker editor tab."""
    return await editor_bridge.call("get_song")


@mcp.tool()
async def chiptracker_editor_create_song(
    tempo: int = 120,
    rows_per_beat: int = 4,
    rows_per_pattern: int = 16,
    channels: Optional[list[dict[str, str]]] = None,
) -> dict[str, Any]:
    """Replace the song open in the Chiptracker editor tab with a brand new one.

    channels: list of {"name": str, "instrument_type": "square"|"triangle"|"noise"}.
    Requires at least one channel. This discards whatever was open in the tab.
    """
    return await editor_bridge.call("create_song", {
        "tempo": tempo,
        "rows_per_beat": rows_per_beat,
        "rows_per_pattern": rows_per_pattern,
        "channels": _channel_list_or_empty(channels),
    })


@mcp.tool()
async def chiptracker_editor_load_song(path: str) -> dict[str, Any]:
    """Replace the song open in the Chiptracker editor tab by loading a Song
    resource wholesale from `path` (a res:// path to an existing .tres Song).

    For bulk imports where hundreds/thousands of individual set_note calls
    would be impractical (e.g. converting an external tracker/notation
    format) -- build the Song resource first (e.g. a headless GDScript run
    that saves a .tres), then load it here in one call. Discards whatever
    was open in the tab.
    """
    return await editor_bridge.call("load_song", {"path": path})


@mcp.tool()
async def chiptracker_editor_add_instrument(
    waveform: str = "square",
    duty_cycle: float = 0.5,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
    layers: Optional[list[dict[str, Any]]] = None,
) -> dict[str, Any]:
    """Add an instrument (waveform: square|triangle|noise|sine) to the open tab's song; returns its id.

    envelope is a list of levels (0-1). envelope_times optionally gives where
    each level sits along the note (0-1, one per level, never decreasing);
    leave it out for evenly spaced points.

    pitch_envelope is an optional pitch bend curve, in semitones (signed, 0 =
    the note's written pitch) -- independent of the volume envelope, with its
    own point count and (via pitch_envelope_times) its own timing. Left out,
    there's no pitch bend. pitch_range sets how far the instrument panel's
    pitch graph axis reaches and how far RANDOMIZE's pitch curve is clamped
    (default 24 semitones); it doesn't itself limit pitch_envelope's values.

    layers is an optional list of additional instruments that play alongside
    this one whenever it's triggered (e.g. a power chord's fifth and octave).
    Each entry is a dict with the same fields as this call itself (waveform,
    duty_cycle, envelope, envelope_times, pitch_envelope,
    pitch_envelope_times, pitch_range -- a layer can't have layers of its
    own), plus step_offset (int, semitones above/below the triggering note,
    default 0) and fixed_note_enabled/fixed_note (when enabled, the layer
    always plays fixed_note instead, ignoring both the triggering note and
    step_offset). Layers have no id of their own and can't be assigned to a
    cell directly -- only this instrument can be."""
    params: dict[str, Any] = {
        "waveform": waveform,
        "duty_cycle": duty_cycle,
        "envelope": envelope or [],
        "envelope_times": envelope_times or [],
        "pitch_envelope": pitch_envelope or [],
        "pitch_envelope_times": pitch_envelope_times or [],
    }
    if pitch_range is not None:
        params["pitch_range"] = pitch_range
    if layers is not None:
        params["layers"] = layers
    return await editor_bridge.call("add_instrument", params)


@mcp.tool()
async def chiptracker_editor_list_archetypes() -> dict[str, Any]:
    """List the envelope archetypes (kicks, snares, leads, basses, pads...) that random_instrument can make, with each one's usual waveform, whether it has a pitch curve too (has_pitch), and the node-count limit."""
    return await editor_bridge.call("list_archetypes", {})


@mcp.tool()
async def chiptracker_editor_random_instrument(
    archetype: str,
    node_count: int = 8,
    pitch_node_count: Optional[int] = None,
    pitch_range: int = 24,
    waveform: Optional[str] = None,
    duty_cycle: Optional[float] = None,
    name: Optional[str] = None,
    seed: Optional[int] = None,
) -> dict[str, Any]:
    """Add an instrument with a randomised envelope in the shape of a named archetype; returns its id and the envelope.

    archetype is a name or part of one ("kick", "open hi-hat", "theremin") or
    an index from list_archetypes. node_count is how many envelope points
    (1-60, default 8). waveform (square|triangle|noise|sine) defaults to the
    one that suits the archetype. Each call varies the shape a little; pass
    seed to repeat one.

    For an archetype with a pitch curve (list_archetypes' has_pitch), a
    matching pitch envelope is generated too, clamped to +/-pitch_range
    semitones (default 24) with pitch_node_count points (defaults to
    node_count). An archetype with no pitch curve leaves pitch flat."""
    params: dict[str, Any] = {"archetype": archetype, "node_count": node_count, "pitch_range": pitch_range}
    if pitch_node_count is not None:
        params["pitch_node_count"] = pitch_node_count
    for key, value in (("waveform", waveform), ("duty_cycle", duty_cycle), ("name", name), ("seed", seed)):
        if value is not None:
            params[key] = value
    return await editor_bridge.call("random_instrument", params)


@mcp.tool()
async def chiptracker_editor_update_instrument(
    id: int,
    waveform: Optional[str] = None,
    duty_cycle: Optional[float] = None,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
) -> dict[str, Any]:
    """Change an existing instrument's own fields in place (never its layers -- see update_layer). Only the fields you pass are changed.

    id is the instrument's id (from add_instrument/random_instrument or get_song), not its position in the list. Refused for the reserved Metronome instrument."""
    params: dict[str, Any] = {"id": id}
    for key, value in (
        ("waveform", waveform), ("duty_cycle", duty_cycle), ("envelope", envelope),
        ("envelope_times", envelope_times), ("pitch_envelope", pitch_envelope),
        ("pitch_envelope_times", pitch_envelope_times), ("pitch_range", pitch_range),
    ):
        if value is not None:
            params[key] = value
    return await editor_bridge.call("update_instrument", params)


@mcp.tool()
async def chiptracker_editor_rename_instrument(id: int, name: str) -> dict[str, Any]:
    """Rename an instrument by id. Refused for the reserved Metronome instrument, or a name of "Metronome" (reserved)."""
    return await editor_bridge.call("rename_instrument", {"id": id, "name": name})


@mcp.tool()
async def chiptracker_editor_remove_instrument(id: int) -> dict[str, Any]:
    """Remove an instrument by id. Refused for the reserved Metronome instrument (use remove_metronome instead). A cell still referencing a removed instrument's id is left as-is and simply plays nothing, same as in the dock."""
    return await editor_bridge.call("remove_instrument", {"id": id})


@mcp.tool()
async def chiptracker_editor_duplicate_instrument(id: int) -> dict[str, Any]:
    """Deep-copy an instrument, including its layers (each an independent copy, not a shared reference), as a new instrument with a fresh id and, if the original was named, " (Copy)" appended. Returns the new id."""
    return await editor_bridge.call("duplicate_instrument", {"id": id})


@mcp.tool()
async def chiptracker_editor_add_layer(
    instrument_id: int,
    waveform: str = "square",
    duty_cycle: float = 0.5,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
    step_offset: int = 0,
    fixed_note_enabled: bool = False,
    fixed_note: int = 60,
) -> dict[str, Any]:
    """Add a layer to an existing instrument -- an additional instrument that plays alongside it (e.g. a power chord's fifth and octave). Same fields as add_instrument's own (a layer can't have layers of its own), plus step_offset (semitones above/below the triggering note) and fixed_note_enabled/fixed_note (when enabled, this layer always plays fixed_note instead, ignoring both the triggering note and step_offset). Returns the new layer's index (for update_layer/remove_layer). Refused for the reserved Metronome instrument."""
    params: dict[str, Any] = {
        "instrument_id": instrument_id,
        "waveform": waveform,
        "duty_cycle": duty_cycle,
        "envelope": envelope or [],
        "envelope_times": envelope_times or [],
        "pitch_envelope": pitch_envelope or [],
        "pitch_envelope_times": pitch_envelope_times or [],
        "step_offset": step_offset,
        "fixed_note_enabled": fixed_note_enabled,
        "fixed_note": fixed_note,
    }
    if pitch_range is not None:
        params["pitch_range"] = pitch_range
    return await editor_bridge.call("add_layer", params)


@mcp.tool()
async def chiptracker_editor_update_layer(
    instrument_id: int,
    layer_index: int,
    waveform: Optional[str] = None,
    duty_cycle: Optional[float] = None,
    envelope: Optional[list[float]] = None,
    envelope_times: Optional[list[float]] = None,
    pitch_envelope: Optional[list[float]] = None,
    pitch_envelope_times: Optional[list[float]] = None,
    pitch_range: Optional[int] = None,
    step_offset: Optional[int] = None,
    fixed_note_enabled: Optional[bool] = None,
    fixed_note: Optional[int] = None,
) -> dict[str, Any]:
    """Change one of an instrument's existing layers in place, by its index (from add_layer, or position in get_song's layers list). Only the fields you pass are changed."""
    params: dict[str, Any] = {"instrument_id": instrument_id, "layer_index": layer_index}
    for key, value in (
        ("waveform", waveform), ("duty_cycle", duty_cycle), ("envelope", envelope),
        ("envelope_times", envelope_times), ("pitch_envelope", pitch_envelope),
        ("pitch_envelope_times", pitch_envelope_times), ("pitch_range", pitch_range),
        ("step_offset", step_offset), ("fixed_note_enabled", fixed_note_enabled), ("fixed_note", fixed_note),
    ):
        if value is not None:
            params[key] = value
    return await editor_bridge.call("update_layer", params)


@mcp.tool()
async def chiptracker_editor_remove_layer(instrument_id: int, layer_index: int) -> dict[str, Any]:
    """Remove one of an instrument's layers by its index."""
    return await editor_bridge.call("remove_layer", {"instrument_id": instrument_id, "layer_index": layer_index})


@mcp.tool()
async def chiptracker_editor_add_pattern() -> dict[str, Any]:
    """Create a new empty pattern in the open tab's song; returns its index. Not auto-added to the order list."""
    return await editor_bridge.call("add_pattern")


@mcp.tool()
async def chiptracker_editor_remove_pattern(index: int) -> dict[str, Any]:
    """Remove the pattern at `index` from the open tab's song. Order-list entries referencing it are dropped; later entries shift down."""
    return await editor_bridge.call("remove_pattern", {"index": index})


@mcp.tool()
async def chiptracker_editor_rename_pattern(index: int, name: str) -> dict[str, Any]:
    """Rename the pattern at `index` in the open tab's song."""
    return await editor_bridge.call("rename_pattern", {"index": index, "name": name})


@mcp.tool()
async def chiptracker_editor_add_channel(
    name: Optional[str] = None,
    instrument_type: str = "square",
) -> dict[str, Any]:
    """Add a channel to the open tab's song (e.g. a "Drums" channel using "noise"). Returns its index.

    Grows every existing pattern's rows to match -- existing cells on other
    channels are untouched.
    """
    params: dict[str, Any] = {"instrument_type": instrument_type}
    if name is not None:
        params["name"] = name
    return await editor_bridge.call("add_channel", params)


@mcp.tool()
async def chiptracker_editor_remove_channel(index: int) -> dict[str, Any]:
    """Remove the channel at `index` from the open tab's song. Refuses to remove the last remaining channel."""
    return await editor_bridge.call("remove_channel", {"index": index})


@mcp.tool()
async def chiptracker_editor_rename_channel(index: int, name: str) -> dict[str, Any]:
    """Rename a channel by its index. Refused for the reserved Metronome channel, or a name of "Metronome" (reserved)."""
    return await editor_bridge.call("rename_channel", {"index": index, "name": name})


@mcp.tool()
async def chiptracker_editor_update_channel(
    index: int,
    muted: Optional[bool] = None,
    solo: Optional[bool] = None,
    instrument_type: Optional[str] = None,
) -> dict[str, Any]:
    """Set a channel's muted/solo state and/or its instrument_type (square|triangle|noise). Only the fields you pass are changed. The Metronome channel's mute/solo can still be set, but not its instrument_type."""
    params: dict[str, Any] = {"index": index}
    if muted is not None:
        params["muted"] = muted
    if solo is not None:
        params["solo"] = solo
    if instrument_type is not None:
        params["instrument_type"] = instrument_type
    return await editor_bridge.call("update_channel", params)


@mcp.tool()
async def chiptracker_editor_move_channel_to_group(channel_index: int, group_index: int) -> dict[str, Any]:
    """Reassign a channel to a different group, by absolute target group index (not a one-step move like the dock's arrows). Refused for the reserved Metronome channel/group."""
    return await editor_bridge.call("move_channel_to_group", {"channel_index": channel_index, "group_index": group_index})


@mcp.tool()
async def chiptracker_editor_add_group() -> dict[str, Any]:
    """Add a new, empty channel group. Returns its index."""
    return await editor_bridge.call("add_group", {})


@mcp.tool()
async def chiptracker_editor_remove_group(index: int) -> dict[str, Any]:
    """Remove a channel group by index, folding its channels into the group immediately to its left. Refused for Group 1 (index 0), the last remaining group, or the reserved Metronome group."""
    return await editor_bridge.call("remove_group", {"index": index})


@mcp.tool()
async def chiptracker_editor_rename_group(index: int, name: str) -> dict[str, Any]:
    """Rename a channel group by index. Refused for the reserved Metronome group, or a name of "Metronome" (reserved)."""
    return await editor_bridge.call("rename_group", {"index": index, "name": name})


@mcp.tool()
async def chiptracker_editor_add_metronome() -> dict[str, Any]:
    """Add the metronome: a reserved instrument, group and channel, ticking every beat of every pattern. Refused if the song already has one."""
    return await editor_bridge.call("add_metronome", {})


@mcp.tool()
async def chiptracker_editor_remove_metronome() -> dict[str, Any]:
    """Remove the metronome's channel, group and instrument together. Refused if there's no metronome, or if it's the song's only channel."""
    return await editor_bridge.call("remove_metronome", {})


@mcp.tool()
async def chiptracker_editor_add_to_order(pattern: int) -> dict[str, Any]:
    """Append a pattern (by its index in the patterns list) to the order list. Returns the new entry's position in the order list."""
    return await editor_bridge.call("add_to_order", {"pattern": pattern})


@mcp.tool()
async def chiptracker_editor_remove_from_order(order_index: int) -> dict[str, Any]:
    """Remove an entry from the order list, by its position in the order list (not the pattern's own index)."""
    return await editor_bridge.call("remove_from_order", {"order_index": order_index})


@mcp.tool()
async def chiptracker_editor_move_order_entry(order_index: int, direction: int) -> dict[str, Any]:
    """Swap an order-list entry with its neighbour: direction -1 moves it earlier, 1 moves it later. Refused at either end of the list."""
    return await editor_bridge.call("move_order_entry", {"order_index": order_index, "direction": direction})


@mcp.tool()
async def chiptracker_editor_set_note(
    pattern: int,
    row: int,
    channel: int,
    note: int,
    instrument_id: int = 0,
    volume: int = 15,
) -> dict[str, Any]:
    """Set a cell's note (MIDI number, -1 = empty), instrument, and volume (0-15) in the open tab's song.

    A pattern index one past the current end auto-creates a new pattern and
    appends it to the order list.
    """
    return await editor_bridge.call("set_note", {
        "pattern": pattern,
        "row": row,
        "channel": channel,
        "note": note,
        "instrument_id": instrument_id,
        "volume": volume,
    })


@mcp.tool()
async def chiptracker_editor_set_tempo(tempo: int, rows_per_beat: Optional[int] = None) -> dict[str, Any]:
    """Update the open tab's song tempo (BPM) and optionally rows_per_beat."""
    params: dict[str, Any] = {"tempo": tempo}
    if rows_per_beat is not None:
        params["rows_per_beat"] = rows_per_beat
    return await editor_bridge.call("set_tempo", params)


@mcp.tool()
async def chiptracker_editor_set_rows_per_pattern(rows_per_pattern: int, pattern: Optional[int] = None) -> dict[str, Any]:
    """Resize patterns in the open tab's song, preserving existing notes (growing appends empty rows, shrinking truncates).

    With `pattern`, resizes only that pattern index. Without it, resizes EVERY pattern and sets
    the song's default length for newly-added patterns. Patterns are independently sized, so
    prefer passing `pattern` unless you really mean to re-length the whole song.
    """
    params: dict[str, Any] = {"rows_per_pattern": rows_per_pattern}
    if pattern is not None:
        params["pattern"] = pattern
    return await editor_bridge.call("set_rows_per_pattern", params)


@mcp.tool()
async def chiptracker_editor_play_preview(pattern: Optional[int] = None, row: int = 0) -> dict[str, Any]:
    """Start real-time preview playback of the open tab's song, using its own Play button's engine."""
    params: dict[str, Any] = {"row": row}
    if pattern is not None:
        params["pattern"] = pattern
    return await editor_bridge.call("play_preview", params)


@mcp.tool()
async def chiptracker_editor_stop_preview() -> dict[str, Any]:
    """Stop the open tab's preview playback."""
    return await editor_bridge.call("stop_preview")


@mcp.tool()
async def chiptracker_editor_render_wav(output_path: str) -> dict[str, Any]:
    """Render the open tab's current song to a WAV file.

    output_path is a Godot res:// path (relative to the Godot project) or an
    absolute filesystem path the Godot process can write to.
    """
    return await editor_bridge.call("render_wav", {"output_path": output_path})


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()

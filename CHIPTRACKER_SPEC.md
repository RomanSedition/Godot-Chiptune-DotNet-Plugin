# Chiptracker: Godot Chiptune Tracker + MCP Bridge

## Planned / under discussion (do not build yet)

This section holds ideas being actively refined in the planning chat that are not yet ready to implement. Read it for context on where things are headed, but do not build anything listed here until it has been moved into the relevant section above (data model, milestones, etc.) — that move is the signal it's ready. If something here would change a decision already made elsewhere in this doc, flag it rather than guessing which one wins.

### Envelopes in seconds, not fractions of the note

Envelope point times are fractions of the note's length (`Instrument.envelope_times`,
0-1), so an envelope's shape stretches with the note: a short note gets a fast
decay and a long note a slow one. A tracker's volume macro (Furnace, FamiTracker)
instead runs in absolute ticks, at 60 a second here, whatever the note length. This
matters when transcribing a `.fur` song: notes here sustain until the next note, so
the same instrument sounds different at different note lengths, and one fixed
shape only matches one length.

An `envelope_seconds` option on `Instrument` was built and tried (times in seconds
from the note's start, the last level held after them; the synth change was a
one-line choice of the time axis) and then reverted, because the fractions model
is the one wanted for now. If it comes back, this is what it needs beyond the
synth change: a seconds-aware instrument panel (the graph, its point boxes and
RANDOMIZE all work in fractions and were locked for such an instrument), an
`envelope_seconds` parameter on the `add_instrument` MCP tools, and an answer to
how a hybrid works (a note-off/release stage that stretches to the note's end,
as real trackers do, with the attack and decay in seconds).

The synth, `Instrument`, bridge, MCP tool and panel-locking parts, with a passing test,
are saved as `chiptune-plugin/patches/envelope_seconds.patch` (apply from the repo root
with `git apply chiptune-plugin/patches/envelope_seconds.patch`; it adds
`tests/envelope_seconds_test.gd`). It does not include the seconds-based Shovel Knight
song that was generated to try it, and adding the `envelope_seconds` member to
`Instrument` means restarting Godot after applying it. It is a snapshot of the
code as of the envelope-archetype commits, so it may need rebasing if those files
change.

Other Furnace macros (pitch, arpeggio, duty) have no equivalent in the instrument
model either, so a `.fur` transcription can match volume but not those.

### Playback performance: closing the gap with standalone trackers

Raised after chasing several real timing/stutter bugs in the M6/M7
playback path (buffer-length prefill skip, bursty buffer-availability
fills, pacing-carry loss under backpressure — see git history around
`PlaybackEngine._process()`). All of those are now fixed and a
`Song.total_duration_sec()` + `tests/timing_regression_test.gd` pair
exists to catch regressions automatically. The ideas below are about
closing the *remaining*, more fundamental gap with how standalone
trackers (FamiTracker, OpenMPT, Renoise, DefleMask, etc.) architect
audio, not about a known bug:

- **Move sample generation off the main thread.** Everything today —
  mixing, sequencing, grid redraws, MCP polling — runs on Godot's single
  main thread inside `_process()`. Standalone trackers run mixing on a
  dedicated high-priority OS audio callback thread, fully isolated from
  UI work, so a slow redraw or dialog can never cause an audio glitch.
  Doing the same here (a Godot `Thread` for `PlaybackState.advance_sample()`
  generation, feeding the `AudioStreamGeneratorPlayback` ring buffer)
  would be the single biggest robustness win, but introduces real
  thread-safety risk: the `Song`/`Cell` data being edited on the main
  thread while a background thread reads it for playback needs a
  locking or snapshot strategy that doesn't exist yet.
- **Streaming/block-based synthesis instead of generate-ahead-of-time.**
  `Synth.generate_buffer()` synthesizes a triggered note's *entire*
  duration synchronously in one call the instant it starts, which
  concentrates CPU cost into a spike at row boundaries (worse the more
  channels trigger notes on the same row) instead of spreading it evenly
  over time. Real synthesis engines compute audio in small fixed-size
  blocks (e.g. 64-128 samples) continuously regardless of note
  triggers. Would smooth out exactly the kind of dense-arrangement
  stutter observed with the 6-channel/64-row stress-test song.
  Meaningfully larger change than the thread move above — touches the
  whole Synth/PlaybackState contract, not just where generation runs.
- **Not planned: hand-vectorized/SIMD mixing, or rewriting the mixing
  loop in GDExtension/C++.** Would close the "compiled vs. interpreted"
  performance gap (GDScript is roughly one to two orders of magnitude
  slower than the C/C++ real trackers use for this), but is a much
  bigger commitment (a native build step, per-platform binaries) for a
  project that's explicitly meant to be a drop-in, pure-GDScript addon
  (see "Portability / exportability"). Only worth reconsidering if the
  thread move + streaming synthesis above turn out not to be enough. See
  the C#/.NET alternative below for a lighter way to close roughly the
  same gap.

### C#/.NET as an alternative implementation (dual-track plan)

Moved out of this section and into "C#/.NET port" below now that setup has
actually started — see there for the current state and rationale.

## Sync protocol

This spec is mirrored in a secret GitHub gist so it stays consistent across
Godot/Claude Code sessions and a separate planning chat. Rules:

- **At the start of any session working on this project**: `git pull` in the
  local gist clone (or `curl` the raw gist URL) before reading or editing
  this file, so you're working from the latest version. Also check the
  "Planned / under discussion" section for anything relevant to the work
  about to happen — it's context, not a build instruction, but ignoring it
  risks building something that's about to change.
- **After making any change to this file**: commit and push back to the
  gist immediately (`git add CHIPTRACKER_SPEC.md && git commit -m "..." &&
  git push`) — don't batch up edits across a long session, since another
  party may pull mid-session.
- Treat this file as the single source of truth for scope, data model, and
  the MCP command contract. If code and this doc disagree, update this doc
  in the same change that resolves the disagreement — don't let it drift.
- Gist clone lives at: `~/ChipTune/chiptracker-spec`
- Gist URL: `https://gist.github.com/RomanSedition/eeb619f5a3b37c3373ac6d5779c7278d`

## Purpose of this document

This is the working spec for an agentic build session in Godot. It defines what
we're building, the architecture, the milestones in build order, and the
conventions to follow. Read this in full before writing code. When a decision
isn't specified here, prefer the simplest option that unblocks the next
milestone, and note the choice in `DECISIONS.md` (create it if it doesn't
exist) rather than asking — unless it affects the data model or MCP contract,
in which case flag it.

## What we're building

A chiptune tracker (pattern-based music sequencer, in the style of
FamiTracker/OpenMPT) implemented as a Godot project, with two ways to drive it:

1. **As a Godot app/editor plugin** — a human uses the UI directly.
2. **As an MCP-controllable "song engine"** — an external MCP server (Python
   or TS) sends commands over a local socket to mutate song state and trigger
   playback/render, so an LLM can compose/edit music programmatically.

Godot owns: data model, synthesis, real-time playback, WAV export, and
(eventually) the pattern-grid UI. The MCP protocol itself is NOT implemented
in GDScript — a thin socket bridge is the only thing Godot needs to expose.

## Non-goals (for now)

- No authentic chip emulation (no NES 2A03 register-accurate emulation, no
  blip_buf integration). Waveforms are synthesized approximations
  (square/triangle/noise/simple FM). This can be a later upgrade behind the
  same instrument interface.
- No import/export of real tracker formats (.xm, .it, .nsf) in v1. Native
  `.tres`-based song format only. Format converters are a stretch goal.
- No networking beyond localhost. The MCP bridge is not designed to be
  exposed remotely.
- No the Godot EditorPlugin dock UI until the engine + bridge are solid.
  Building the pattern-grid UI too early risks designing it around a data
  model that hasn't proven itself yet.

## Architecture overview

```
[MCP Server: Python/TS]  <--WebSocket (localhost)-->  [Godot: autoload bridge]
                                                              |
                                                    mutates Song resource
                                                              |
                                                     [Playback engine] --> AudioStreamGenerator --> speakers
                                                              |
                                                     [Render engine] --> AudioEffectRecord --> .wav file
```

- MCP server: owns MCP transport (stdio/JSON-RPC to the LLM side), translates
  tool calls into a small JSON command protocol, sends over WebSocket to
  Godot, returns Godot's JSON responses as tool results.
- Godot bridge (autoload singleton, `res://autoload/mcp_bridge.gd`): a
  `WebSocketPeer` (or `TCPServer` if simpler) server that Godot runs while the
  project is open (or headless). Parses incoming JSON commands, calls into
  the song/engine API, serializes results back.
- Song/engine layer: pure Godot, no knowledge that MCP exists. This layer
  should be fully testable and usable by a human directly through UI, with
  the bridge as just another caller.

Keep the bridge as a thin translation layer. All actual logic (validation,
mutation, playback) lives in the engine layer so the same code path is
exercised whether a human or an LLM is driving it.

## Data model

Represent everything as Godot `Resource` subclasses so they get free
serialization to `.tres`, inspector editing, and undo/redo integration later.

- `Song` (Resource)
  - `tempo: int` (BPM)
  - `rows_per_beat: int` (default 4; row duration = `60.0 / tempo / rows_per_beat` seconds)
  - `rows_per_pattern: int`
  - `channels: Array[Channel]`
  - `order_list: Array[int]` (sequence of pattern indices)
  - `patterns: Array[Pattern]`
  - `instruments: Array[Instrument]`
  - `groups: Array[ChannelGroup]` (always at least one; index 0 is "Group 1" and can't be removed)
  - `metronome_accent: bool` (default true; whether the first beat of each bar ticks louder — see "Metronome")

- `ChannelGroup` (Resource)
  - `name: String` (empty means display as "Group N" by index)

- `Channel` (Resource)
  - `group_index: int` (index into `Song.groups`; which Groups-tab column the channel is listed under)
  - `name: String`
  - `instrument_type: String` (e.g. "square", "triangle", "noise")
  - `muted: bool` (default false) — excluded from playback/render mixing
  - `solo: bool` (default false) — if any channel has `solo == true`, only
    soloed channels are audible (independent of `muted`); multiple
    channels can be soloed at once

- `Pattern` (Resource)
  - `name: String` (optional; empty means display as "Pattern N" by index)
  - `rows: Array[Array[Cell]]` — indexed `[row][channel]`

- `Cell` (Resource)
  - `note: int` (MIDI-style note number, or -1 for empty/no-op)
  - `instrument_id: int`
  - `volume: int` (0-15, NES-style 4-bit scale)
  - `effect: String` (optional, e.g. "arpeggio", "slide" — can be a stub in v1)
  - `effect_param: int`

- `Instrument` (Resource)
  - `id: int`
  - `name: String` (optional; empty means display as "Instrument N" by id)
  - `waveform: String` ("square", "triangle", "noise", "sine")
  - `duty_cycle: float` (for square waves)
  - `envelope: Array[float]` (volume breakpoints in [0,1], linearly
    interpolated across a note's duration by `Synth._envelope_value`; any
    length works, not just 4 -- the InstrumentPanel editor defaults new/
    never-touched instruments to 4 points but lets the count be changed)
  - `envelope_times: Array[float]` (where each envelope point sits along the
    note, as a fraction 0-1 of its length: one entry per envelope point, never
    decreasing. Empty, or a length that doesn't match `envelope`, means the
    points are evenly spaced from 0 to 1, which is how envelopes always worked,
    so older songs and instruments made without it are unchanged. Before the
    first point the level holds the first value and after the last it holds
    the last; two points at the same time make a step. Edited by dragging a
    point sideways in the EnvelopeGraph, or with the time box beside the
    point's value box: a point can't go earlier than the one before it or
    later than the one after it (the first is bounded by 0, the last by 1),
    and holding Shift while dragging snaps both the level and the time to
    the nearest 0.1. `Instrument.set_point_time()` enforces the bounds, and
    `add_envelope_point()` / `remove_envelope_point()` keep the two arrays
    together: a new point goes at time 1, and if the old last point was
    already at 1 it moves to halfway so points are never stacked.)
  - `arpeggio: Array[int]` (optional pitch offsets per tick)

Keep field names and enums identical between this doc, the GDScript classes,
and the MCP command schema below — this is the seam most likely to drift.

## MCP command protocol (Godot bridge side)

JSON messages over the WebSocket, request/response, one response per request.
Suggested shape:

```json
// request
{ "id": "uuid", "command": "set_note", "params": { "pattern": 0, "row": 4, "channel": 1, "note": 60, "instrument_id": 0 } }

// response
{ "id": "uuid", "ok": true, "result": {} }
{ "id": "uuid", "ok": false, "error": "row out of range" }
```

Initial command set to implement, in order:

1. `create_song` (params: tempo, channels, rows_per_pattern)
2. `get_song` (returns full current song state as JSON)
3. `add_instrument` (params: waveform, duty_cycle, envelope, and optionally
   envelope_times -- one 0-1 time per envelope point, never decreasing; the
   command is refused if the list is the wrong length or out of order, and
   `get_song` reports every instrument's `envelope_times`, evenly spaced when
   none were set)
4. `set_note` (params: pattern, row, channel, note, instrument_id, volume)
5. `set_tempo`
6. `play_preview` (starts real-time playback from a given row/pattern)
7. `stop_preview`
8. `render_wav` (params: output path) → renders full song to WAV, returns path.
   The Metronome channel is never included in the file (see "Metronome").

Added after M6, for editing a song's channel layout (params match the data
model's `Channel` fields):

9. `add_channel` (params: name, instrument_type) → appends a channel and
   grows every existing pattern's rows to match; returns `{channel_index}`
10. `remove_channel` (params: index) → shrinks every pattern's rows to
    match; rejects removing the last remaining channel
11. `set_rows_per_pattern` (params: rows_per_pattern) → resizes every
    pattern to that many rows, preserving existing cells (grow appends
    empty rows, shrink truncates)
12. `add_pattern` (no params) → appends a new empty pattern sized to the
    song's current rows_per_pattern/channel count; returns
    `{pattern_index}`. Not automatically added to the order list.
13. `remove_pattern` (params: index) → removes the pattern; order-list
    entries referencing it are dropped, entries referencing later
    patterns shift down by one
14. `rename_pattern` (params: index, name)
15. `load_song` (params: path) → replaces the current song wholesale by
    loading a `Song` resource from a `res://` path (built beforehand, e.g.
    by a headless GDScript run). For bulk imports (converting an external
    tracker/notation format) where hundreds or thousands of individual
    `set_note` calls would be impractical.

Added to give external tool calls parity with everything the dock/panel can
already do by hand -- an instrument (or channel, group, pattern-order entry)
made this way is otherwise a dead end for a caller that only has `add_*`:

16. `update_instrument` (params: id, and any of waveform/duty_cycle/
    envelope/envelope_times/pitch_envelope/pitch_envelope_times/pitch_range)
    → changes an existing instrument's own fields in place (never its
    layers), only the fields given. Referenced by `id`, not array position,
    since removing an earlier instrument shifts every later one's position
    but never its id. Refused for the reserved Metronome instrument.
17. `rename_instrument` (params: id, name) → refused for the Metronome
    instrument or the reserved name.
18. `remove_instrument` (params: id) → refused for the Metronome instrument
    (`remove_metronome` instead). A cell still referencing a removed
    instrument's id is left as-is and simply plays nothing, same as
    removing one from the dock.
19. `duplicate_instrument` (params: id) → deep-copies the instrument,
    including its layers (each an independent copy, not a shared
    reference -- `Instrument.duplicate(true)` recurses through
    `Array[Instrument]`), as a new instrument with a fresh id and, if the
    original was named, `" (Copy)"` appended. Returns `{instrument_id}`.
20. `add_layer` (params: instrument_id, and the same fields `add_instrument`
    itself takes, plus step_offset/fixed_note_enabled/fixed_note -- see
    "Layered instruments") → appends a layer, built by the same
    `_build_instrument()` used for a top-level instrument (called with
    `allow_layers: false`, so a "layers" key inside it is ignored -- a
    layer can never have layers of its own). Returns `{layer_index}`.
    Refused for the reserved Metronome instrument.
21. `update_layer` (params: instrument_id, layer_index, and any of the
    fields `add_layer` takes) → changes one existing layer in place, only
    the fields given.
22. `remove_layer` (params: instrument_id, layer_index)
23. `rename_channel` (params: index, name) → refused for the Metronome
    channel or the reserved name.
24. `update_channel` (params: index, and any of muted/solo/instrument_type)
    → the Metronome channel's mute/solo can still be set, but not its
    instrument_type.
25. `move_channel_to_group` (params: channel_index, group_index) → an
    absolute target group, unlike the dock's one-step `<-`/`->` buttons.
    Refused for the reserved Metronome channel/group.
26. `add_group` (no params) → returns `{group_index}`.
27. `remove_group` (params: index) → folds its channels into the group to
    its left. Refused for Group 1 (index 0), the last remaining group, or
    the Metronome group.
28. `rename_group` (params: index, name) → refused for the Metronome group
    or the reserved name.
29. `add_metronome` (no params) → `Song.add_metronome()`; returns
    `{channel_index, instrument_id}`. Refused if the song already has one.
30. `remove_metronome` (no params) → `Song.remove_metronome()`. Refused if
    there's no metronome, or it's the song's only channel.
31. `add_to_order` (params: pattern) → appends a pattern index to the order
    list; returns `{order_index}`.
32. `remove_from_order` (params: order_index) → by position in the order
    list, not the pattern's own index.
33. `move_order_entry` (params: order_index, direction: -1 or 1) → swaps
    with the neighbouring entry, the same move the dock's up/down buttons
    do, not a reorder to an arbitrary position. Refused at either end of
    the list.

`get_song` reports `groups` (name per group) and each channel's
`group_index` alongside everything it already reported, so 23-28's effects
are visible without a separate call.

Each command should validate params and return a clear `error` string on
failure rather than crashing the bridge connection.

Commands 16-22 (instrument-level) exist as both `chiptracker_*` and
`chiptracker_editor_*` MCP tools, matching `add_instrument`/
`random_instrument`. Commands 23-33 (channel/group/metronome/order) are
editor-only, matching the existing `add_channel`/`remove_channel` -- the
private in-memory Play-time song has never needed live channel-layout
editing the way the open editor tab does. Covered by
`tests/bridge_extended_test.gd`.

### Two bridge instances, same protocol

M6 (the editor UI) introduced a second bridge alongside the original one,
both implementing the exact command set above via the same
`ChiptrackerCommandDispatcher`, just pointed at different `Song` instances:

- **`ChiptrackerBridge`** (autoload, `res://addons/chiptracker/bridge/mcp_bridge.gd`,
  port **7777**) — live only while the project is actually *running* (Play
  or headless), operating on a private in-memory song. This is the one a
  batch/headless `render_wav` workflow, or an exported build, would use.
- **`ChiptrackerEditorBridge`** (`res://addons/chiptracker/bridge/editor_bridge.gd`,
  port **7778**) — hosted directly by the `EditorPlugin` (added in
  `_enter_tree()`, not a project autoload), so it's live whenever the editor
  is open, *not just during Play*. It points straight at whatever `Song`
  the Chiptracker main-screen tab currently has loaded, and every mutating
  command refreshes that tab's UI immediately afterward — so an external
  tool call (e.g. adding a drum channel) shows up live in the open tab.

The Python MCP server (`mcp_server/server.py`) exposes both as separate
tool namespaces: `chiptracker_*` talks to port 7777, `chiptracker_editor_*`
talks to port 7778. They operate on genuinely different `Song` objects —
using one does not affect the other.

## Coexisting with generic editor MCP servers

This project's Godot environment also has generic editor-control MCP servers connected (Ivan Murzak's Godot MCP on the .NET side, gdai-mcp-plugin-godot on GDScript-only projects). These expose broad "run code in the editor" / "edit scene tree" style tools and are NOT aware of the Song/Pattern/Instrument resources or tracker semantics — they are a separate control path over the same mutable state as the bridge in this spec.

Rules to avoid the two control paths colliding:

- **Namespace every bridge tool** with a `chiptracker_` prefix (e.g. `chiptracker_set_note`, `chiptracker_render_wav`), so tool names never collide with the generic editor MCP's tools and are unambiguous to select.
- **Prefer the `chiptracker_*` tools over generic code-execution tools** whenever both are available and the task is something the bridge already covers (creating songs, setting notes, rendering audio, etc). Only reach for the generic editor MCP for things outside the bridge's scope — editor scaffolding, scene/node setup, non-tracker code changes.
- **Don't mutate Song/Pattern/Instrument resources via raw run-code calls** through the generic editor MCP. All tracker-state writes should go through the bridge so validation runs and the bridge's in-memory state doesn't drift from what a raw script edit did.
- **Pick a distinct local port** for the bridge's WebSocket/TCP listener. This
  project's own Godot-MCP instance (the C#-side one, see "C#/.NET port" below)
  runs on port **29649**, deliberately *not* the 29648 seen in other local
  Godot projects using the same addon (e.g.
  `~/Godot/Godot Projects/Godot Metroidvania Engine/new-game-project/`) —
  that port is already spoken for by that project's own standalone server, and
  the two could run at the same time on this machine. gdai-mcp-plugin-godot
  (`godot-ai`, the GDScript side) is believed to be somewhere in the 8000s
  (unconfirmed — verify in its plugin dock if a conflict ever comes up).
  **Chiptracker bridge ports: 7777** (Play-time/headless `ChiptrackerBridge`)
  **and 7778** (editor-resident `ChiptrackerEditorBridge`, added after M6) —
  clear of all of the above.
- **Use a distinct autoload/singleton name** for the bridge (e.g. `ChiptrackerBridge`) so it can't collide with an autoload registered by the generic editor MCP plugin.
- Note the headless/editor-open constraint: the bridge in this spec is designed to work with Godot running headless (for `render_wav` in batch contexts). Murzak's/gdai's servers require the editor to be open — don't assume their tools are available in a headless run.

## Portability / exportability

This must work as a drop-in addon: copy `addons/chiptracker/` into another Godot project's `addons/` folder, enable it in Project Settings → Plugins, done. No separate packaging step, no manual per-project setup. To keep that true throughout the build:

- **Everything lives under `res://addons/chiptracker/`** — engine layer, bridge, UI, all of it. Nothing referenced from outside that folder.
- **No hardcoded absolute paths.** Every internal reference is relative to `res://addons/chiptracker/...`.
- **Register the bridge autoload in code**, not via manual Project Settings setup — call `add_autoload_singleton` in the plugin's `_enter_tree()` and `remove_autoload_singleton` in `_exit_tree()`, so enabling the plugin is the only step a new project needs.
- **`plugin.cfg` must be complete and correct** (name, description, author, version, script entry point) so it behaves properly in the Plugins list and is publishable to the Godot AssetLib later if desired.
- Test portability directly before considering the plugin done: copy the `addons/chiptracker/` folder into a fresh/throwaway Godot project, enable it, and confirm the main-screen tab, bridge, and playback all work with zero manual setup beyond enabling the plugin.

## C#/.NET port

The original intent for this project was actually Godot's .NET/C# build,
not GDScript — that just wasn't specified at the very start, so the plugin
ended up built in GDScript for simplicity and that's stayed the working
implementation through M1-M7+. The plan is to eventually maintain **both**:
this GDScript addon (simplest install, no .NET requirement, matches the
"drop-in addon" portability goal above) and a separate C#/.NET port,
expected to end up the stronger option specifically for playback
performance. The GDScript version keeps developing as the reference
implementation; the C# port's environment setup has started (below), but no
tracker code has been ported yet.

**Naming**: since both implementations now live in the same Godot project
and appear side-by-side in Project Settings → Plugins, the GDScript addon's
plugin display name was changed from "Chiptracker" to "Chiptracker GD"
(`addons/chiptracker/plugin.cfg`'s `name` field and `plugin.gd`'s
`_get_plugin_name()`/dock-panel title). The future C# addon should be named
"Chiptracker .Net" from the start, once its own `plugin.cfg`/`EditorPlugin`
exists, so the two are never ambiguous in the plugin list or the main-screen
tab bar.

- C#'s JIT-compiled execution is roughly 10-50x faster than GDScript's
  bytecode interpreter for the kind of tight per-sample numeric loop
  `PlaybackState._mix_sample()`/`Synth.generate_buffer()` do — likely
  enough on its own to push the channel-count-before-stutter ceiling well
  past anything a chiptune tracker realistically needs, possibly without
  also needing the streaming-synthesis rework mentioned under "Playback
  performance" above.
- It does **not** by itself solve the main-thread/UI-contention issue — C#
  code invoked from `_process()` still shares Godot's main thread unless
  it's also moved to a separate `Thread`. The dedicated-thread idea and the
  C# port are complementary, not substitutes for each other.
- Cost: requires the consuming project to have Godot's C#/.NET support
  enabled — a real setup step compared to this addon's current "enable the
  plugin, done" story. Same category of portability trade-off as the
  GDExtension/SIMD option mentioned earlier, though lighter in practice (no
  per-platform native binaries to build/ship, just a project that needs
  .NET support turned on).

### Environment setup (done)

Same Godot project as the GDScript addon (`chiptune-plugin/`), not a
separate one -- so the C# port and the GDScript reference implementation
coexist and can be compared directly.

- **Godot**: the project is opened with `Godot_mono.app` (the .NET-enabled
  editor build) for C# work, alongside the regular `Godot.app` build already
  used for the GDScript addon and its headless tests. Both are Godot 4.7.2;
  only the mono build can load a plugin with a `.cs` entry point.
  `project.godot`'s `config/features` includes `"C#"` and it has a
  `[dotnet] project/assembly_name="Chiptune Plugin"` section.
- **`Chiptune Plugin.csproj`** at the project root (`Godot.NET.Sdk/4.7.2`,
  `net8.0`), with `com.IvanMurzak.ReflectorNet`/`com.IvanMurzak.McpPlugin`
  package references -- no `.sln` needed, Godot's mono build doesn't require
  one. `dotnet restore`/`dotnet build` both succeed from the command line
  (independent of the editor), so a plain compile-check doesn't need the
  editor open at all.
- **Godot-MCP** (`IvanMurzak/Godot-MCP` on GitHub, Apache-2.0): a generic
  editor-control MCP server for the .NET side, the C# counterpart to
  `gdai-mcp-plugin-godot` (`godot-ai`) already used for the GDScript side --
  see "Coexisting with generic editor MCP servers" above, including the
  confirmed port. Installed at `res://addons/godot_mcp/` (enabled in
  `project.godot`'s `editor_plugins`) by copying the exact addon folder from
  an already-working local project
  (`~/Godot/Godot Projects/Godot Metroidvania Engine/new-game-project/`)
  rather than a fresh download, since that copy is proven to actually work
  end to end (build + connect) rather than just matching the README.
- **Connection mode**: local/self-hosted, not Murzak's hosted cloud
  (`ai-game.dev`) -- no OAuth account, everything on localhost, matching how
  every other MCP piece in this project already works. It's an
  HTTP-transport MCP server at `http://localhost:29649/mcp/p/<per-project-id>`
  -- the `<per-project-id>` segment is assigned by the local server the first
  time it registers this project, isn't derivable in advance, and (per the
  reference project) isn't persisted to any file the addon writes either --
  it's only ever visible in the running addon's own dock UI. So this
  project's own URL is only known once its server has actually run once (see
  below) and someone reads it off there.
- **Run the server standalone, not via the addon's own "Start Server"
  button.** The addon's own local-server lifecycle is tied to the editor
  process, and **a C# rebuild makes the addon kill any server it spawned
  itself** -- so code changes would silently drop the MCP connection every
  time. The addon downloads the actual server binary the first time it runs
  in the editor (`GameDev-MCP-Server`, the engine-agnostic server shared by
  Unity-MCP/Godot-MCP/Unreal-MCP, cached at
  `chiptune-plugin/.godot/mcp-server/<rid>/gamedev-mcp-server`); running that
  same binary independently, so it survives editor rebuilds and the addon
  just reconnects to it (shows "External" in its dock instead of spawning),
  is the pattern the reference project uses and this project now copies:
  - `.claude/godot-mcp-session.sh` -- `start`/`stop`. `start` health-checks
    `POST http://localhost:29649/api/system-tools/ping`; if nothing answers,
    launches the cached binary detached
    (`port=29649 plugin-timeout=10000 client-transport=streamableHttp
    auth=none`), recording its PID in a marker file so `stop` only kills a
    server *this* session started (never one the user or another session is
    using). If the binary isn't there yet, it says so rather than failing
    silently -- the editor needs to have run at least once first.
  - `.claude/settings.local.json` -- `SessionStart` runs `start`,
    `SessionEnd` runs `stop`, so the server's lifetime tracks the Claude
    session automatically. Both files are git-ignored (global gitignore
    already covers `**/.claude/settings.local.json`).
- **Still manual** (needs the actual editor GUI, which nothing here can
  drive): open `chiptune-plugin/` in `Godot_mono.app` for the first time so
  it builds the C# project, the plugin initializes, and the server binary
  gets downloaded; from the next Claude session onward the hook above starts
  it automatically. Read the resulting `http://localhost:29649/mcp/p/<id>`
  URL off the Godot-MCP dock and add it to a `.mcp.json` at the repo root
  (`{"mcpServers": {"godot-mcp": {"type": "http", "url":
  "http://localhost:29649/mcp/p/<id>"}}}`, matching the reference project's
  shape) -- not written yet, since the id isn't known until then. Once that's
  done, Claude gets a second, generic (not tracker-aware) way to drive this
  project's editor -- same division of labor described in "Coexisting with
  generic editor MCP servers": prefer the `chiptracker_*` bridge for
  anything it already covers, and Godot-MCP only for C#/editor-scaffolding
  work outside that.
- **C# editor-plugin gotchas**, learned from the reference project and worth
  knowing before driving any C# node through Godot-MCP (or any similar
  reflection-based tool):
  - A property-set tool **can't write a C# `[Export]` field** at all
    (reflection only reaches base-type properties) -- edit the `.tscn` on
    disk directly instead, then Scene → Reload Saved Scene in the editor.
  - Built-in Godot properties need **PascalCase** through these tools
    (`Position`, `Monitorable`), not GDScript's lowercase.
  - Assigning a `Resource`-typed property inline fails ("Instance creation
    failed") -- create it as a `.tres` first and reference it by path.
  - **A C# rebuild wipes any live C# editor-plugin's own UI state** (docks,
    etc.), and editing a live C# `EditorPlugin`'s own script needs the
    plugin disabled, then rebuilt, then re-enabled -- it doesn't hot-reload
    the way a GDScript `@tool` script does.
- No tracker code (Song/Instrument/Synth/etc.) has been ported to C# yet --
  this section is environment setup only.

## Milestones (build in this order)

**M1 — Data model + song construction, no audio**
Implement `Song`/`Channel`/`Pattern`/`Cell`/`Instrument` as Resources. Write a
GDScript unit test (or a scratch scene with print statements) that builds a
small song by hand and saves/loads it as `.tres`. No UI, no audio yet.

**M2 — Synthesis engine**
Implement a synth that takes an `Instrument` + note + duration and generates
a PCM buffer (square/triangle/noise waves, basic envelope). Play it through
`AudioStreamGenerator` from a test scene — one channel, one note, confirm it
sounds right before going further.

**M3 — Playback engine**
Given a `Song`, step through the order list/patterns row by row at the
correct tempo, mixing all channels, feeding `AudioStreamGenerator`. This is
the real-time sequencer. Test with a hand-built multi-channel song.

**M4 — WAV render**
Reuse the playback engine but drive it through `AudioEffectRecord` (or
manually mix to a buffer and write a WAV file) instead of live output, with
no real-time constraint — render the full song file as fast as possible to a
`.wav` on disk.

**M5 — MCP bridge**
Implement the autoload WebSocket server and the command set above, calling
into the M1-M4 engine layer. Build/test the corresponding minimal Python (or
TS) MCP server separately, using the MCP SDK, that connects to this socket
and exposes the commands as MCP tools. Test end-to-end: an LLM composing a
short melody via tool calls, then rendering it to WAV.

**M6 — Pattern grid UI (human-facing)**
Only after M5 works: build the visual tracker UI (custom `Control` for the
grid, instrument editor panel, transport controls). This should call the same
engine API the bridge uses — if it needs new engine capabilities, add them to
the engine layer, not directly in UI code.

*Editor integration approach*: implement this as an `EditorPlugin` with
`_has_main_screen()` returning `true`, so "Chiptracker" appears as its own
top-bar tab next to 2D/3D/Script/Game/AssetStore — not as a bottom dock.
`_make_visible()`/`_edit()` should swap in a root `Control` containing: a
transport bar (play/stop/loop, tempo, current row/pattern), the pattern grid
itself (a custom `Control` with manual `_draw()` — Godot has no built-in
spreadsheet-style grid control, so this is the most involved UI piece in the
project), and a side panel for the selected instrument's waveform/envelope/
duty-cycle controls. The instrument list/order-list can live in a separate
bottom dock (registered via `add_control_to_dock`) alongside FileSystem etc.,
independent of which main-screen tab is active.

**M7 — Scene persistence via `ChiptrackerSongNode`**
Added a `Node` (`ChiptrackerSongNode`) holding a `song: Song` export, plus
`plugin.gd` `_handles()`/`_edit()` wiring so selecting one in the Scene
dock binds the Chiptracker tab to it. See "Scene persistence" below.

**M8 — Pre-rendered pattern/song audio cache**
Replace live per-sample synthesis during Play/cursor-start playback
with a pre-rendered sample buffer per pattern, built via the same
non-real-time render loop `WavRenderer` already uses. Bar preview
(Space in Edit mode) renders its bounded 16-row slice synchronously and
needs no cache. See "Pattern/song audio cache" below for the full
design, including the dirty-tracking/rebuild triggers and the Play
button's orange-triangle/progress-indicator UI feedback.

**M9 — Live recording**
Enter notes while playback scrolls through a looping pattern, written at
the row being played. Built in steps (see "Live recording (M9)" below);
all seven are done.

## Scene persistence (ChiptrackerSongNode)

`Song` (and its nested `Channel`/`Pattern`/`Instrument`/`Cell`) were already
`Resource` subclasses with `@export` fields from M1 onward, specifically so
they'd get free serialization -- `ChiptrackerSongNode` is what actually
plugs that into a savable scene:

- `ChiptrackerSongNode` (`res://addons/chiptracker/engine/chiptracker_song_node.gd`,
  `@tool`, extends `Node`) — a plain node with `song: Song` and
  `show_smooth_playhead: bool` (default `true`) fields. Add it anywhere in
  any scene; saving that scene saves the whole song (channels, instruments,
  patterns, order list) as an inline sub-resource, since Godot serializes
  exported Resource properties as part of the scene file.
  `show_smooth_playhead` toggles a continuously-interpolating playhead
  line PatternGridView draws on top of the discrete per-row highlight
  during playback -- read live every frame, so flipping it in the
  Inspector takes effect immediately, including mid-playback.
- Selecting a `ChiptrackerSongNode` in the Scene dock binds the Chiptracker
  main-screen tab to it: `plugin.gd` implements `_handles()`/`_edit()`
  (the same mechanism that makes selecting a `Node3D` switch to the 3D
  tab) and calls `ChiptrackerMainView.bind_song_node(node)`, which points
  the tab's `song` at `node.song` directly (same Resource reference) —
  edits made through the tracker UI, or through the editor MCP bridge
  (port 7778), write straight into that node's data. Deselecting the node
  leaves the tab showing whatever it last had; it does not reset.
- Because in-place mutation of an exported Resource's fields doesn't
  trigger Godot's own "unsaved changes" scene-dirty tracking (only edits
  made directly through the Inspector do), `ChiptrackerMainView._mark_dirty()`
  calls `EditorInterface.mark_scene_as_unsaved()` after every tracker edit
  while a node is bound, so Ctrl+S actually offers to save.
- `create_song` (via either bridge) replaces the `Song` object outright
  rather than mutating it in place; when a node is bound, the editor
  bridge also re-points `bound_song_node.song` at the new object so the
  node's export doesn't go stale.
- Without a bound node, the tab behaves exactly as before M7: a scratch
  `Song` that starts as the built-in demo song and does not persist
  across an editor restart. Binding a node is opt-in, not required.

## Metronome

A practice click, created and removed only by the **Add Metronome** /
**Remove Metronome** buttons on the dock's Groups tab (bottom row, beside
an **Accent** toggle). All logic is in `Song` (engine layer); the dock only
calls it.

- `Song.add_metronome()` creates, together: an instrument named `Metronome`
  (noise, a 16-point envelope that hits silence within the first fifth of the
  note), a group named `Metronome` (appended after the existing groups) and a
  channel named `Metronome` (noise, in that group), then fills in the ticks.
  There is only ever one; a second call returns false and changes nothing.
  The Add button is disabled while one exists.
- `Song.remove_metronome()` removes all three together. It refuses if the
  metronome is the song's only channel.
- **Reserved name.** The three are found by the exact name `Metronome`
  (`Song.METRONOME_NAME`), so the name is reserved case-insensitively
  (`Song.is_reserved_name()`): the metronome's own channel, group and
  instrument can't be renamed, and nothing else can take the name (dock
  renames, and the bridge's `add_channel`, are rejected).
- **Protected.** `Song.remove_channel()` and `remove_group()` refuse the
  Metronome channel/group, the dock's instrument removal refuses the
  Metronome instrument, and the bridge's `remove_channel` returns an error
  for it. The Metronome channel can't be moved out of the Metronome group and
  no other channel can be moved into it. With nothing selected, the dock's
  Remove buttons skip the metronome and take the last other item.
- **Ticks** are real cells, laid by `Song.sync_metronome()` on every row that
  is a multiple of `rows_per_beat`, in every pattern (other rows of that
  channel are cleared). It re-runs on `add_metronome()`, `add_pattern()`,
  `set_rows_per_pattern()`, `set_rows_per_beat()` and the Accent toggle, so
  it stays in step with new patterns, longer patterns and a changed beat.
  Hand edits to the Metronome channel don't survive a re-sync.
- **Accent.** With `metronome_accent` on, the first beat of each bar (a bar
  is 4 beats, `METRONOME_BEATS_PER_BAR`) is volume 15 and the others 9; off,
  every tick is 12. Noise ignores pitch, so accent is volume only.
- **Export.** `PlaybackState.exclude_metronome` leaves the channel out of
  the mix (and out of the per-channel level division). `WavRenderer` sets it,
  so WAV export and the bridge's `render_wav` never contain the click. Play
  and the pattern audio cache leave it off, so it's audible while working;
  it counts as one more audible channel there, which lowers the overall mix
  level slightly.
- The click's length scales with the beat (its envelope spans the note, which
  lasts until the next tick), so it's shorter at faster tempos.

## Envelope archetypes

The instrument panel can build an envelope in the style of one of the twenty
archetypes in `chiptune-plugin/CommonEnvelopes.md`. A row above the envelope
graph has a drop-down of the archetypes and a **RANDOMIZE** button, with no
node-count box of its own: RANDOMIZE replaces the instrument's envelope with a
new one in the chosen style, keeping whatever number of points the envelope
currently has (add or remove points first, with the header row's +/- buttons,
to change how many it makes). Each click gives a different result.

- **Only the volume envelope.** `EnvelopeArchetypes` (engine layer) writes
  `Instrument.envelope` and `envelope_times`. It never changes the waveform,
  duty cycle, or `Instrument.pitch_envelope` -- the pitch envelope has its own,
  entirely separate RANDOMIZE PITCH (see "Pitch envelope" below); pressing
  RANDOMIZE here never touches it, and pressing RANDOMIZE PITCH never touches
  this one. A handful of archetypes' descriptions do include a pitch
  chirp/slide, and `EnvelopeArchetypes.generate_pitch()` can build one from
  that data (`has_pitch()`/`generate_pitch()`, used by the MCP bridge's
  `random_instrument` and by `PitchArchetypes` sharing its helpers) -- but the
  instrument panel's two buttons don't cross-wire the two curves any more (see
  "RANDOMIZE PITCH" for why that changed). The rest of each archetype's
  description (vibrato, pulse-width swaps, arpeggio loops) is periodic
  modulation or note-sequencing, not a one-shot curve, and still isn't
  generated.
- **Timing.** The archetypes are in milliseconds; envelope times are fractions
  of the note's length. A note is taken to be 500 ms (`REFERENCE_MSEC`), so a
  150 ms decay is 0.3. A longer shape (the crash's 1.5 s tail, the music box's
  800 ms, the pad's 900 ms) is squeezed evenly to fit the note; a shorter one
  holds its last level for the rest of it. The synth has no key-release, so an
  archetype's release is written into the shape as its tail. The real length
  of a sound then depends on how long the note lasts.
- **Shape.** Each archetype is a list of `[ms, level]` keyframes (two at the
  same time make a step, as the gated kick's cut and the echo lead's ghost
  notes do; the music box decays exponentially). With more points than
  keyframes the extras go on the curve, always splitting the widest gap; with
  fewer, the keyframes closest to a straight line between their neighbours are
  dropped first, so the ends and corners stay. One point is a steady level at
  the shape's peak.
- **Variance.** Moderate, drawn from a `RandomNumberGenerator`: the whole
  shape stretches or squeezes in time by up to 15%, each point wanders a
  little within the gap to its neighbours (half that again, so the two
  together stay near 20%), and each level changes by up to 15% of itself (so
  silence stays silent and nothing passes 1). The heavy drive bass, which
  must stay flat, varies its levels by only 3%. Order can't break and steps
  stay steps. The same seed reproduces the same envelope.
- The synth's lookup for custom envelope times is a binary search, so a
  255-point envelope renders as fast as a short one.
- Covered by `tests/envelope_archetypes_test.gd` and (the pitch curve)
  `tests/pitch_envelope_test.gd`.

## Pitch envelope

Alongside the volume envelope, an instrument has an independent pitch
envelope: `Instrument.pitch_envelope` (semitones, signed, 0 = the note's
written pitch) and `pitch_envelope_times`, with the same "custom times or
evenly spaced" rule as the volume envelope's `envelope_times`
(`has_custom_pitch_times`/`pitch_point_time`/`resolved_pitch_times`/
`set_pitch_point_time`/`add_pitch_point`/`remove_pitch_point` on `Instrument`,
mirroring the volume ones). Empty means no bend at all, the state of every
instrument before this existed; `Synth.generate_buffer` skips it entirely in
that case rather than costing a `pow()` per sample for nothing, and otherwise
scales the waveform's phase increment each sample by `pow(2, semitones / 12)`.

- **Independent of the volume envelope.** Its own point count, its own
  timing, and its own RANDOMIZE button -- the two curves are never forced to
  move together, and neither RANDOMIZE button touches the other curve. (An
  earlier version had RANDOMIZE generate both together from a shared node
  count; that surprised anyone who'd grown one curve by hand and then pressed
  the other button expecting it to leave their work alone, so the two were
  fully separated -- see "RANDOMIZE PITCH" below.)
- **The panel** shows it as a second block, between the volume envelope's
  graph and the preview controls: a header (`Pitch Envelope (N points)`, Add,
  Remove), its own archetype drop-down/Nodes/Range/RANDOMIZE PITCH row (see
  below), a second `EnvelopeGraph` (see below), and a point selector (`Point
  N` value box, in semitones, and an `at` time box) -- the same shape as the
  volume envelope's own controls, built in code the same way.
- **`Range +/-`** (in the pitch section's own row, beside `Nodes`) is
  `Instrument.pitch_range`: how far the pitch graph's vertical axis reaches
  above and below 0, and the limit both a hand-dragged point and RANDOMIZE
  PITCH's curve are held to. Shrinking it clamps any existing pitch points
  down to fit immediately. Defaults to 24 (two octaves).
- **`EnvelopeGraph`** (`ui/envelope_graph.gd`) is shared with the volume
  envelope, generalized with a `value_min`/`value_max` axis (default 0-1, a
  level) instead of assuming 0-1: the pitch graph sets these to
  `-pitch_range`/`+pitch_range`, which is all dragging, drawing and clamping
  needs to work in semitones instead. A range spanning zero draws a solid
  center line. Shift-drag snaps the level to `value_snap` (0.1 for the volume
  graph, a whole semitone for the pitch graph) rather than always 0.1 of the
  axis, since a pitch point landing exactly back on 0 (no bend) matters more
  than snapping to a tenth of whatever range happens to be set.
- **`EnvelopeArchetypes.has_pitch(index)`** says whether an archetype has a
  pitch curve (`"pitch_keys"`, the same kind of `[ms, semitones]` keyframe
  list as `"keys"`, optionally with its own `"pitch_curve": "exp"`);
  `generate_pitch(index, node_count, range_semitones, rng)` builds one the
  same way `generate()` builds the volume curve (reusing the same
  keyframe-squeeze/node-pick machinery), clamped to `+/-range_semitones` and
  varied by a fixed `PITCH_LEVEL_VARIANCE` semitones (absolute, not
  proportional -- a point sitting at exactly 0 needs to be able to wander
  too). Seven archetypes have one: both kicks with a chirp/sweep, the gated
  kick, the 8-bit tom, the slap bass, the rubber bass, and the coin arpeggio.
  The rest (both remaining snares, both hi-hats, the cymbal, both remaining
  leads, both remaining basses, all four plucks/pads/pluck-adjacent
  archetypes) have none. This is used by the MCP bridge's `random_instrument`
  (below) and by `PitchArchetypes`' one-shot shapes, not directly by the
  instrument panel's volume archetype row any more.
- **The MCP bridge.** `add_instrument` takes optional `pitch_envelope`,
  `pitch_envelope_times` and `pitch_range`. `random_instrument` takes
  `pitch_range` (default 24) and `pitch_node_count` (defaults to
  `node_count`), and generates a matching pitch envelope in one call for an
  archetype that has one -- unlike the instrument panel's two buttons, a
  single MCP call is naturally "build me one full instrument," so there's no
  equivalent clobbering risk to design around. `list_archetypes` reports
  `has_pitch` per archetype. `get_song` reports `pitch_envelope`,
  `pitch_envelope_times` (resolved) and `pitch_range` per instrument.

### RANDOMIZE PITCH: generic pitch-curve archetypes

The pitch envelope has its own RANDOMIZE PITCH button, archetype drop-down,
`Nodes` box (1 to 60) and `Range +/-` box (`PitchArchetypes`,
`engine/pitch_archetypes.gd`) -- all in the pitch section's own row, entirely
separate from the volume archetype row above. Pressing it only ever replaces
`pitch_envelope`/`pitch_envelope_times`; the volume envelope's own RANDOMIZE
only ever replaces `envelope`/`envelope_times`. Unlike the volume row, the
pitch section keeps its own `Nodes` box, since typing an exact target count
before generating is more useful there; Add/Remove keep that box's displayed
value matching the pitch envelope's actual point count the rest of the time
(`_sync_pitch_node_count_spin`), so it never goes stale.

These eleven archetypes aren't tied to an instrument's name the way the
twenty volume ones are -- a "chirp down" pitch shape suits a kick as much as
a lead -- so they're a separate, generic list:

- **One-shot shapes** (Chirp Up, Chirp Down, Portamento In, Pitch Bend
  Release, Scoop, Fall Off, Overshoot, Detune Drift): a `[ms, semitones]`
  keyframe list, generated the same way as `EnvelopeArchetypes.generate()` --
  in fact reusing its keyframe-squeeze and node-picking helpers
  (`_keyframes_from`/`_pick_nodes`/`_jittered_times`) rather than duplicating
  them, since the shape is built identically; only the level variance and
  final range-clamp differ (semitones, not 0-1).
- **Periodic shapes** (Vibrato, Delayed Vibrato, Trill): don't reduce to a
  handful of keyframes the way a one-shot bend does, so they're generated
  directly as a sine (or, for Trill, a hard alternating step) sampled at
  `node_count` evenly-spaced points instead. Depth is a fixed fraction
  (`PERIODIC_DEPTH_FRACTION`, 0.15) of the Range box rather than a separate
  control, so widening Range widens the wobble too. Delayed Vibrato holds
  flat at 0 for the first 30% of the note before the wobble starts, matching
  the volume archetypes' Theremin/Violin's delayed vibrato description. Each
  draw wanders the cycle count, phase and depth a little
  (`PERIODIC_VARIANCE`), so consecutive presses differ.
- A node count too low to show an oscillation clearly (a handful of points
  for a 3-cycle vibrato) is a real limit of a node-based curve, not a bug;
  raising `Nodes` fixes it, and 60 is normally plenty.
- Covered by `tests/pitch_envelope_test.gd`.

## Layered instruments

An instrument can carry additional instruments, its layers, that play
alongside it whenever it's triggered -- each with its own waveform, duty
cycle, volume envelope and pitch envelope, plus a note of its own relative to
whatever note triggered the base instrument.

- **Data model.** `Instrument.layers: Array[Instrument]` -- a layer is a
  plain `Instrument`, reusing every field the panel already edits (waveform,
  duty_cycle, envelope/envelope_times, pitch_envelope/pitch_envelope_times/
  pitch_range). Two fields only mean anything on a layer: `step_offset`
  (semitones above/below the triggering note, default 0) and
  `fixed_note_enabled`/`fixed_note` (when on, always play `fixed_note`
  regardless of the triggering note, ignoring `step_offset` entirely --
  `Instrument.resolved_layer_note(triggering_note)` is the one place that
  logic lives). A layer's own `layers` is expected to stay empty: layering is
  one level deep, and a layer's tab has no "+ LAYER" button of its own.
- **Private to the parent.** Layers have no `id`, don't appear in the
  Instruments list, and can't be assigned to a channel cell directly -- only
  a base instrument (one that isn't itself somebody else's layer) is ever
  selectable that way. `InstrumentPanel.get_edited_instrument()` always
  returns the base, regardless of which tab is open, for exactly this reason
  (cell entry needs an id to stamp onto the cell).
- **`Synth.generate_layered_buffer(instrument, note, duration, sample_rate)`**
  renders the instrument playing `note`, then adds in each layer rendered at
  `layer.resolved_layer_note(note)` with its own waveform/envelopes, summed
  sample-by-sample. An instrument with no layers renders identically to
  `generate_buffer`. The sum isn't normalized for headroom -- a loud stack of
  layers can clip, the same as several full-volume channels would.
- **Reaches real playback and export too.** `PlaybackState._trigger_row()` --
  the one place a triggered note's audio is rendered -- calls
  `generate_layered_buffer` instead of `generate_buffer`, and both
  `PatternAudioCache` and `WavRenderer` drive playback through `PlaybackState`
  rather than duplicating that logic, so this one change is enough to cover
  live Play, the pattern audio cache, and WAV export. (The metronome's click,
  in `count_in.gd`, still calls `generate_buffer` directly -- its instrument
  is a fixed, reserved one that can never have layers, so there's nothing to
  gain there.)
- **The panel** gets a tab bar right below the title: "Base" plus one tab per
  layer. Selecting a tab switches which instrument the shared controls
  (waveform, duty, both envelopes, both RANDOMIZE buttons, the ordinary
  Preview button) read and write (`_edited_target`, distinct from
  `_instrument`, which always stays the base). Only the base tab shows the
  **+ LAYER** button (bottom of the panel, adds a layer and switches straight
  to its tab) and a green **STACK** button beside the ordinary Preview button
  (tinted via `modulate` rather than a StyleBox override, so it doesn't fight
  the editor theme); only a layer's tab shows its `Step Offset`/`Fixed Note`
  row (right below the tab bar) and, separately, a **REMOVE LAYER** button --
  also at the bottom of the panel, in the same spot + LAYER occupies on the
  base tab, since the two are never visible at once. REMOVE LAYER originally
  lived at the end of the Step Offset/Fixed Note row, but a fourth control
  there was enough to widen the whole dock panel, so it moved down where
  width isn't a factor. Selecting a different instrument in the dock always
  lands back on that instrument's own Base tab.
- **Two previews, deliberately different.** The ordinary Preview button
  renders whichever tab is open, alone (`Synth.generate_buffer` on
  `_edited_target`) -- for tuning one oscillator by ear without the rest of
  the stack in the way. The green STACK button, and the public
  `preview_note()` API that keyboard/MIDI note entry in Edit mode already
  calls, render the base together with every layer
  (`Synth.generate_layered_buffer` on `_instrument`) -- what will actually be
  heard once this instrument is used in a song. `preview_note()` behaves
  exactly as before for an instrument with no layers, so keyboard/MIDI
  preview elsewhere in the tracker didn't need to change.
- **An earlier design had RANDOMIZE generate both the volume and pitch
  envelope together** (see "Envelope archetypes" above) -- the same
  reasoning applies here: each tab's RANDOMIZE/RANDOMIZE PITCH only ever
  touches that tab's own instrument, never the base's or another layer's.
- **The MCP bridge.** `add_instrument` takes an optional `layers` array; each
  entry is the same shape `add_instrument` itself takes (waveform, envelope,
  pitch_envelope, ...) plus `step_offset`/`fixed_note_enabled`/`fixed_note`,
  built by the dispatcher's `_build_instrument()` (the same function used for
  the top-level instrument, called once per layer with `allow_layers: false`
  so a layer's own `layers` is always ignored). `get_song` reports each
  instrument's `layers` the same way, minus `id`/`name` since a layer has
  neither.
- **Duplicating an instrument.** The dock's Instruments list has a
  `Duplicate` icon button on the right of each row
  (`ChiptrackerDock.duplicate_instrument_requested(index)`), styled like the
  compact per-row icon buttons elsewhere in the dock (Patterns' "add to
  order", channels' mute/solo). `ChiptrackerMainView` handles it with
  `Instrument.duplicate(true)` -- Godot's built-in deep-duplicate recurses
  through `Array[Instrument]` (`layers`) too, so a layered instrument's
  layers come along as independent copies, not shared references back to the
  original's. The copy gets a fresh id (`Song.next_instrument_id()`) and,
  if the original was named, `" (Copy)"` appended to it.
- Covered by `tests/layered_instrument_test.gd` and (duplication)
  `tests/m6_ui_load_test.gd`.

## Cursor step

A `Step` box right after TAP TEMPO in the transport bar (a whole number, 0 or
more, starting at 0; a blank box counts as 0; Enter hands the keyboard back to
the grid). In Edit mode, entering a note at the cursor, from the computer keys
or a MIDI key, then moves the cursor down that many rows: with a step of 3 and
the cursor on row 00, a key writes the note at 00 and the cursor goes to 03. The
cursor stops on the pattern's last row rather than running past it. It applies
only to notes entered at the cursor, so it does nothing while the song is playing
(including live recording, which writes at the playhead), and clearing a note with
Backspace doesn't move the cursor. The value is per session, not saved.

## Tap tempo

A `TAP TEMPO` button right after the octave readout in the transport bar, the
` key, and the Akai's TAP TEMPO button (CC 82, action `tap_tempo`), set the
song's tempo by tapping along.

- The ` key is a tab-wide shortcut (`ChiptrackerMainView._handle_shortcut_key()`),
  so it works with or without Edit mode. It is ignored while the tab isn't
  showing, while a text field has focus, for a held-key repeat, and with any
  modifier held (Shift+` is a ~).

- `TapTempo` (engine layer, pure: the caller supplies the clock) averages the
  most recent taps (up to 8) over the span from the first to the last, so
  jitter on the taps in between doesn't add up. It answers -1 for the first
  tap, and the BPM from the second on.
- A gap longer than 2 seconds starts a fresh count. Taps under 100 ms after
  the last are ignored as double triggers, which also caps the result near
  the tempo field's maximum. The result is clamped to the tempo field's range
  (20-400).
- `ChiptrackerMainView._on_tap_tempo()` applies the result to `Song.tempo` and
  the tempo field, as editing the field does (so the song is marked changed).
  Like any tempo change it doesn't alter audio that is already playing, since
  row lengths are baked into the cached audio; it applies from the next Play.
- Taps are timed when the tab handles the event, so MIDI taps carry about a
  frame of jitter (`InputEventMIDI` has no timestamp); averaging over several
  taps absorbs it.
- Covered by `tests/tap_tempo_test.gd`.

## MIDI keyboard input (ChiptrackerMidiNode)

Lets a physical MIDI keyboard preview and enter notes, and trigger tracker
actions from its buttons. The classes live in
`res://addons/chiptracker/engine/`; each device's profile resource, diagram
and input map live in `res://MIDI/<device>/` (e.g. `MIDI/Akai MPK Mini 4/`,
which holds `MidiKeyboardMap.md`, the diagram image and the profile `.tres`).

- `ChiptrackerMidiNode` (`@tool`, extends `Node`) — added as a child of a
  `ChiptrackerSongNode`. Exports `keyboard_profile: MidiKeyboard` and
  `enabled: bool`. Translates `InputEventMIDI` into signals `note_on(note,
  volume)`, `note_off(note)`, `control_changed(controller_number, value)` and
  `action_requested(action)` via `handle_midi_event()`. It only translates;
  it never writes cells or starts playback itself. Calls
  `OS.open_midi_inputs()` in `_ready()` and deliberately never closes them
  (see below).
- `MidiKeyboard` (`@tool`, extends `Resource`) — base class for a device
  profile: `device_name`, `midi_channel` (-1 = any), `octave_offset`,
  `default_instrument_id`, plus overridable `map_note()`, `map_velocity()`
  (0-127 to the 0-15 volume scale), `handle_control_change()`,
  `accepts_channel()`, `action_for_control_change()` (returns an action
  name), `get_mappings()` (function/input pairs for display),
  `get_diagram_path()` and `get_diagram_hotspots()`. It also defines the
  shared action names (`ACTION_*`), their display labels (`ACTION_LABELS`)
  and `SpotState` (FREE, ASSIGNED, UNAVAILABLE).
- `AkaiMPKMini4` (`@tool`, extends `MidiKeyboard`) — the first device
  profile. `BINDINGS` is the single table of CC-to-action mappings; it drives
  what a press does and what the MIDI Keyboard tab shows. `HOTSPOTS` holds
  each control's rectangle on the diagram. `KNOB_CC` holds the knob CCs
  (24-31, from a captured preset) and `_handle_knob()` is an empty hook. New
  devices add another subclass; nothing else changes.
- A node holds only one script, so the profile is a `Resource` assigned to
  the node's export, the same pattern `ChiptrackerSongNode.song` uses.

Actions (abstract, so profiles for different devices share them):

| Action | Effect in the tracker tab |
|---|---|
| `toggle_edit_mode` | Flips the EDIT button |
| `play` | Single press toggles Play/Stop; a second press within 350 ms stops and moves the cursor to row 0 of the first pattern in the order list |
| `undo_grid_edit` | Same as the UNDO button |
| `redo_grid_edit` | Same as the REDO button |
| `toggle_record` | Flips the REC button (see "Live recording (M9)") |
| `tap_tempo` | Same as the TAP TEMPO button (see "Tap tempo") |

Current Akai MPK Mini 4 bindings: CONTINUE (CC 76) = `play`, QUANTIZE
(CC 77) = `toggle_edit_mode`, the UNDO button (CC 73) = `undo_grid_edit`,
GLOBAL (CC 74) = `redo_grid_edit`, AUTOMATION (CC 78) = `toggle_record`,
TAP TEMPO (CC 82) = `tap_tempo`. Buttons fire on a value above 0, so once
per press.

How events reach the node:

- Hardware MIDI is NOT delivered to nodes inside the edited scene while the
  editor is idle, but IS delivered to nodes owned by the editor itself.
  `ChiptrackerMainView._input()` therefore forwards each `InputEventMIDI` to
  the bound song node's `ChiptrackerMidiNode` child, found per event (not
  cached) so adding the node or profile after binding works without a
  scene reload. `note_on` previews the open instrument, or, when Edit mode
  is on, enters the note at the cursor exactly like typing on the computer
  keyboard (`_on_note_key_pressed`: also previews, uses the cell editor's
  volume and the open instrument, and lands in the grid undo history; key
  velocity is ignored). `action_requested` is carried out by
  `ChiptrackerMainView._on_midi_action()`.
- In a running project (Play) the node's own `_input()` receives MIDI
  directly and emits the same signals; the Instrument panel doesn't exist
  there, so nothing previews.

Constraints and limits:

- macOS CoreMIDI can't reopen a driver once closed in the same process
  ("MIDIDriverCoreMidi cannot be reopened"), so MIDI is opened once and
  never closed. Calling `open_midi_inputs()` again while open is harmless.
- The profile scripts must stay `@tool`, or a profile loaded from a saved
  scene is an inert placeholder and `accepts_channel()` etc. fail.
- The MPK Mini 4's Octave buttons send no MIDI; the device shifts the note
  numbers it sends, so previews follow them without any code.
- Pads send notes on channel 10 and currently behave like keys (they preview
  and enter pitched notes 36-43) until they are assigned something else.
- Not built: binding knobs, pads, wheels or the remaining buttons to
  actions, and a warning/auto-create when a song node has no MIDI child.

MIDI Keyboard tab (bottom dock, third tab after Tracks and Groups):

- Lists every current mapping from the active profile's `get_mappings()`,
  function on the left and input on the right, rebuilt each time the tab is
  opened (`ChiptrackerDock.midi_view_shown`, answered by
  `ChiptrackerMainView._refresh_midi_mappings()`).
- "Show Diagram" (top right, disabled when there's no profile or diagram)
  opens a popup `Window` containing `MidiDiagramView`
  (`res://addons/chiptracker/ui/midi_diagram_view.gd`). It draws the
  profile's diagram, marks each control by state (green = assigned, dimmed
  = reserved by the device, unmarked = free to map), outlines the hovered
  control in orange and shows a tooltip with its name and current function.
  Rectangles come from the profile in the image's own pixels and were
  measured by eye, so they may need small adjustments. (The eight knobs were
  re-measured from the image's pixels: each ring's bounds were found by
  flood-filling its dark outline, and the bottom row's rectangles moved up 4
  px to sit flush like the top row's.)

## Pattern/song audio cache (M8)

Motivation: every playback-timing bug fixed earlier this session
(buffer-length prefill skip, bursty buffer-availability fills,
pacing-carry loss under backpressure — see git history around
`PlaybackEngine._process()`) came from synthesizing audio live, under a
hard real-time deadline, on the same thread as everything else in the
editor. This sidesteps that whole category of problem for previewing:
render to a plain sample buffer once, outside real time (the same
non-real-time loop `WavRenderer` already uses), then just stream it —
no live synthesis, no per-sample mixing cost, no dependence on frame
timing during actual playback.

### Scope for this milestone

- One cached buffer per pattern (rendered the same way `WavRenderer`
  renders a whole song, scoped to a single pattern's rows), stored as
  something seekable — an `AudioStreamWAV` is the natural choice, since
  `AudioStreamPlayer.play(from_position)` already seeks into a static
  WAV stream natively with no custom seek logic needed.
- **Bar preview (spacebar in Edit mode) needs no caching at all.**
  Render the 16-row bar synchronously the instant Space is pressed —
  bounded and small enough to be effectively instant (confirmed live: a
  full 64-row/6-channel pattern rendered in well under a frame's
  budget; a 16-row bar is a quarter of that).
- **Dirty-tracking:** invalidate a pattern's cached buffer whenever a
  cell/instrument/channel edit touches it. Hook into the existing
  `ChiptrackerMainView._mark_dirty()` call sites rather than adding a
  parallel notification path.
- **Leaving Edit mode triggers a full cache rebuild** (every pattern
  referenced by `order_list`) — a clean, deliberate signal that active
  note-entry has paused. After that, full Play and cursor-start
  playback always stream the cached version instead of live-
  synthesizing. Starting from the cursor is a sample-offset lookup:
  `row_index * samples_per_row`, extended to a cumulative per-pattern
  offset table across `order_list` (same approach
  `Song.total_duration_sec()` already uses to sum per-pattern
  contributions), fed straight into `AudioStreamPlayer.play(from_position)`.
- Dock/instrument panel/MCP-bridge edits go through the same
  dirty-tracking hook as keyboard note entry — leaving Edit mode is the
  main trigger for proactively *rebuilding* the cache ahead of the next
  Play press, not the only thing that can *invalidate* it. (In
  practice, most editing happens with Edit mode on anyway — direct
  edits outside it feel clunky through this UI — and MCP-bridge edits
  already come with an expected waiting feel, so this covers the common
  case well.)
- **UI feedback:**
  - Play button's triangle icon turns orange when the cache is clean
    (matches the current song state, nothing to render before playing);
    reverts to its normal color the moment a dirtying edit lands.
  - If Play is pressed while a background rebuild is still in flight:
    wait for it rather than falling back to live synthesis or blocking
    the button outright. Show a radial/circular progress indicator
    overlaid on the Play button for the render's duration, then start
    playback and switch the triangle to orange.

### Explicitly out of scope for M8 (possible follow-ups, not blocking this milestone)

- **Per-channel cache + single-note "stitching"** instead of a full
  pattern re-render on every edit. Sound in principle — tempo + row
  index gives an exact sample offset — but has a real subtlety: a
  note's synthesized duration comes from `_rows_until_next_note()`
  (rows until that *same channel's* next note), so adding or removing a
  note can retroactively change how long the *preceding* note on that
  channel needs to sustain. A correct incremental patch may need to
  regenerate two spans (the previous note plus the edited one), not
  just the single edited row. Revisit only if full-pattern re-render
  ever isn't fast enough in practice — expected to already be fast
  enough at realistic pattern sizes based on live timing tests.
- **Moving the cache render to a background `Thread`** instead of
  running synchronously on the main thread. The bar-preview case is
  small enough this shouldn't matter; a whole-song rebuild on leaving
  Edit mode is a less bounded cost and the more natural candidate for
  this if the radial progress indicator ends up showing for a
  noticeable amount of time in practice.

## Live recording (M9)

With playback looping the current pattern, a key press writes a note into
the pattern at the row being played, so a part can be performed over the
loop instead of typed row by row. Design agreed 2026-09-19.

**Decisions.**

- **Explicit Record toggle** beside the EDIT button, mappable to a MIDI
  button. On the Akai MPK Mini 4 it is the AUTOMATION button (CC 78), via a
  new `toggle_record` action.
- **Target row: nearest.** A press goes to the playing row if under half a
  row into it, otherwise to the next row. At the end of the looped pattern
  "next" wraps to its first row.
- **Volume:** the cell editor's volume, as when typing. Key velocity is
  ignored.
- **Loop the current pattern.** With the Loop button on, Play repeats the
  order-list entry it started in until stopped. Recording is done over that
  loop.
- **Hearing it.** The press previews instantly through the instrument
  panel. The recorded note joins the looped backing on the next pass, after
  the cache is rebuilt at the loop point (or on stop) -- there is no separate
  live layer.
- **Note length:** none. Key release is ignored and a note sounds until the
  channel's next note, so there is no data-model change.
- **Chords:** everything goes to the channel under the cursor; a second key
  on the same row overwrites the first.
- **Space stops.** While Record is on, Space stops playback instead of
  starting the 16-row bar preview (which would fight the recording pass for
  the playhead and stream). With nothing playing it does nothing, and Record
  stays on. With Record off, Space is the bar preview as before.
- **Count-in:** an optional toggle; when on, one bar of clicks plays before
  the pattern loop starts. The clicks are built in (`CountIn`), so it works
  with or without a Metronome in the song.

**Build order.**

1. Pattern looping for cached Play. **Done.** `CachedPlaybackEngine.loop`;
   `play()` sets the `AudioStreamWAV` loop points to exactly the pattern it
   started in (`_apply_loop_range()`), so the audio thread wraps it with no
   gap, and the playhead wraps because the polled position jumps back. The
   Loop button now sets it (it previously set only the live engine used by
   bar preview, which still wraps the whole song). The setting is read at
   `play()`, so toggling Loop while already playing applies on the next Play.
   Covered by `tests/m9_loop_test.gd`. Confirmed by ear: the loop is
   gapless.
2. Record toggle, and writing at the playing row. **Done.**
   `LiveRecord.target_row()` / `resolve()` (pure, engine layer) turn the
   playhead into a (order entry, row); `ChiptrackerMainView._on_note_key_pressed`
   uses it while Record is on and playback is running, so it applies to MIDI
   and computer-keyboard notes alike. A `REC` button (built in code in
   `CellEditorBar`, right after EDIT) turns Record on and Edit on with it;
   turning Edit off turns Record off. Not playing, Record behaves like
   Edit mode (writes at the cursor). The cursor doesn't move during a take.
   Each note is one grid undo step. Covered by `tests/m9_record_test.gd`.
3. Rebuild at the loop point, marking only the edited pattern dirty.
   **Done.** `PatternAudioCache.mark_pattern_dirty()` (alongside
   `mark_all_dirty()`) lets a note edit re-render only its own pattern, since
   each pattern is rendered in isolation; a recorded note uses it
   (`ChiptrackerMainView._mark_pattern_dirty()`). `CachedPlaybackEngine`
   emits `looped` when a looping pass wraps (the polled row goes backwards in
   the same order entry, `_poll()`), and the tab answers by rebuilding if
   anything is dirty and calling `refresh_stream()`, which swaps in the
   rebuilt stream, re-applies the loop range, and restarts the player at the
   position the old stream had reached (plus
   `AudioServer.get_time_since_last_mix()`). So a recorded note is heard from
   the next pass. Stopping needs nothing extra: Play and leaving Edit mode
   already rebuild a dirty cache. Covered by `tests/m9_rebuild_test.gd`.
   The swap restarts the player rather than editing the stream in place, so
   a tiny discontinuity at the wrap is possible; not yet checked by ear.
4. Latency compensation. **Done.** Two parts. (a) The playhead is read at the
   moment of the press: `CachedPlaybackEngine.now_sample()` (position plus
   `AudioServer.get_time_since_last_mix()`) and `locate()`, instead of the
   per-frame value in the cursor. The per-frame `_poll()` is unchanged, since
   adding the mix time there could make the position step backwards and
   fake a loop wrap. (b) A `Lat` box beside REC (milliseconds, 0-500) sets how
   far back from the playhead a note is placed: `LiveRecord.resolve()` takes
   the playhead back by that many samples before choosing the nearest row,
   wrapping to the end of the same pattern when looping, going into the
   previous order entry when not, and clamping at the start. It covers audio
   output delay (the polled position runs ahead of what is heard) and key
   delay. It is a per-machine setting, kept in the editor settings
   (`chiptracker/record_latency_ms`) and not in the song; until it has been
   set it defaults to the audio output latency. `resolve()` now takes the
   row counts of every order entry (`rows_in_order`) instead of one pattern's.
   Covered by `tests/m9_record_test.gd` and `m9_rebuild_test.gd`. The editor
   settings persistence and the feel of the default value are not yet checked
   in the editor.
5. One undo step for a whole take. **Done.** `GridEditHistory` keeps its flat
   stack of one-cell steps, but steps pushed between `begin_group()` and
   `end_group()` share a `group_id`, and `undo_group()` / `redo_group()` pop a
   whole group (newest first when undoing, oldest first when redoing, so a
   cell written twice in a take is restored correctly). `peek_undo_label()`
   reads "Recorded take, N notes" for a group. In `ChiptrackerMainView` a
   note written at the playing row joins the current take
   (`_record_cell_edit(..., recording=true)`); any other edit, Undo, Redo,
   turning Record or Edit off, Stop, playback finishing, or pressing Play
   again closes it, so the next recorded note starts a new take. Undo and
   Redo buttons/keys and the Akai's undo/redo all act on a whole take.
   The 100-step cap still counts individual notes, so a take of more than 100
   notes can't be fully undone. Covered by `tests/m9_take_test.gd`.
6. Optional count-in. **Done.** A `Count-in` checkbox beside REC (a
   per-machine preference, `chiptracker/record_count_in` in the editor
   settings, off by default). `CountIn.render()` makes one bar (four beats) of
   noise clicks with the metronome's envelope and accent scheme, and does not
   depend on any channel. When the box is on and Record is turned on with
   nothing playing, or Play is pressed while Record is on, `play(..., true)`
   builds the stream as [clicks][the song from the start of the pattern]
   (`_build_counted_stream()`), so the handover is sample-exact. `_prefix` is
   the clicks' length and `_bias` converts a song sample to a stream position;
   the loop range, the position poll (`_poll()`), `now_sample()` and
   `refresh_stream()` all account for it, and `refresh_stream()` drops the
   clicks since they're over by the first wrap. `PlaybackCursor.counting_in`
   is true meanwhile and the playhead waits at row 0. The pass always starts
   from row 0 of the pattern (the cursor row is ignored). Notes played during
   the count-in are only previewed. Record while already playing just arms,
   and Play with Record off never counts in. Covered by
   `tests/m9_countin_test.gd`; not yet checked by ear/eye in the editor.
7. Map AUTOMATION to `toggle_record` in `AkaiMPKMini4.BINDINGS`. **Done**
   (`MidiKeyboard.ACTION_TOGGLE_RECORD`).

**Settled while building.**

- Turning Record on also turns Edit mode on, and turning Edit off turns
  Record off.
- With playback not running, Record behaves like Edit mode: notes go to the
  cursor.
- The cursor stays put during a take.
- The count-in clicks with a built-in sound, not the song's Metronome.

## Conventions

- GDScript, typed (`var tempo: int`, `func set_note(...) -> void:`) throughout
  — this project will have LLM-driven callers on both sides (MCP + Claude
  Code), and static typing catches drift early.
- One `class_name` per file, matching filename (`song.gd` → `class_name Song`).
- Keep the bridge, engine, and UI in separate folders: `res://engine/`,
  `res://bridge/`, `res://ui/`. Nothing in `engine/` may reference `bridge/`
  or `ui/`.
- Every new MCP command added to the bridge must be added to this doc's
  command list in the same change, so the spec doesn't drift from the code.
- Prefer small, runnable test scenes over speculative abstraction — this
  project rewards verifying audio output early and often over getting the
  "right" class hierarchy up front.

## Open questions to resolve during the build

- ~~Tempo model~~ — **Resolved in M1**: BPM + rows-per-beat. `Song.tempo`
  is BPM (int), `Song.rows_per_beat` (int, default 4) sets how many rows
  make up one beat. Row duration in seconds = `60.0 / tempo / rows_per_beat`.
- ~~Volume scale~~ — **Resolved in M1**: 0-15 (NES-style, 4-bit). `Cell.volume`
  and instrument-level volume both use this range.
- ~~WebSocket vs raw TCP~~ — **Resolved in M5**: WebSocket, implemented with
  plain `TCPServer` + `WebSocketPeer` (no external Godot library). MCP server
  is Python, using the official `mcp` SDK (pinned `mcp<2`; v2 renamed
  `FastMCP` to `MCPServer` and changed its API) plus `websockets` as the
  bridge client. Server lives at `mcp_server/server.py` (a `uv run`-able
  PEP 723 script, sibling to `chiptune-plugin/`), exposing the 8 commands
  below as `chiptracker_*` tools.

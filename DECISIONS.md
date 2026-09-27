# Chiptracker build decisions

Implementation choices made during the build that the spec left open,
per CHIPTRACKER_SPEC.md's "prefer the simplest option ... note the choice
here rather than asking" rule. None of these affect the data model or MCP
contract.

## M6 — Pattern grid UI

- **Cell editing via a side control bar, not in-grid keyboard entry.**
  `PatternGridView` (`addons/chiptracker/ui/pattern_grid.gd`) is display +
  click-to-select only. A separate `CellEditorBar`
  (`addons/chiptracker/ui/cell_editor.gd`) holds Note/Instrument/Volume
  SpinBoxes bound to whichever cell is selected. A full piano-keyboard note
  entry scheme (the usual FamiTracker-style QWERTY-row-to-note mapping) is
  a reasonable follow-up but adds real complexity; this gets a working
  editable grid now.
- **Instrument envelope UI is a fixed 4-point editor**, not a variable-length
  list. Matches how the engine (`Synth._envelope_value`) already treats
  `envelope` as ordered breakpoints — 4 points is enough to shape
  attack/decay/sustain/release without building add/remove-point UI.
- **No WAV-export button in the M6 UI.** The spec's M6 description
  (transport bar: play/stop/loop/tempo/row-pattern; grid; instrument panel)
  doesn't call for one, and `render_wav` is already exposed via the M5 MCP
  bridge. Can be added later as a bottom-dock button calling
  `WavRenderer.render_song` directly if wanted.

### Gotcha worth remembering: `@tool` is required for main-screen UI

Every script that lives on a node inside the Chiptracker main-screen tab or
its bottom dock needs `@tool` at the top (`chiptracker_main_view.gd`,
`pattern_grid.gd`, `cell_editor.gd`, `instrument_panel.gd`,
`transport_bar.gd`, `chiptracker_dock.gd`, and `playback_engine.gd` since a
`PlaybackEngine` node lives inside the main view for live preview). Without
it, Godot loads the script as an inert "placeholder" instance when the
`EditorPlugin` instantiates the scene — no `_ready()`, no `_draw()`, no
signal handlers ever run, but the node tree's *static* `.tscn`-defined
properties (default text, default SpinBox values) still display, making the
UI look present but completely dead. The only sign is a
`SCRIPT ERROR: ... Attempt to call a method on a placeholder instance`,
and Godot AI's MCP log capture misses it because it attaches after plugins
finish their early boot — worth checking the actual editor stdout (run the
binary directly rather than via `open -a`) if a from-scratch editor UI
plugin ever looks inert like this again.

## Grid-edit undo/redo (separate from the editor's own)

Requested as a standalone feature, not tied to a spec milestone: undo/redo
for note/instrument/volume cell edits on the pattern grid, capped at 100
steps, kept entirely separate from Godot's own `EditorUndoRedoManager` (so
it doesn't interact with, or get cleared by, unrelated editor undo state).

- **New `GridEditHistory` engine class** (`engine/grid_edit_history.gd`,
  `RefCounted`) rather than wiring into `EditorUndoRedoManager` — the ask
  was explicitly for a separate stack, and `EditorUndoRedoManager` isn't
  available outside the editor anyway (would break the headless test
  harness the same way `EditorInterface` calls already have to be guarded
  against, per the M6 UI gotcha below).
- **Steps store `(pattern_index, row, channel)` + before/after
  `Cell.snapshot()` dictionaries**, not a live `Cell` reference — a step
  needs to still make sense (or safely no-op, via `ChiptrackerMainView.
  _cell_at()`'s bounds checks) if the referenced pattern/row/channel later
  shrinks or gets removed by an unrelated structural edit.
- **UNDO/REDO ended up as plain colored text buttons (red/green), not
  icons** — landed there after three failed icon attempts, in order:
  1. Godot's stock EditorIcons theme has no "Undo"/"Redo" icon at all
     (confirmed against the running editor: `has_theme_icon("Redo",
     "EditorIcons")` is true, but `"Undo"` is false, oddly asymmetric — no
     `ActionUndo`/`GuiActionUndo` equivalent either) — tried "Back"/
     "Forward" as a same-shape substitute, but a themed icon also
     wouldn't have been colored the way these needed to be regardless.
  2. Switched to a procedurally-drawn curved-arrow `Control` (vector
     `draw_polyline`/`draw_colored_polygon`), same "draw it ourselves"
     approach the edit-mode status dot below and PatternGridView's grid
     already use — visually fine, but replaced per the next ask.
  3. Replaced with a hand-drawn Aseprite pixel-art icon (a curved hook +
     triangular arrowhead, red/green, mirrored via `flip_layer` +
     `replace_color`) once "design your own icon in Aseprite" was asked
     for specifically — exported as real PNG assets and wired in as
     `Button.icon`. **This one silently failed to render, but only inside
     the actual editor main-screen tab** — it rendered correctly offscreen
     (a throwaway `SubViewport` + `Image.save_png()` script; `--headless`
     uses Godot's dummy renderer, which never renders a frame, so that
     capture needs a normal non-headless run) and even when the scene was
     run directly as a game (`project_run` with `mode: "custom"` +
     `editor_screenshot(source: "game")` — the only way found to get a
     real screenshot of this addon's UI at all, since `editor_screenshot`
     only captures the 3D/2D scene viewport, not a plugin's own
     main-screen tab content). Root cause: `Button.expand_icon` computes/
     caches its icon draw rect against the button's laid-out size, and
     the Chiptracker tab starts `visible = false` (`plugin.gd`, standard
     `EditorPlugin` pattern, hidden until the tab is first selected) — so
     the button's first layout pass happens while still hidden, and the
     icon rect never gets recomputed once later shown. Confirmed by
     reproducing that exact hidden-then-shown sequence offscreen.
     Dropping `expand_icon` (rendering at native pixel size instead) fixed
     the repro, but text buttons were requested instead by that point —
     simpler and with no layout-timing dependency at all. The Aseprite
     assets (`ui/icons/undo_arrow.png` / `redo_arrow.png`) are still in
     the repo, unreferenced, in case icons come back later.
  4. **Lesson for next time an editor-main-screen-tab icon looks broken
     only in the live editor**: suspect `expand_icon` (or anything else
     that sizes off the button's own layout) before suspecting the asset
     or the import pipeline — reproduce the plugin's actual
     hidden-then-shown `visible` sequence offscreen rather than assuming
     "works when I test it in isolation" rules out a live-editor-only bug.
- **Ctrl+Z/Ctrl+Y (not Cmd)** for the keyboard shortcuts, checked via
  `event.ctrl_pressed and not event.meta_pressed` in
  `PatternGridView._input()` — keeps Cmd+Z/Cmd+Shift+Z free for the Godot
  editor's own undo/redo on macOS. Both are gated behind `edit_mode`, same
  as the other grid shortcuts, and checked *before* the `NOTE_KEY_OFFSETS`
  lookup, since Z and Y are also note keys (bottom-row Z, top-row Y) and
  would otherwise be read as plain note entry with the Ctrl modifier
  ignored.

## M7 — Scene persistence surfaced the same gotcha in the engine layer

`Song`/`Channel`/`Pattern`/`Cell`/`Instrument` (`addons/chiptracker/engine/`)
were plain `Resource` scripts with no `@tool`, which was harmless through
M1-M6 since every instance was always created fresh at runtime via `.new()`
inside an already-`@tool` UI script. `ChiptrackerSongNode` changed that: its
`song: Song` export gets loaded by the editor as a real serialized
sub-resource (from a saved `.tscn`), and Godot instantiates a script-less
`Resource` script as an inert placeholder in that case exactly like the
node-script version of this bug — so `_on_rows_per_pattern_changed` calling
`song.set_rows_per_pattern(...)` failed silently (logged as "Attempt to
call a method on a placeholder instance") and the Rows field in the
transport bar stopped actually growing the pattern. Fixed by adding `@tool`
to all five engine Resource classes. Lesson: `@tool` isn't just for node
scripts living in the editor tree -- ANY custom Resource script that might
get loaded from a saved scene/resource file while the editor is open needs
it too, not only ones created purely in memory at runtime.

## MIDI keyboard input — where hardware MIDI does and doesn't arrive

`ChiptrackerMidiNode` + a `MidiKeyboard` profile Resource (see "MIDI
keyboard input" in CHIPTRACKER_SPEC.md). Three things took real debugging
and are worth not rediscovering:

- **Edited-scene nodes get no hardware MIDI in the idle editor; the
  editor's own nodes do.** A `@tool` node's `_input()` receives
  `InputEventMIDI` in Play but never while just editing (confirmed on
  macOS, Godot 4.7.2, regardless of the active main-screen tab).
  `ChiptrackerMainView` is owned by the editor and does receive it, so it
  forwards events to the song node's `ChiptrackerMidiNode`
  (`handle_midi_event`). The MIDI node is looked up per event instead of
  cached at bind time, because a cached reference went stale whenever the
  node or profile was added after the tab had already bound the song node.
- **Never call `OS.close_midi_inputs()`.** macOS CoreMIDI can't be reopened
  within a process, so closing in `_exit_tree()` killed MIDI for the rest
  of that editor session the first time the node left the tree (scene
  reload, deletion). It stays open for the life of the process.
- **Profile scripts need `@tool`** for the same placeholder-instance reason
  as the M7 engine Resources: without it a profile loaded from a saved
  scene can't run `accepts_channel()` in the editor.

Process lesson: when adding a debug print to check whether something fires,
confirm it can actually be seen. Removing the per-event print during cleanup
made later "no events in Play" results meaningless and caused a long detour
(restarts, device modes) chasing a problem that wasn't there. Also, editing
a script's `_ready()` doesn't re-run it on existing scene nodes — reload the
scene to test `_ready()` changes.

## MIDI actions, the MIDI Keyboard tab and the diagram

- **Profiles return abstract actions, the tab performs them.** A profile
  maps its own buttons to action names (`MidiKeyboard.ACTION_*`) through
  `action_for_control_change()`; `ChiptrackerMidiNode` emits
  `action_requested` and `ChiptrackerMainView._on_midi_action()` does the
  work. The tracker never needs to know which button on which device was
  pressed, so a second keyboard only needs its own profile.
- **One table drives behaviour and display.** `AkaiMPKMini4.BINDINGS` decides
  what a press does and feeds `get_mappings()` and the diagram markers, so
  the tab can't drift from what actually happens.
- **Names follow the labels printed under the buttons.** The physical UNDO
  button was captured as "REDO" (CC 73) because that's the label under it;
  it's mapped to undo. The code and `MidiKeyboardMap.md` say so explicitly.
- **Reserved buttons are not "free".** ARP, LATCH (button), NOTE REPEAT,
  PLUGIN/DAW send no MIDI, and OCT -/+ are handled by the device, so the
  diagram dims them instead of leaving them unmarked.
- **Double press is timed at handling.** The play double-press window (350
  ms) is measured when the event is processed, so a slow first press that has
  to rebuild the audio cache can push the second press outside the window.
- **Knob CCs were guesses until captured.** `KNOB_CC` was first filled from
  memory (70-77) and was wrong; the real values (24-31) came from a MIDI
  monitor capture. Capture first, then write the constants.

## Metronome — a real channel with real cells, found by a reserved name

- **Real channel, group, instrument and cells, not a virtual click.** The
  metronome shows in the grid, the mixer's mute/solo and the Groups tab like
  anything else, and Play/the audio cache need no special path. The price is
  that `Song.sync_metronome()` must re-run whenever pattern count, row count
  or `rows_per_beat` changes (it's called from those setters), and that hand
  edits to the channel are overwritten on the next sync.
- **Identified by the exact name `Metronome`, not a new field.** That's why
  the word is reserved (case-insensitively) and the three can't be renamed;
  it avoids a marker field on Channel/ChannelGroup/Instrument. Only
  `Song.metronome_accent` was added to the data model, for the Accent toggle.
- **Protection lives in the engine where it can.** `Song.remove_channel()` /
  `remove_group()` refuse the metronome, so the UI and the MCP bridge share
  the rule; the dock adds the parts that only make sense in the UI (no
  renaming, no moving between groups, Remove defaults skipping it).
- **Accent is volume, not pitch.** The click is white noise, which ignores
  the note, so the downbeat can't be a higher tick.
- **WAV export skips it via `PlaybackState.exclude_metronome`**, set only by
  `WavRenderer`; Play and the audio cache keep it so it's audible. The mix
  divides by the number of audible channels, so having a metronome lowers the
  overall level a little during Play (not in exports).
- **Refusing the last channel.** `remove_metronome()` refuses when the
  metronome is the only channel, since a song needs at least one.

## M9 live recording — decisions worth keeping

- **Loop by stream loop points, not by restarting.** Looping the pattern sets
  `AudioStreamWAV.loop_begin/loop_end` on the cached stream, so the audio
  thread wraps it sample-exactly (confirmed gapless by ear). The playhead
  wrap is inferred from the polled row going backwards, which is what
  `CachedPlaybackEngine.looped` reports.
- **Hearing a recorded note = rebuild at the wrap.** No separate live layer:
  the key press previews instantly, and the note joins the loop after the
  cache is rebuilt and the stream swapped in at the next wrap. Only the edited
  pattern is re-rendered (`mark_pattern_dirty`), since each pattern is
  rendered in isolation. The swap restarts the player at the same position
  rather than editing the stream in place.
- **Don't add `get_time_since_last_mix()` to the per-frame poll.** It can make
  the position step backwards, which the wrap detection would read as a loop.
  It is added only where a position is read once, at a key press
  (`now_sample()`) and when swapping the stream.
- **Latency is a per-machine setting, not part of the song.** The `Lat` box
  is stored in the editor settings and defaults to the audio output latency;
  `LiveRecord.resolve()` shifts the playhead back by it before rounding.
- **A take is a group of ordinary undo steps.** `GridEditHistory` keeps its
  flat one-cell steps and adds a group id, so existing undo/redo behaviour and
  its tests are untouched. Any edit that isn't a recorded note closes the
  take, so a take can't swallow a typed edit. The 100-step cap still counts
  notes.
- **Count-in is part of the stream.** One bar of built-in clicks is prepended
  to the stream that plays ([clicks][song from the pattern start]) rather than
  timed separately, so the handover is sample-exact; a timer or a second
  player couldn't promise that. It starts from row 0 of the pattern (the
  cursor row is ignored), and uses built-in clicks so it needs no Metronome.
  A bias between song samples and stream positions (`_bias`) keeps the row
  maths, loop range and stream swap correct with the prefix in front.
- **Space stops while recording** instead of starting the bar preview, which
  would fight the recording pass for the playhead and stream.
- **A crash worth knowing about (cause found).** Godot 4.7.2 segfaults if a
  script is hot-reloaded so that it gains a new member variable while an
  instance made before the reload is still alive, and code then uses that
  member on the old instance. Reading the member gives null; the first method
  that touches it crashes the editor (null read at a small offset, in a
  nested chain of script calls). Reproduced in a minimal headless probe whose
  backtrace has the same six Godot code addresses as the 22:21 crash report on
  2026-09-20; the 21:47 report on 2026-09-19 shares its call pattern (that day
  `PatternAudioCache` had just gained a member), so it is very likely the same
  bug. It is an engine bug, not the tracker's logic. Adding `Instrument.envelope_times` while the song's instruments (which
  live in the open scene and are not recreated by a plugin reload) were alive
  triggered it on the first envelope drag.
  **Rule:** after a change that adds or removes a member variable of a class
  whose instances live in the open scene or in a cached resource (Song,
  Instrument, Channel, Cell, Pattern, the MIDI profile...), restart the
  editor before using the affected feature. A plugin reload is not enough (it
  recreates only the plugin's own nodes), and the editor hot-reloads changed
  scripts on its own the moment it regains focus, so the risk starts as soon
  as the files change. Save the scene before trying the new feature.
  A third crash (18:39, 2026-09-20) has different frames and is unexplained.

## Envelope point times — a parallel list, empty meaning "evenly spaced"

Envelope points had no x value: `Instrument.envelope` is only levels, spaced
evenly across the note. Making x editable needed somewhere to store it.

- **`envelope_times`, a second array, rather than changing `envelope`.**
  Empty (or the wrong length) means the old even spacing, so every existing
  song, saved `.tres` and MCP-created instrument sounds exactly as before and
  needs no migration. Changing `envelope` to points would have touched every
  consumer and every saved file.
- **The synth's even-spacing path is left as it was**; the custom path only
  runs when times exist, so ordinary instruments pay nothing extra. The
  custom lookup holds the first/last level outside the points, and at exactly
  a step's time returns the later value.
- **The bounds live in `Instrument.set_point_time()`**, not only in the graph,
  so the graph, the time box and any other caller can't put points out of
  order. Both the graph and the box also limit themselves (the box's range is
  set to the neighbours'), so the user never sees a value bounce back.
- **Shift snaps time as well as level to 0.1**, then holds the result between
  the neighbours, so a snapped value can never pass one.
- **Adding a point at time 1** and moving a point already there to halfway, so
  no two points are ever stacked where one can't be grabbed.
- **The time box shows 0.01 steps** (display only: the instrument keeps the
  exact time), matching the value box.
- Extended the MCP `add_instrument` and `get_song` for it, since the schema
  must match the data model.

## Envelope archetypes — generate the volume shape only, from keyframes

- **Envelope only, not waveform or pulse width.** Picked by the user: RANDOMIZE
  never overwrites the instrument's waveform, so it's safe to try on a
  finished instrument. The archetypes' waveform hints stay in
  `CommonEnvelopes.md`.
- **A fixed 500 ms reference note.** Times are fractions of the note, so
  milliseconds need a note length to mean anything. A fixed reference keeps
  the archetypes' relative lengths (a hi-hat stays shorter than a snare). The
  alternative, stretching each shape to fill the note, would make a hi-hat and
  a crash the same length. Shapes longer than 500 ms are squeezed to fit.
- **Release is a tail in the shape.** A note here has no key-release: it runs
  until the channel's next note. So "R" can only exist as the end of the
  envelope.
- **Keyframes, then add or drop points.** Each archetype is a short keyframe
  list. More points than keyframes: split the widest gap on the curve. Fewer:
  drop the keyframe closest to a straight line between its neighbours, so a
  3-point slap bass still has its pluck and its sustain.
- **The echo lead's ghost notes are steps in the envelope.** The doc's "trigger
  a ghost note 3 frames later" is a note-level trick; the envelope version is a
  fade, then a step back up to 0.4, a fade, a step to 0.15, a fade.
- **Variance is proportional and bounded.** Levels change by a fraction of
  themselves (silence stays silent, nothing passes 1), the whole shape
  stretches a little, and each point wanders a little within its gaps (halved,
  since it compounds with the stretch). Bounded so any draw is still
  recognisably the archetype, and the tests hold that for fifty draws.
- **RANDOMIZE is capped at 60 nodes** because the consoles being imitated step
  an envelope once per 60 Hz frame, so 60 a second is the finest change they
  could make (a 500 ms reference note is about 30 frames). Hand-drawn
  envelopes are not capped.
- **The synth lookup became a binary search** because 255 points scanned per
  sample would have made cache rebuilds slow; a test holds it to the plain
  scan's answers on 200 random envelopes, with steps.
- **Restart after this change.** It adds members to `InstrumentPanel`, which is
  live in the open editor; see the hot-reload crash rule above.

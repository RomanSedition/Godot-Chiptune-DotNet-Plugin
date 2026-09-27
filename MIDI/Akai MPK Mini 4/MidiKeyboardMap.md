# Akai MPK Mini 4 — input map

Names are the labels printed on the device. Message data was captured with
a MIDI monitor (Chrome MIDI Control Mapper), so it reflects what the device
actually sends in its current preset.

The **Chiptracker function** column is for planning which action each input
will trigger. It is empty until you decide.

## Channels

The monitor shows channels 1-based; Godot's `InputEventMIDI.channel` is
0-based, so subtract 1 in code.

| Sent on | Monitor channel | Godot `channel` |
|---|---|---|
| Keys, buttons, knobs, wheels | 1 | 0 |
| Pads | 10 | 9 |

## Knobs (CC, channel 1)

Numbered 1-8 on the device, top row left to right (1-4), then bottom row
(5-8). They send consecutive CCs.

| Knob | Name | CC | Chiptracker function |
|---|---|---|---|
| 1 | DIVISION | 24 | |
| 2 | SWING | 25 | |
| 3 | MODE | 26 | |
| 4 | OCT | 27 | |
| 5 | LATCH KNOB | 28 | |
| 6 | SYNC | 29 | |
| 7 | GATE | 30 | |
| 8 | BPM | 31 | |

`LATCH KNOB` is knob 5. It is separate from the LATCH *button* in the ARP
row, which hasn't been captured.

## Pads

Notes on channel 10 (Godot channel 9). Velocity is the pad strike strength.
PAD 1-4 are the bottom row and PAD 5-8 the top row, left to right.

| Name | Message | Number | Chiptracker function |
|---|---|---|---|
| PAD 1 | Note | 36 | |
| PAD 2 | Note | 37 | |
| PAD 3 | Note | 38 | |
| PAD 4 | Note | 39 | |
| PAD 5 | Note | 40 | |
| PAD 6 | Note | 41 | |
| PAD 7 | Note | 42 | |
| PAD 8 | Note | 43 | |

## Buttons (CC, channel 1)

| Name | CC | Chiptracker function |
|---|---|---|
| SHIFT | 17 | |
| BANK (the BANK A/B button) | 12 | |
| - | 80 | |
| + | 81 | |
| TAP TEMPO | 82 | Tap tempo (tap along to set the tempo) |
| REDO (the UNDO button) | 73 | Undo grid edit |
| GLOBAL | 74 | Redo grid edit |
| CONTINUE | 76 | Play; pressing while playing stops; double press stops and moves the cursor to the top of the first pattern |
| QUANTIZE | 77 | Toggle Edit mode |
| AUTOMATION | 78 | Toggle Record (write notes at the playing row) |

REDO, GLOBAL, CONTINUE, QUANTIZE and AUTOMATION are the labels printed
under the UNDO, loop, stop/play, record and plus buttons. CC 75 sits
between REDO/GLOBAL and CONTINUE and hasn't been seen.

## Wheels and big encoder

| Name | Message | Range | Chiptracker function |
|---|---|---|---|
| MODULATION WHEEL | CC 1 | 0-127 | |
| PITCH WHEEL | Pitch bend, channel 1 | 14-bit, centre 8192 | |
| VOLUME | CC 14 | 0-127 | |

`VOLUME` is the large encoder under the display. It last read 127, so it
may be relative like the eight knobs; confirm by turning it both ways.

## Observed value behaviour (not verified)

These come from the last value the monitor saw per input, so treat them as
hints to confirm before writing mapping code:

- **SHIFT, `-`, `+`, BANK, TAP TEMPO** last read 0, and **REDO, GLOBAL,
  CONTINUE, QUANTIZE, AUTOMATION** last read 127. Buttons probably send 127
  on press and 0 on release; press each and watch both values to be sure.
- **The eight knobs** last read 1 or 2, except OCT and LATCH KNOB which read
  126 (OCT read 1 in the earlier capture and 126 in this one). That looks
  like relative increments: small values mean turn one way, values near 127
  mean the other. Confirm by turning each knob both ways.
- The physical OCT-/OCT+ buttons send no MIDI at all (the device shifts the
  note numbers it sends instead), so they are not listed.

## Not captured yet

Buttons and inputs printed on the device but absent from the captures:

- The keyboard keys themselves (plain notes on channel 1).
- ARP, LATCH (button), NOTE REPEAT, PLUGIN/DAW: these send nothing through
  the MIDI monitor. They appear to be reserved by the device for its own
  functions, so they can't be mapped to Chiptracker actions.
- The unshifted functions of UNDO, loop, stop/play, record and plus, if they
  send anything different from REDO/GLOBAL/CONTINUE/QUANTIZE/AUTOMATION.
- CC 75.

`AkaiMPKMini4.KNOB_CC` in `akai_mpk_mini_4.gd` holds the knob CCs above
(24-31), in knob order 1-8.

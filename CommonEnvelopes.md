# Common Envelopes

## Kick Drums

### 1. Standard 8-Bit Kick (Pulse/Triangle Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 150ms) | S: 0.0 | R: 0.0
- **Pitch Curve:** Frame 1: +24 semitones | Frame 2: +12 semitones | Frame 3+: 0 (base note)
- **Character:** The punchy, generic kick heard in most NES games.

### 2. The "Laser" Kick (Pulse Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 120ms) | S: 0.0 | R: 0.0
- **Pitch Curve:** Continuous, steep exponential sweep downward from +48 semitones down into sub-bass frequencies.
- **Character:** Ideal for sci-fi shoot-'em-ups or high-energy techno-chiptune.

### 3. Heavy/Gated Atari Kick (Noise Channel)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.6 | S: 0.6 (hold for 50ms) | R: 0.0 (instant cut)
- **Pitch Curve:** Pure low-frequency white noise with a fast 2-frame downward pitch slide.
- **Character:** Aggressive, crunchier kick typical of the AY-3-8910 chip.

## Snare Drums

### 4. Standard Noise Snare (Noise Channel)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 250ms) | S: 0.0 | R: 0.1
- **Pitch/Frequency:** Fixed white noise; no pitch change.
- **Character:** The quintessential, crisp arcade snare.

### 5. Metallic "Deep" Snare (Noise + Pulse Wave Layer)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 200ms) | S: 0.0 | R: 0.0
- **Pitch Curve (on Pulse layer):** Starts at +12 semitones, drops instantly to 0, and holds a low resonant frequency.
- **Character:** Recreates the iconic heavy Mega Man-style snare hits.

### 6. Explosion / Impact Snare (Noise Channel)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 500ms) | S: 0.0 | R: 0.5
- **Pitch/Frequency:** Dynamic shift starting at high-frequency noise and steadily filtering down to a low-frequency rumble.
- **Character:** Perfect for dramatic beats or simulating huge cinematic impacts.

## Hi-Hats & Cymbals

### 7. Closed Hi-Hat (Noise Channel)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 30ms) | S: 0.0 | R: 0.0
- **Pitch/Frequency:** Static; fixed at the highest possible pitch/frequency band.
- **Character:** Tightly controlled, ticking rhythm keeper.

### 8. Open Hi-Hat (Noise Channel)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 150ms) | S: 0.0 | R: 0.0
- **Pitch/Frequency:** Static; fixed at a high frequency (slightly lower pitched than the closed hat).
- **Character:** Provides breathing room and sustain to the drum top-end.

### 9. Crash Cymbal (Noise Channel)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.25 | S: 0.25 | R: 0.0 → 0.8 (long tail over 1.5 seconds)
- **Pitch/Frequency:** Starts at a piercing high frequency and slowly drops to a mid-range hiss as it decays.
- **Character:** Emulates a long, washed-out cymbal crash to finish a musical bar.

## Percussive FX

### 10. 8-Bit Tom-Drum (Triangle Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 200ms) | S: 0.0 | R: 0.0
- **Pitch Curve:** A smooth, linear downward pitch slide spanning roughly 12 semitones over the course of the decay.
- **Character:** Sounds like a classic synthesis disco tom or a retro game UI element.

## Lead Instruments

### 1. The "Mega" Duty-Swap Lead (Square/Pulse Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.85 (over 100ms) | S: 0.85 | R: 0.1 (soft cut over 50ms)
- **Timbre/Modulation:** Alternate the pulse width between 12.5% (thin) and 50% (hollow) on every note press to simulate a biting, shifting edge.
- **Character:** The primary, aggressive action-game lead sound heard in classic Capcom soundtracks.

### 2. Expressive Theremin / Violin (Triangle or Pulse Wave)

- **Volume Envelope:** A: 0.15 (smooth swell over 150ms) | D: 1.0 → 1.0 | S: 1.0 | R: 0.4 (long fade)
- **Pitch Curve:** Introduce a delayed LFO vibrato (±0.3 semitones at 6Hz) that only kicks in after the note has been held for 200ms.
- **Character:** Perfect for emotional, singing melodies or eerie sci-fi themes.

### 3. Echo / Delay Lead (Pulse Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (over 150ms) | S: 0.0 | R: 0.0
- **Macro Trick:** To fake an echo on a single hardware channel, trigger a "ghost note" exactly 3 frames later at Volume: 0.4, and another 3 frames later at Volume: 0.15.
- **Character:** Gives a solo melody a sense of space without using actual reverb effects.

## Basslines

### 4. Snappy Slap Bass (Low Triangle Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.3 (fast pluck over 80ms) | S: 0.3 | R: 0.0 (instant gate)
- **Pitch Curve:** Start at +12 semitones on Frame 1, dropping instantly to 0 on Frame 2 to create a transient string "pop".
- **Character:** High-energy, funky basslines that stay out of the way of the melody.

### 5. Heavy Drive Bass (Pulse Wave at 25% Width)

- **Volume Envelope:** A: 0.0 | D: 0.0 | S: 1.0 | R: 0.0 (hard gated stop)
- **Timbre/Modulation:** Keep the volume completely flat. Use a fast, constant pitch vibrato (±0.1 semitones) to emulate a warm, unstable analog hum.
- **Character:** Driving, rock-focused underground basslines.

### 6. Rubber Bass (Low Pulse Wave)

- **Volume Envelope:** A: 0.05 (slight softening) | D: 1.0 → 0.6 (over 150ms) | S: 0.6 | R: 0.1
- **Pitch Curve:** A subtle, quick pitch slide upward (-2 semitones up to 0) over the first 50ms of the note.
- **Character:** A bouncy, elastic synth bass that feels fluid and groovy.

## Plucks, Chords & Pads

### 7. Standard Chiptab / Arpeggio Chord (Square Wave)

- **Volume Envelope:** A: 0.0 | D: 0.0 | S: 1.0 | R: 0.1
- **Pitch Curve:** Loop a lightning-fast 3-note sequence (e.g., Frame 1: 0 | Frame 2: +4 | Frame 3: +7) at 60Hz.
- **Character:** The definitive 8-bit technique to fake a major triad chord on a single mono channel.

### 8. Tiny 8-Bit Music Box (Pulse Wave at 12.5% Width)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (smooth exponential decay over 800ms) | S: 0.0 | R: 0.0
- **Pitch Curve:** Static, but played 2 to 3 octaves higher than a standard lead.
- **Character:** Highly bright, crystalline, and metallic chime sound.

### 9. Soft Chiptune Pad (Triangle Wave)

- **Volume Envelope:** A: 0.4 (slow fade-in over 400ms) | D: 1.0 → 1.0 | S: 1.0 | R: 0.5 (smooth release over 500ms)
- **Pitch Curve:** Keep completely static and clean to maximize the smooth, sub-bass nature of the triangle wave.
- **Character:** Fills out the background layer of an arrangement without causing clutter.

## Special Effects & Accents

### 10. Coin / Power-Up Arpeggio (Pulse Wave)

- **Volume Envelope:** A: 0.0 | D: 1.0 → 0.0 (decaying over 300ms) | S: 0.0 | R: 0.0
- **Pitch Curve:** Frame 1 to 5: Hold a stable mid-range note | Frame 6+: Instantly jump up +12 semitones and hold until the volume fades.
- **Character:** The legendary "collectible item" sound popularized by classic platformers.

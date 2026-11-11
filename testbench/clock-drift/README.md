# Measuring the clock difference between two sound cards

When Tonebench plays its stimulus on one device and samples it on another, the two run on
independent crystals. **Epsilon is how far apart they run**, in parts per million, and it is the one
number the whole two-device question turns on: a duplex read/write loop across two cards drifts one
ring's worth of samples and then glitches, which is why the output and the input get a stream each
(`SoundCardStreamDirection`) rather than one duplex stream between them.

These scripts measure epsilon from a recording Tonebench has already written, so they need no
instrumentation in the app and nothing to be wired up beyond the measurement cable that is there
anyway.

## The idea

The generator plays a tone the **output** device clocks out; the analyzer samples it on the
**input** device's clock. Both are told the same nominal rate, so the frequency the recording
reports is

    commanded * (1 + a) / (1 + b)

where `a` and `b` are each card's own error against true time. Neither is separately knowable from
one cable, and neither needs to be: the ratio gives `epsilon = a - b` directly.

Frequency is estimated in two stages, because neither alone is enough. An FFT over the whole record
puts the tone inside half a bin; then the phase of a DFT evaluated at that frequency, segment by
segment, is a straight line whose slope is the residual. That beats the bin spacing by orders of
magnitude and does not depend on the window's shape.

## Running it

Needs Python with `numpy`. Nothing else.

1. Set the input and the output to the two cards you want compared (Sound card dialog, **Separate
   input and output devices**), and connect the output to the input with the cable you would
   measure through.
2. Turn **Record every acquisition to WAV** on — Configuration ▸ Diagnostics. That writes a
   `-out.wav` (the stimulus submitted to the output card), a `-in.wav` (what came back on the input
   card) and an `-info.json` per acquisition, into
   `%LOCALAPPDATA%\Tonebench\recordings`.
3. Play a single tone and let it run for 20 s or more. The longer the record, the finer the phase
   slope; 20 s already resolves well under a ppm.
4. Stop, then:

```bash
python testbench/clock-drift/measure_clock_drift.py "$LOCALAPPDATA/Tonebench/recordings"
```

with no second argument it takes the newest recording, prints the sidecar's own figures and a tone
frequency per channel per side. Then, for epsilon itself:

```bash
python testbench/clock-drift/epsilon.py "$LOCALAPPDATA/Tonebench/recordings" <stem> [frames] [Hz]
```

`<stem>` is the file name without the `-in.wav` / `-out.wav` suffix. `frames` trims the tail (see
the traps below) and `Hz` is the commanded tone, default 1000.

`peaks.py` takes one WAV and lists its largest spectral components. Use it first whenever a reading
looks wrong: it is what shows that the loudest thing in the capture is not the tone.

**If you are driving this from the Claude desktop app**, the app's own processes write into an
AppData *overlay* rather than the real folder, so read the recordings through
`//localhost/C$/Users/<you>/AppData/Local/Tonebench/recordings` instead.

## The two traps, both of which this got wrong first

**The phase reference has to run on the absolute sample index.** Restarted per segment, each
segment's phase advances by `2*pi*f*seg/rate` rather than by the residual `2*pi*(f - coarse)`,
which aliases and yields a slope with no relation to the error. It read a stimulus file that is
1000.000000 Hz *by construction* as 1000.638 Hz — 638 ppm out — and the resulting epsilon was
nonsense that looked plausible. **The stimulus WAV is the self-check**: it must come back at the
commanded frequency to within a ppm or two, and `epsilon.py` prints that check on its own line
above the answer. Do not read the answer without reading the check.

**A weak tone under hum fakes an unstable clock.** The tone is not necessarily the loudest thing in
the capture — on the bench this was written against a 60 Hz hum ladder was eight times its
amplitude — so the coarse search is restricted to a window around the commanded frequency rather
than taking the global maximum. And the recording ends with the generator silenced: segments of
silence contribute random phase that drags the straight-line fit, hence the `frames` trim. Skip
either and epsilon comes back as tens of ppm, wandering from window to window, which reads as a
badly behaved clock rather than as a measurement fault.

## What it measured, 2026-11-11

MOTU M4 as the output, Sound BlasterX G6 as the input, both on WASAPI at 48 kHz, 1 kHz tone:

| | |
|---|---|
| epsilon | **-104.37 ppm** |
| spread over five 4 s windows | 0.62 ppm |
| standard deviation | 0.23 ppm |
| both channels | identical |

So it is **large and stable**, not small and wandering. Over one 131072-sample analysis record at
48 kHz that is 285 us, or 13.7 samples of slip; against PortAudio's ~180 ms ring it is about
**29 minutes** to slip the ring's whole depth. The same figure came back at 192 kHz, which is what
settles the design: the drift is slow enough that two one-direction streams never need resampling
between them, and fast enough that a single duplex stream across two cards would be unusable.

Worth repeating on a QA403-plus-DAC pair, and over longer than a minute, before it is treated as a
property of all hardware rather than of this pair.

## What it does not answer

It cannot say *which* card is off, only the difference — separating them needs a reference the
analyzer does not have. And it says nothing about jitter: a stable epsilon is a statement about the
mean rate over the window, and close-in phase noise is a different measurement (see the QA403
re-clock note in CLAUDE.md for what that looks like when it bites).

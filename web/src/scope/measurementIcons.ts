/**
 * The little figure beside each measurement in the picker: a waveform with the quantity marked on
 * it.
 *
 * <p><b>Why a picture at all.</b> Thirty entries named <em>+Width</em>, <em>−Width</em>,
 * <em>+Duty</em>, <em>−Duty</em>, <em>Top</em>, <em>Base</em> and <em>Amplitude</em> are a list a
 * reader has to parse rather than scan, and the difference between any neighbouring pair of them is
 * one character. A figure showing where on a trace the reading is taken is read at a glance and
 * needs no English at all, which is why every bench instrument draws one.</p>
 *
 * <p>Shapes as data rather than one SVG per icon, and definitely not markup strings: thirty
 * hand-written SVGs drift, and a list of primitives is the only form of this a test can assert
 * anything about — that every catalogue entry has a figure, and that every figure stays inside the
 * box it is drawn in.</p>
 */

/** The box every figure is drawn in. Marks outside it would be clipped. */
export const ICON_WIDTH = 24
export const ICON_HEIGHT = 16

export type IconShape =
  /** A stroked path. <code>faint</code> is the backdrop trace; the rest is the mark on it. */
  | { t: 'path'; d: string; faint?: boolean; dash?: boolean }
  /** A straight mark. */
  | { t: 'line'; x1: number; y1: number; x2: number; y2: number; faint?: boolean; dash?: boolean }
  /** A filled block — what an area, a duty or a band is drawn as. */
  | { t: 'fill'; x: number; y: number; w: number; h: number }
  /** A double-headed measure, horizontal or vertical, with the ticks a dimension line carries. */
  | { t: 'spanH'; x1: number; x2: number; y: number }
  | { t: 'spanV'; y1: number; y2: number; x: number }

/** Two cycles of a sine, the backdrop most of the vertical readings are marked on. */
const WAVE = 'M1,8 C2.5,1.5 5.5,1.5 7,8 C8.5,14.5 11.5,14.5 13,8 C14.5,1.5 17.5,1.5 19,8 C20.5,14.5 22,14.2 23,12'

/** A pulse train, for everything measured between two settled levels. */
const PULSE = 'M1,13 H6 V3 H13 V13 H18 V3 H23'

/** One edge, for the edge timings. */
const RISE = 'M1,13 H9 L12,3 H23'
const FALL = 'M1,3 H9 L12,13 H23'

/** A pulse that overshoots its top and rings down to it. */
const OVERSHOOT = 'M1,13 H7 L8,1 L9,4 L10,2.5 H23'
const PRESHOOT = 'M1,3 H7 L8,15 L9,12 L10,13.5 H23'

/** The second trace of a pair figure, shifted along. */
const WAVE_LAGGED = 'M4,8 C5.5,2.5 8.5,2.5 10,8 C11.5,13.5 14.5,13.5 16,8 C17.5,2.5 20.5,2.5 22,8'
const WAVE_SMALL = 'M1,8 C2.5,5 5.5,5 7,8 C8.5,11 11.5,11 13,8 C14.5,5 17.5,5 19,8 C20.5,11 22,10.9 23,10'

const faintWave: IconShape = { t: 'path', d: WAVE, faint: true }
const faintPulse: IconShape = { t: 'path', d: PULSE, faint: true }

/**
 * Every figure, by the <code>icon</code> name its catalogue entry carries.
 *
 * <p>One entry per measurement rather than a glyph shared by a family, because the whole value of
 * the figure is telling two near-identical names apart — a shared one for <em>+Width</em> and
 * <em>−Width</em> would put the reader straight back to reading the label.</p>
 */
export const SCOPE_MEASUREMENT_ICONS: Readonly<Record<string, readonly IconShape[]>> = Object.freeze({
  // ---- Vertical ----
  max: [faintWave, { t: 'line', x1: 1, y1: 2, x2: 23, y2: 2, dash: true }],
  min: [faintWave, { t: 'line', x1: 1, y1: 14, x2: 23, y2: 14, dash: true }],
  pkpk: [
    faintWave,
    { t: 'line', x1: 1, y1: 2, x2: 20, y2: 2, dash: true },
    { t: 'line', x1: 1, y1: 14, x2: 20, y2: 14, dash: true },
    { t: 'spanV', y1: 2, y2: 14, x: 21.5 },
  ],
  top: [faintPulse, { t: 'line', x1: 1, y1: 3, x2: 23, y2: 3, dash: true }],
  base: [faintPulse, { t: 'line', x1: 1, y1: 13, x2: 23, y2: 13, dash: true }],
  amplitude: [
    faintPulse,
    { t: 'line', x1: 1, y1: 3, x2: 20, y2: 3, dash: true },
    { t: 'line', x1: 1, y1: 13, x2: 20, y2: 13, dash: true },
    { t: 'spanV', y1: 3, y2: 13, x: 21.5 },
  ],
  mean: [faintWave, { t: 'line', x1: 1, y1: 8, x2: 23, y2: 8 }],
  cycleMean: [
    faintWave,
    { t: 'line', x1: 1, y1: 8, x2: 13, y2: 8 },
    { t: 'spanH', x1: 1, x2: 13, y: 14 },
  ],
  rms: [faintWave, { t: 'fill', x: 1, y: 5.5, w: 22, h: 5 }],
  cycleRms: [faintWave, { t: 'fill', x: 1, y: 5.5, w: 12, h: 5 }, { t: 'spanH', x1: 1, x2: 13, y: 14 }],
  acRms: [
    faintWave,
    { t: 'line', x1: 1, y1: 8, x2: 23, y2: 8, dash: true },
    { t: 'fill', x: 1, y: 6, w: 22, h: 4 },
  ],
  cycleStdev: [
    faintWave,
    { t: 'line', x1: 1, y1: 8, x2: 13, y2: 8, dash: true },
    { t: 'fill', x: 1, y: 6, w: 12, h: 4 },
    { t: 'spanH', x1: 1, x2: 13, y: 14 },
  ],
  crest: [
    faintWave,
    { t: 'line', x1: 1, y1: 8, x2: 23, y2: 8, dash: true },
    { t: 'spanV', y1: 2, y2: 8, x: 7 },
  ],
  overshoot: [
    { t: 'path', d: OVERSHOOT, faint: true },
    { t: 'line', x1: 1, y1: 3, x2: 23, y2: 3, dash: true },
    { t: 'spanV', y1: 1, y2: 3, x: 14 },
  ],
  preshoot: [
    { t: 'path', d: PRESHOOT, faint: true },
    { t: 'line', x1: 1, y1: 13, x2: 23, y2: 13, dash: true },
    { t: 'spanV', y1: 13, y2: 15, x: 14 },
  ],

  // ---- Horizontal ----
  freq: [faintWave, { t: 'spanH', x1: 1, x2: 13, y: 14 }, { t: 'line', x1: 7, y1: 1, x2: 7, y2: 15, dash: true }],
  period: [
    faintWave,
    { t: 'line', x1: 1, y1: 1, x2: 1, y2: 15, dash: true },
    { t: 'line', x1: 13, y1: 1, x2: 13, y2: 15, dash: true },
    { t: 'spanH', x1: 1, x2: 13, y: 14 },
  ],
  posWidth: [faintPulse, { t: 'spanH', x1: 6, x2: 13, y: 2 }],
  negWidth: [faintPulse, { t: 'spanH', x1: 13, x2: 18, y: 14 }],
  posDuty: [faintPulse, { t: 'fill', x: 6, y: 3, w: 7, h: 10 }, { t: 'spanH', x1: 6, x2: 18, y: 14 }],
  negDuty: [faintPulse, { t: 'fill', x: 13, y: 3, w: 5, h: 10 }, { t: 'spanH', x1: 6, x2: 18, y: 14 }],
  rise: [
    { t: 'path', d: RISE, faint: true },
    { t: 'line', x1: 1, y1: 11.8, x2: 23, y2: 11.8, dash: true },
    { t: 'line', x1: 1, y1: 4.2, x2: 23, y2: 4.2, dash: true },
    { t: 'spanH', x1: 9.3, x2: 11.7, y: 8 },
  ],
  // The slope itself rather than the time it takes: the edge faint, with the rise and the run
  // drawn on it as the two legs of the triangle whose hypotenuse the edge is. That is the glyph
  // every textbook draws for a gradient, and it is what tells these two from `rise` and `fall`
  // beside them, which mark the horizontal span instead.
  riseSlew: [
    { t: 'path', d: RISE, faint: true },
    { t: 'line', x1: 9, y1: 13, x2: 12, y2: 13 },
    { t: 'line', x1: 12, y1: 13, x2: 12, y2: 3 },
  ],
  fallSlew: [
    { t: 'path', d: FALL, faint: true },
    { t: 'line', x1: 9, y1: 3, x2: 12, y2: 3 },
    { t: 'line', x1: 12, y1: 3, x2: 12, y2: 13 },
  ],
  fall: [
    { t: 'path', d: FALL, faint: true },
    { t: 'line', x1: 1, y1: 4.2, x2: 23, y2: 4.2, dash: true },
    { t: 'line', x1: 1, y1: 11.8, x2: 23, y2: 11.8, dash: true },
    { t: 'spanH', x1: 9.3, x2: 11.7, y: 8 },
  ],
  timeAtMax: [faintWave, { t: 'line', x1: 4, y1: 1, x2: 4, y2: 15 }, { t: 'spanH', x1: 1, x2: 4, y: 14 }],
  timeAtMin: [faintWave, { t: 'line', x1: 10, y1: 1, x2: 10, y2: 15 }, { t: 'spanH', x1: 1, x2: 10, y: 14 }],

  // ---- Miscellaneous ----
  area: [faintWave, { t: 'fill', x: 1, y: 8, w: 22, h: 6 }, { t: 'line', x1: 1, y1: 8, x2: 23, y2: 8, dash: true }],
  cycleArea: [
    faintWave,
    { t: 'fill', x: 1, y: 8, w: 12, h: 6 },
    { t: 'line', x1: 1, y1: 8, x2: 23, y2: 8, dash: true },
    { t: 'spanH', x1: 1, x2: 13, y: 14 },
  ],

  // ---- Channel delay ----
  phase: [faintWave, { t: 'path', d: WAVE_LAGGED }, { t: 'spanH', x1: 1, x2: 4, y: 14 }],
  skew: [faintWave, { t: 'path', d: WAVE_LAGGED }, { t: 'spanH', x1: 1, x2: 4, y: 2 }],
  correlation: [faintWave, { t: 'path', d: WAVE_SMALL }],
  ratio: [
    faintWave,
    { t: 'path', d: WAVE_SMALL },
    { t: 'spanV', y1: 5, y2: 8, x: 22 },
  ],
})

/** The figure for an icon name, or an empty list — a missing one draws nothing rather than throwing. */
export function scopeMeasurementIcon(name: string): readonly IconShape[] {
  return SCOPE_MEASUREMENT_ICONS[name] ?? []
}

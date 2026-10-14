// The round tinted icon beside every panel title (CollapsibleSection → PanelIcon.vue), from Joost's
// two mockups of 2026-10-12 (docs/design/). Each panel gets a 28 px circle in one of the ten kind
// accents already defined in style.css, with a 16 px line glyph inside it.
//
// **The mapping is by title, not by a prop on every call site.** CollapsibleSection is used from
// thirty-four components and the titles are already distinct — "Display", "Gain range (dB)",
// "History details" — so deriving the icon from the title gave every panel in the app the new
// chrome without thirty-four edits, and gave the redesign one table to review instead of thirty-four
// template diffs. A panel that wants something else passes `icon` and `tone` explicitly; that
// override is what a *new* panel should use rather than adding a title to the table below.
//
// Here rather than in the .vue for the usual reason: nothing in a `.vue` is testable under the node
// environment vitest runs in, and the one thing worth pinning is that every title the app actually
// renders resolves to something deliberate rather than to the fallback.
import type { MeasurementKindTone } from '../measurements/kinds'

/**
 * The glyphs. Lucide-flavoured line art at 24×24, drawn with `currentColor` so one path serves both
 * themes — the circle behind it carries the tint. `circles` is for the glyphs that need a filled
 * ring the path syntax would make unreadable (the eye's pupil, the stopwatch's bezel).
 */
export interface PanelGlyph {
  paths: string[]
  circles?: { cx: number; cy: number; r: number }[]
}

const GLYPHS = {
  /** Measurement settings: a decaying waveform — the thing every settings panel is about to make. */
  waveform: {
    paths: ['M2 12c1.5-7 3-7 4.5 0s2.5 6 3.5 0 2-5 3 0 1.5 4 2 0 1-3 1.5 0 .8 2 1.2 0 .5-1.5.8 0 .4 1 .6 0'],
  },
  /** A sweep: the staircase of a stepped stimulus climbing the band. */
  steps: { paths: ['M3 19h4v-4h4v-4h4V7h4V3'] },
  /** Readings: a ruler. */
  ruler: { paths: ['M3 8h18v8H3zM7 8v3M11 8v4M15 8v3M19 8v4'] },
  /** Display options: an eye. */
  eye: {
    paths: ['M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z'],
    circles: [{ cx: 12, cy: 12, r: 3 }],
  },
  /** A vertical axis range: arrows apart on a vertical rule. */
  rangeY: { paths: ['M12 3v18M8 7l4-4 4 4M8 17l4 4 4-4'] },
  /** A horizontal axis range: the same, lying down. */
  rangeX: { paths: ['M3 12h18M7 8l-4 4 4 4M17 8l4 4-4 4'] },
  /** A time range: a stopwatch. */
  stopwatch: { paths: ['M12 9v4l2.5 1.5M9 2h6'], circles: [{ cx: 12, cy: 13, r: 8 }] },
  /** History: a clock. */
  clock: { paths: ['M12 7v5l3 2'], circles: [{ cx: 12, cy: 12, r: 9 }] },
  /** Notes: ruled lines. */
  notes: { paths: ['M4 6h16M4 12h16M4 18h10'] },
  /** A list to tick: the history checklist, the overlay list. */
  checklist: { paths: ['M3 6l2 2 3-3M3 14l2 2 3-3M11 7h10M11 15h10'] },
  /** Harmonics and the other spectrum tables: a bar spectrum. */
  bars: { paths: ['M4 20v-7M8 20V5M12 20v-10M16 20V8M20 20v-4'] },
  /** The generator: a sine leaving a source. */
  sine: { paths: ['M2 12c2-6 4.5-6 6 0s3.5 6 6 0 4-6 6 0'] },
  /** A gate or window: two brackets around what is kept. */
  gate: { paths: ['M7 4H4v16h3M17 4h3v16h-3M10 9v6M14 9v6'] },
  /** A fixture: a reference resistor in line. */
  resistor: { paths: ['M2 12h3l1.5-5 3 10 3-10 1.5 5h8'] },
  /** Calibration: a crosshair on a target. */
  target: {
    paths: ['M12 2v3M12 19v3M2 12h3M19 12h3'],
    circles: [
      { cx: 12, cy: 12, r: 8 },
      { cx: 12, cy: 12, r: 3 },
    ],
  },
  /** The written-out transfer function: a fraction bar between two terms. */
  formula: { paths: ['M4 12h16M7 5h4M9 5v3M13 16h4M15 16v3'] },
  /** Poles and zeros on the plane: two crosses and a ring. */
  roots: { paths: ['M3 5l4 4M7 5l-4 4M3 15l4 4M7 15l-4 4'], circles: [{ cx: 17, cy: 10, r: 2.5 }] },
  /** Stability margins: a shield. */
  shield: { paths: ['M12 3l8 3.5v5c0 5-3.5 8.5-8 9.5-4.5-1-8-4.5-8-9.5v-5z'] },
  /** Anything with knobs behind it: FFT settings, Advanced, weighting. */
  sliders: { paths: ['M4 21v-6M4 11V3M12 21v-9M12 8V3M20 21v-4M20 13V3M1 15h6M9 8h6M17 17h6'] },
  /** Copy / export. */
  copy: { paths: ['M9 9h11v11H9zM5 15H4V4h11v1'] },
  /** Editing something: a pencil. */
  pencil: { paths: ['M4 20h4L19 9l-4-4L4 16z', 'M13 7l4 4'] },
  /** A folder — the project a dialog is about. */
  folder: { paths: ['M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'] },
  /** A document with ruled lines: a report. */
  document: { paths: ['M6 2h9l5 5v15H6zM14 2v6h6M9 13h8M9 17h6'] },
  /** A paperclip: what else goes in. */
  paperclip: { paths: ['M20 11.5 11.5 20a5 5 0 0 1-7-7l8-8a3.5 3.5 0 0 1 5 5l-8 8a2 2 0 0 1-3-3l7.5-7.5'] },
  /** A sound card: the interface itself, with its two jacks. */
  /** The About dialog: the header's own blue circled i. */
  info: { paths: ['M12 11v5M12 8h.01'], circles: [{ cx: 12, cy: 12, r: 9 }] },
  /** Report an issue: the header's own bug. */
  bug: { paths: ['M7 12a5 5 0 0 1 10 0v3a5 5 0 0 1-10 0zM9 7a3 3 0 0 1 6 0M3 13h4M17 13h4M4 7l3 2M20 7l-3 2M4 19l3-2M20 19l-3-2'] },
  /** The manual: the header's own book. */
  book: { paths: ['M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3z', 'M5 17a3 3 0 0 1 3-3h11'] },
  /**
   * Configuration: the app's own cog, not the mockup's eight-spoke drawing — which reads as a sun
   * at this size (Joost, 2026-10-12, about the header icon). The violet is the half of that drawing
   * that was the point.
   */
  cog: {
    paths: [
      'M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z',
    ],
    circles: [{ cx: 12, cy: 12, r: 3 }],
  },
  soundcard: { paths: ['M2 7h20v10H2z', 'M18 10v4'], circles: [{ cx: 7, cy: 12, r: 2 }, { cx: 13, cy: 12, r: 2 }] },
  /** A signal leaving: a wave with an arrow out of it. */
  signalOut: { paths: ['M3 12h4M17 12h4M19 9l3 3-3 3', 'M7 12c1.5-5 3-5 5 0s3.5 5 5 0'] },
  /** A signal arriving: the same wave with the arrow pointing in. */
  signalIn: { paths: ['M3 12h4M17 12h4M5 9l-3 3 3 3', 'M7 12c1.5-5 3-5 5 0s3.5 5 5 0'] },
  /** A measured component read off a curve: a capacitor-and-coil pair. */
  component: { paths: ['M3 12h4M7 7v10M11 7v10M11 12h2M13 9a3 3 0 0 1 0 6M16 9a3 3 0 0 1 0 6M19 12h2'] },
} satisfies Record<string, PanelGlyph>

/**
 * `satisfies` above keeps every key a literal (so `PanelIconName` is the union of the twenty-odd
 * names and a typo fails to compile), while this annotation widens the *values* back to PanelGlyph
 * — without it each entry's type is its own literal shape and `glyph.circles` is an error on the
 * ones that have no circles.
 */
export type PanelIconName = keyof typeof GLYPHS
export const PANEL_GLYPHS: Record<PanelIconName, PanelGlyph> = GLYPHS

export interface PanelIconSpec {
  icon: PanelIconName
  tone: MeasurementKindTone
}

/** What a title no rule below recognises gets: a neutral readings icon, never nothing. */
export const PANEL_ICON_FALLBACK: PanelIconSpec = { icon: 'ruler', tone: 'indigo' }

/**
 * Titles matched outright, before any of the patterns below. Lower-cased on both sides, so
 * "Display" and "display" are one entry.
 */
const BY_TITLE: Record<string, PanelIconSpec> = {
  'measurement settings': { icon: 'waveform', tone: 'orange' },
  sweep: { icon: 'steps', tone: 'orange' },
  'stepped sweep': { icon: 'steps', tone: 'orange' },
  'crosstalk sweep': { icon: 'steps', tone: 'orange' },
  'output impedance sweep': { icon: 'steps', tone: 'orange' },
  'fft settings': { icon: 'sliders', tone: 'orange' },
  generator: { icon: 'sine', tone: 'orange' },
  advanced: { icon: 'sliders', tone: 'amber' },
  'weighting': { icon: 'sliders', tone: 'cyan' },

  display: { icon: 'eye', tone: 'pink' },

  measurements: { icon: 'ruler', tone: 'indigo' },
  measurement: { icon: 'ruler', tone: 'indigo' },
  component: { icon: 'component', tone: 'indigo' },
  'equivalent circuit': { icon: 'component', tone: 'indigo' },
  reference: { icon: 'target', tone: 'indigo' },

  history: { icon: 'clock', tone: 'amber' },
  'history details': { icon: 'clock', tone: 'amber' },
  notes: { icon: 'notes', tone: 'green' },
  'copy as…': { icon: 'copy', tone: 'green' },

  gate: { icon: 'gate', tone: 'teal' },
  fixture: { icon: 'resistor', tone: 'teal' },
  calibration: { icon: 'target', tone: 'teal' },

  harmonics: { icon: 'bars', tone: 'blue' },
  'transfer function': { icon: 'formula', tone: 'indigo' },
  'poles and zeros': { icon: 'roots', tone: 'violet' },
  stability: { icon: 'shield', tone: 'red' },
}

/**
 * The patterns, tried in order once no exact title matched. They are what lets the dozen axis-range
 * panels — whose titles carry their unit, their channel or their x-axis name and so cannot be
 * listed — land on the right icon: a *frequency* range is horizontal, every other range is the
 * vertical axis, and a delay range is a clock face.
 */
const BY_PATTERN: { test: RegExp; spec: PanelIconSpec }[] = [
  { test: /\bdelay (range|axis)\b/, spec: { icon: 'stopwatch', tone: 'red' } },
  { test: /\bfrequency (range|axis)\b/, spec: { icon: 'rangeX', tone: 'cyan' } },
  { test: /^pole\/zero range/, spec: { icon: 'rangeX', tone: 'cyan' } },
  { test: /\b(range|axis)\b/, spec: { icon: 'rangeY', tone: 'violet' } },
  // "Left: Harmonics", "Right: Intermodulation", "Left: Other peaks", "Right: Multitone".
  { test: /\b(harmonics|intermodulation|multitone|peaks)\b/, spec: { icon: 'bars', tone: 'blue' } },
  { test: /\b(history|saved)\b/, spec: { icon: 'clock', tone: 'amber' } },
  { test: /\bsweep\b/, spec: { icon: 'steps', tone: 'orange' } },
]

/**
 * The icon and accent for a panel with this title. Pure, total, and deliberately forgiving — a
 * title it has never seen still gets a circle, because a panel with a gap where its neighbours have
 * an icon reads as a rendering fault rather than as a panel nobody has got to yet.
 */
export function panelIconFor(title: string): PanelIconSpec {
  const key = title.trim().toLowerCase()
  const exact = BY_TITLE[key]
  if (exact) return exact
  for (const { test, spec } of BY_PATTERN) if (test.test(key)) return spec
  return PANEL_ICON_FALLBACK
}

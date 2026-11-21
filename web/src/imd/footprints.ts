/**
 * The little spectrum beside each IMD preset: where its two tones sit, and where the products it
 * is read by land — Joost's design of 2026-11-20.
 *
 * <p><b>Why a picture of the FFT rather than a figure of the waveform.</b> What separates these
 * five is not what they look like in time — every one of them is two sine tones — but
 * <em>where in the band</em> they put their energy and their products. SMPTE and CCIF are the same
 * stimulus by shape and opposite ends of the spectrum by purpose, so a waveform icon would draw
 * them identically. On a log axis from 20 Hz to 40 kHz the difference is unmistakable at 90 px:
 * one tall bar at the far left with a cluster in the middle, against two bars jammed together at
 * the right with a lone product down at 1 kHz.</p>
 *
 * <p><b>The drawn product spacing is not to scale, and that is the one deliberate lie.</b> On this
 * axis 19 kHz and 20 kHz are under a pixel apart, and so are a sideband and its carrier — drawn
 * truly, every one of these would be a single smear. The bars are pushed apart by a fixed
 * <code>SPREAD</code> so the <em>shape</em> of each footprint reads: a carrier with sidebands
 * either side of it, or a close pair with products outside it and one far away. The frequencies
 * themselves are on the chips beside the picture, where they can be exact.</p>
 *
 * <p>Computed rather than drawn by hand, like <code>scope/mathFigures.ts</code>: the bar positions
 * are the real logarithmic positions of the real frequencies, so a preset cannot end up drawn in
 * the wrong part of the band — which is the whole of what the picture is for.</p>
 */
import { IMD_PRESETS, type ImdPreset, type ImdPresetId } from './presets'

/** The box every footprint is drawn in. */
export const FOOTPRINT_WIDTH = 80
export const FOOTPRINT_HEIGHT = 40

/** The band the x axis spans — the audio band with headroom either side, as the legend says. */
const F_LO = 20
const F_HI = 40000

/** Where the bars stand and how tall 0 dB is. */
const X0 = 3
const X1 = 78
const BASE = 36
const TOP = 4
/** The floor the bars are measured down to: −80 dB is the full height of the box. */
const FLOOR_DB = -80

/** How far apart bars are drawn when their true spacing is under a pixel. See the note above. */
const SPREAD = 2.2

function x(frequencyHz: number): number {
  const clamped = Math.min(F_HI, Math.max(F_LO, frequencyHz))
  return X0 + ((X1 - X0) * Math.log10(clamped / F_LO)) / Math.log10(F_HI / F_LO)
}

function y(db: number): number {
  const fraction = Math.min(1, Math.max(0, -db / -FLOOR_DB))
  return TOP + fraction * (BASE - TOP)
}

const round = (n: number) => (Math.round(n * 10) / 10).toString()

/** One bar, standing on the axis. */
function bar(atX: number, db: number): string {
  return `M${round(atX)} ${BASE} V${round(y(db))} `
}

/** One footprint, as the four paths it is stroked in. */
export interface ImdFootprint {
  /** The decade rules behind it — 100 Hz, 1 kHz, 10 kHz. */
  grid: string
  /** The first tone. */
  f1: string
  /** The second. */
  f2: string
  /** Every product the method reads, drawn shorter and in their own colour. */
  products: string
  /** True for Custom, whose two tones are whatever you type — so they are drawn dashed. */
  dashed: boolean
}

const GRID = [100, 1000, 10000].map((f) => `M${round(x(f))} 3 V${BASE}`).join(' ')

/**
 * A low tone modulating a high one: the sidebands sit at f2 ± n·f1 and are what the reading is
 * taken from, so they are drawn either side of the carrier and falling away in pairs.
 */
function modulation(lowHz: number, highHz: number): ImdFootprint {
  const carrier = x(highHz)
  return {
    grid: GRID,
    f1: bar(x(lowHz), 0),
    // 4:1 in amplitude is 12.04 dB, which is exactly where the high tone sits under the low one.
    f2: bar(carrier, -12),
    products:
      bar(carrier - SPREAD, -42) +
      bar(carrier + SPREAD, -42) +
      bar(carrier - 2 * SPREAD, -54) +
      bar(carrier + 2 * SPREAD, -54),
    dashed: false,
  }
}

/**
 * Two equal tones close together: the second-order product lands at their difference, far down the
 * band, and the third-order pair just outside them — which is the shape that tells this family
 * from the other at a glance.
 */
function twinTone(lowHz: number, highHz: number): ImdFootprint {
  const centre = x((lowHz + highHz) / 2)
  return {
    grid: GRID,
    f1: bar(centre - SPREAD, -6),
    f2: bar(centre + SPREAD, -6),
    products: bar(x(highHz - lowHz), -52) + bar(centre - 3 * SPREAD, -46) + bar(centre + 3 * SPREAD, -46),
    dashed: false,
  }
}

/** Custom: two tones wherever you put them, so they are drawn dashed and carry no products. */
const CUSTOM: ImdFootprint = {
  grid: GRID,
  f1: bar(x(400), -2),
  f2: bar(x(5000), -14),
  products: '',
  dashed: true,
}

/**
 * Every preset's footprint, by its own id.
 *
 * <p>Built from <code>IMD_PRESETS</code> rather than written out, so a preset cannot have a picture
 * of frequencies it does not play — the thing a hand-drawn set would get wrong first.</p>
 */
export const IMD_FOOTPRINTS: Readonly<Record<string, ImdFootprint>> = Object.freeze(
  Object.fromEntries(
    IMD_PRESETS.map((preset: ImdPreset) => [
      preset.value,
      preset.value === 'Custom'
        ? CUSTOM
        : preset.method === 'Ccif'
          ? twinTone(preset.lowHz, preset.highHz)
          : modulation(preset.lowHz, preset.highHz),
    ]),
  ),
)

/** One preset's footprint, or null for an id there is none for. */
export function imdFootprint(id: ImdPresetId | string): ImdFootprint | null {
  return IMD_FOOTPRINTS[id] ?? null
}

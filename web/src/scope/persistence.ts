/**
 * The oscilloscope's persistence — the digital-phosphor picture, where how <em>often</em> the trace
 * passes through a pixel decides how it is painted.
 *
 * <p>A stable part of the waveform burns in bright; jitter, noise and a once-in-a-while glitch show
 * as a faint haze around it. That is the one display here that answers a question the ordinary
 * trace cannot: a glitch one record in fifty is invisible on a chart that redraws each record over
 * the last, and unmistakable on one that keeps them.</p>
 *
 * <p><b>What is accumulated is across records, not within one.</b> Each committed record adds one
 * to every pixel its trace passes through, so the quantity on screen is <em>how many of the last N
 * records went through here</em> — the dimension that shows jitter and intermittency. The other
 * reading, how long the waveform dwells at each height <em>inside</em> one record, needs more than
 * the min/max per column the wire carries and is not built; the two are different measurements and
 * this one is the one a scope is pointed at a fault with.</p>
 *
 * <p>The arithmetic is here rather than in the component for the usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because the rule
 * that matters most is invisible by eye: an accumulator is in <em>pixels</em>, so it has to be
 * thrown away the moment a pixel stops meaning the same volts and the same time.</p>
 */

import { paletteIndex, paletteLut } from '../measurements/decay'
import type { DecayPalette } from '../api/client'

/** How long the picture keeps a record, as the display rail offers it. */
export type ScopePersistence = 'Off' | 'Short' | 'Medium' | 'Long' | 'Infinite'

/**
 * How many records each setting keeps.
 *
 * <p>A <b>record count</b> rather than a time, which is where this differs from a bench
 * instrument's seconds: records arrive at a rate set by the record length, the trigger and the DUT,
 * so "two seconds" is anywhere between one record and two hundred. What a reader is actually asking
 * for is "keep the last N captures", and the labels say so.</p>
 */
export const SCOPE_PERSISTENCE_RECORDS: Readonly<Record<ScopePersistence, number | null>> =
  Object.freeze({
    Off: 0,
    Short: 8,
    Medium: 32,
    Long: 128,
    /** Null, not a large number: nothing decays at all, which is what hunting an intermittent wants. */
    Infinite: null,
  })

/** The dropdown, in the order a reader turns it up. */
export const SCOPE_PERSISTENCE_OPTIONS: readonly { value: ScopePersistence; label: string }[] =
  Object.freeze([
    { value: 'Off', label: 'Off' },
    { value: 'Short', label: '8 records' },
    { value: 'Medium', label: '32 records' },
    { value: 'Long', label: '128 records' },
    { value: 'Infinite', label: 'Infinite' },
  ])

/** How the accumulated picture is coloured. */
export type ScopeIntensityMode = 'Single' | 'Multi'

export const SCOPE_INTENSITY_MODES: readonly { value: ScopeIntensityMode; label: string }[] =
  Object.freeze([
    { value: 'Single', label: "Channel's colour" },
    { value: 'Multi', label: 'Colour map' },
  ])

/**
 * What a record's hits are multiplied by before the next record is added.
 *
 * <p>Chosen so a pixel hit once is down to <b>one step of a 256-level ramp</b> after N records —
 * i.e. gone, rather than merely faint. A half-life or a 1/e time would leave a third of the
 * brightness at N and make the setting read as about three times longer than it says.</p>
 */
export function persistenceDecay(persistence: ScopePersistence): number {
  const records = SCOPE_PERSISTENCE_RECORDS[persistence]
  if (records === null) return 1
  if (!records || records <= 0) return 0
  return Math.pow(1 / 255, 1 / records)
}

/** Whether the accumulated picture is drawn at all. */
export function persistenceDraws(persistence: ScopePersistence): boolean {
  return persistence !== 'Off'
}

/**
 * How opaque a pixel of a given normalised intensity is painted.
 *
 * <p>Zero hits is <b>transparent</b>, never the palette's own floor colour: a wash of dark blue
 * over every pixel the trace has never been near would be a second, false trace. Above that the
 * alpha climbs steeply and is full by <code>ALPHA_KNEE</code>, so the faint end fades in rather
 * than appearing all at once, and everything above the knee is left to the colour to say.</p>
 */
export const ALPHA_KNEE = 0.2

/**
 * The gamma the single-colour ramp is read through.
 *
 * <p>Linear alpha makes everything but the newest trace invisible the moment one record dominates —
 * which, on a stable signal under long persistence, is every record. A square root is the usual
 * answer and is what makes the haze visible at all.</p>
 */
export const INTENSITY_GAMMA = 0.5

export function intensityAlpha(intensity: number, mode: ScopeIntensityMode): number {
  if (!(intensity > 0)) return 0
  const clamped = intensity > 1 ? 1 : intensity
  if (mode === 'Multi') return Math.min(1, clamped / ALPHA_KNEE)
  return Math.pow(clamped, INTENSITY_GAMMA)
}

/**
 * The palette index an intensity maps to.
 *
 * <p><b>Floor zero, unlike every decay surface here.</b> Those lift the mapping off the palette's
 * own foot because a near-black floor makes the quiet half of a <em>level</em> scale unreadable; on
 * an intensity map the faint end is carried by the alpha instead, and lifting the colour as well
 * would make a pixel touched once read as warm as one touched fifty times.</p>
 */
export function intensityPaletteIndex(intensity: number): number {
  return paletteIndex(intensity, 0)
}

/**
 * A pixel buffer of hit counts, in <b>CSS pixels of the plot area</b>.
 *
 * <p>CSS rather than device pixels: the picture is scaled up on the way out, which costs nothing
 * visible on a haze and quarters the work on a hidpi screen — a 700 x 600 plot is 420 000 cells to
 * decay per record rather than 1.68 million.</p>
 */
export class ScopeIntensityBuffer {
  readonly width: number
  readonly height: number
  readonly data: Float32Array

  private max = 0

  constructor(width: number, height: number) {
    this.width = Math.max(1, Math.floor(width))
    this.height = Math.max(1, Math.floor(height))
    this.data = new Float32Array(this.width * this.height)
  }

  /** The brightest cell, which is what every intensity is read against. */
  get peak(): number {
    return this.max
  }

  clear(): void {
    this.data.fill(0)
    this.max = 0
  }

  /**
   * Fades everything already there. A factor of 1 keeps it (infinite persistence) and a factor of 0
   * is a clear, which is what <code>Off</code> would be if it ever reached here.
   */
  decay(factor: number): void {
    if (factor >= 1) return
    if (factor <= 0) {
      this.clear()
      return
    }
    const data = this.data
    for (let i = 0; i < data.length; i++) data[i] *= factor
    this.max *= factor
  }

  /**
   * Adds one record's vertical span at one column — the one primitive both kinds of slice need.
   *
   * <p>A <b>decimated</b> column is a span by construction (its min and its max), and a <b>raw</b>
   * trace between two adjacent samples is the span between their two heights, since the columns are
   * walked one pixel at a time either way. So a fast edge lights every pixel it crosses, exactly
   * once per record, which is what makes an edge that jitters read as a wedge rather than a line.</p>
   */
  addSpan(x: number, yA: number, yB: number): void {
    const col = Math.round(x)
    if (col < 0 || col >= this.width) return

    let top = Math.round(Math.min(yA, yB))
    let bottom = Math.round(Math.max(yA, yB))
    if (bottom < 0 || top >= this.height) return
    if (top < 0) top = 0
    if (bottom > this.height - 1) bottom = this.height - 1

    const data = this.data
    for (let y = top; y <= bottom; y++) {
      const value = (data[y * this.width + col] += 1)
      if (value > this.max) this.max = value
    }
  }

  /**
   * Adds one record's segment between two arbitrary points — what the X–Y figure is made of.
   *
   * <p><b>A line and not a point, which is the whole difference between a goniometer and a cloud
   * of dots.</b> A Lissajous figure is traced out in order, and at any sample rate worth using the
   * consecutive pairs are many pixels apart wherever the locus is moving quickly — so plotting the
   * pairs alone draws a dashed figure whose gaps are exactly where the signal is fastest, which
   * reads as a dropout rather than as the sampling it is.</p>
   *
   * <p>Bresenham rather than a stroked path, because a cell is a count and a canvas stroke is
   * coverage: anti-aliasing would spread one pass over two cells and make a steady figure read as
   * two faint ones. <b>Each cell is counted at most once per call</b>, so a slow stretch of the
   * locus does not light up more brightly than a fast one within the same record — across-record
   * frequency is what this buffer measures, and dwell within a record is a different quantity that
   * would quietly mix into it.</p>
   */
  addLine(x0: number, y0: number, x1: number, y1: number): void {
    if (!Number.isFinite(x0) || !Number.isFinite(y0) || !Number.isFinite(x1) || !Number.isFinite(y1)) return

    // Clipped to the buffer before it is walked, rather than walked and tested per cell. A
    // Lissajous drawn at a V/div far too sensitive runs a long way off screen, and stepping that
    // whole distance one cell at a time to write nothing is work proportional to the signal rather
    // than to the picture. Clipping first makes the cost the plot's size, whatever arrives.
    const clipped = clipSegment(x0, y0, x1, y1, this.width - 1, this.height - 1)
    if (!clipped) return

    let x = Math.round(clipped.x0)
    let y = Math.round(clipped.y0)
    const ex = Math.round(clipped.x1)
    const ey = Math.round(clipped.y1)

    const dx = Math.abs(ex - x)
    const dy = -Math.abs(ey - y)
    const sx = x < ex ? 1 : -1
    const sy = y < ey ? 1 : -1
    let error = dx + dy

    for (;;) {
      this.hit(x, y)
      if (x === ex && y === ey) return
      const twice = 2 * error
      if (twice >= dy) {
        error += dy
        x += sx
      }
      if (twice <= dx) {
        error += dx
        y += sy
      }
    }
  }

  /** One cell, counted once. Out of bounds is dropped rather than clamped — a clamped hit would pile the whole off-screen locus onto the edge. */
  private hit(x: number, y: number): void {
    if (x < 0 || x >= this.width || y < 0 || y >= this.height) return
    const value = (this.data[y * this.width + x] += 1)
    if (value > this.max) this.max = value
  }

  /**
   * Keeps the counts from growing without bound under infinite persistence.
   *
   * <p>Only the ratios are ever read, so scaling the whole buffer changes nothing on screen — but a
   * `Float32Array` left to count for a day would lose its low end to rounding. Called after every
   * record; it does nothing until the peak is large.</p>
   */
  renormalise(ceiling = 1e6): void {
    if (this.max <= ceiling) return
    this.decay(1 / this.max)
  }
}

/**
 * Liang-Barsky: the part of a segment that falls inside `0..maxX` by `0..maxY`, or null.
 *
 * <p>Here rather than inside the buffer because it is pure arithmetic with four easy sign mistakes
 * in it, and because the buffer's own job is counting.</p>
 */
function clipSegment(
  x0: number,
  y0: number,
  x1: number,
  y1: number,
  maxX: number,
  maxY: number,
): { x0: number; y0: number; x1: number; y1: number } | null {
  const dx = x1 - x0
  const dy = y1 - y0
  let enter = 0
  let leave = 1

  // Each edge as p*t <= q: a zero p is a segment parallel to it, which is outside only when q < 0.
  const edges: [number, number][] = [
    [-dx, x0 - 0],
    [dx, maxX - x0],
    [-dy, y0 - 0],
    [dy, maxY - y0],
  ]

  for (const [p, q] of edges) {
    if (p === 0) {
      if (q < 0) return null
      continue
    }
    const t = q / p
    if (p < 0) {
      if (t > leave) return null
      if (t > enter) enter = t
    } else {
      if (t < enter) return null
      if (t < leave) leave = t
    }
  }

  return {
    x0: x0 + enter * dx,
    y0: y0 + enter * dy,
    x1: x0 + leave * dx,
    y1: y0 + leave * dy,
  }
}

/**
 * Everything that has to be the same for an accumulated picture to still mean anything.
 *
 * <p><b>This is the rule the whole feature turns on.</b> A cell is a pixel, and a pixel stands for
 * a time and a level only as long as the plot's size and both of its windows are unchanged — so a
 * zoom, a V/div step, a resize or a measurement switch makes every count already in the buffer a
 * claim about a different part of the signal. Comparing a signature before each record is what
 * makes that structural: no call site can forget to clear, because nothing clears by hand.</p>
 */
export function intensitySignature(
  width: number,
  height: number,
  xMin: number,
  xMax: number,
  yMin: number,
  yMax: number,
  extra = '',
): string {
  return `${Math.round(width)}x${Math.round(height)}:${xMin}:${xMax}:${yMin}:${yMax}:${extra}`
}

/**
 * The accumulated picture as RGBA bytes, ready for an <code>ImageData</code>.
 *
 * @param rgb the channel's own colour, for <code>Single</code>; ignored under <code>Multi</code>
 */
export function intensityPixels(
  buffer: ScopeIntensityBuffer,
  mode: ScopeIntensityMode,
  palette: DecayPalette,
  rgb: readonly [number, number, number],
  out?: Uint8ClampedArray<ArrayBuffer>,
): Uint8ClampedArray<ArrayBuffer> {
  const pixels = out ?? new Uint8ClampedArray(buffer.width * buffer.height * 4)
  const peak = buffer.peak
  if (!(peak > 0)) {
    pixels.fill(0)
    return pixels
  }

  const lut = mode === 'Multi' ? paletteLut(palette) : null
  const data = buffer.data
  for (let i = 0; i < data.length; i++) {
    const intensity = data[i] / peak
    const alpha = intensityAlpha(intensity, mode)
    const o = i * 4
    if (alpha <= 0) {
      pixels[o] = 0
      pixels[o + 1] = 0
      pixels[o + 2] = 0
      pixels[o + 3] = 0
      continue
    }
    if (lut) {
      const index = intensityPaletteIndex(intensity) * 3
      pixels[o] = lut[index]
      pixels[o + 1] = lut[index + 1]
      pixels[o + 2] = lut[index + 2]
    } else {
      pixels[o] = rgb[0]
      pixels[o + 1] = rgb[1]
      pixels[o + 2] = rgb[2]
    }
    pixels[o + 3] = Math.round(alpha * 255)
  }
  return pixels
}

/** A `#rrggbb` as the three bytes the painter wants. */
export function hexRgb(hex: string): [number, number, number] {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim())
  if (!m) return [255, 255, 255]
  const v = parseInt(m[1], 16)
  return [(v >> 16) & 255, (v >> 8) & 255, v & 255]
}

/**
 * What the analyzer is capturing with, as one string — so the accumulated picture can be thrown
 * away the moment the records stop being comparable.
 *
 * <p><b>The signature above this one covers the picture; this one covers the capture.</b>
 * <code>intensitySignature</code> is in pixels, so it already clears on a resize, a zoom or a V/div
 * step — anything that stops a pixel meaning the same volts and the same time. What it cannot see
 * is a change to the <em>record</em>: a sample rate, a record length, a pre-trigger fraction, a
 * trigger level or a switch to Envelope all leave every axis exactly where it was while making the
 * traces already accumulated a picture of something else. A haze of 128 records taken at 48 kS/s
 * under a live trace at 192 is the plausible wrong answer this screen's rules exist to prevent, and
 * nothing on it would say so.</p>
 *
 * <p>Built from the <b>request</b> rather than from the panel's own refs, and that is the whole
 * reason it is here: Run and every retune carry the complete capture configuration, including the
 * half the config panel keeps to itself — the slope, the holdoff, the width bounds, the dropout
 * timeout, which inputs are captured. A signature assembled from the models App.vue happens to hold
 * would silently miss those, which is exactly the kind of gap that is noticed years later.</p>
 *
 * <p>The generator is deliberately <b>not</b> in it. A stimulus change does change the trace, and it
 * is also the one thing a reader turns while <em>watching</em> the haze — sweeping a level to see
 * where a DUT starts clipping is a reading of the accumulation, not a reason to throw it away. The
 * stimulus is edited through <code>/api/generator</code> rather than a retune in any case, so it
 * never reaches this request while a capture is running.</p>
 */
export function scopeCaptureSignature(request: ScopeCaptureFacts | null | undefined): string {
  if (!request) return ''
  const r = request
  // Spelled out rather than JSON.stringify(request): a field added to the request later should be
  // considered rather than folded in silently, and the generator blocks must stay out.
  return [
    r.sampleRateHz,
    r.recordSeconds,
    r.preTriggerFraction,
    r.mode,
    r.triggerKind,
    r.triggerSource,
    r.triggerSlope,
    r.triggerLevelV,
    r.triggerHysteresisV ?? 0,
    r.triggerLevelLowV ?? 0,
    r.pulseWidthLoSeconds ?? 0,
    r.pulseWidthHiSeconds ?? 0,
    r.dropoutSeconds ?? 0,
    r.holdoffSeconds ?? 0,
    r.accumulation ?? 'Normal',
    r.averageCount ?? 0,
    r.processLeft !== false,
    r.processRight === true,
  ].join('|')
}

/** The half of an <code>OscilloscopeStartRequest</code> that decides what a record is. */
export type ScopeCaptureFacts = Pick<
  import('../api/client').OscilloscopeStartRequest,
  | 'sampleRateHz'
  | 'recordSeconds'
  | 'preTriggerFraction'
  | 'mode'
  | 'triggerKind'
  | 'triggerSource'
  | 'triggerSlope'
  | 'triggerLevelV'
  | 'triggerHysteresisV'
  | 'triggerLevelLowV'
  | 'pulseWidthLoSeconds'
  | 'pulseWidthHiSeconds'
  | 'dropoutSeconds'
  | 'holdoffSeconds'
  | 'accumulation'
  | 'averageCount'
  | 'processLeft'
  | 'processRight'
>

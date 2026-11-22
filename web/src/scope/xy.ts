import type { ScopeXySlice } from '../api/client'
import type { ScopeReading } from './readings'
import { formatSeconds } from './readings'
import { formatVolts } from './voltAxis'

/**
 * The oscilloscope's X–Y view — one channel against the other, with time thrown away. A
 * goniometer, a vectorscope, a Lissajous figure; the same picture under three names.
 *
 * <p><b>What the picture is for.</b> Two channels of one signal become a shape rather than two
 * traces: a straight 45° line is a matched pair in phase, a circle is 90° apart, an ellipse is
 * everything between, and a line at any other angle is a level mismatch. It is the fastest way to
 * see channel balance, a swapped or inverted lead, a crossover's transfer curve or a magnetic
 * hysteresis loop — all of which read as "two traces that look similar" on a time axis.</p>
 *
 * <p>The figure itself and its three readings are computed on the analyzer, over the samples; this
 * module holds the window arithmetic and the wording, which is the half nothing in a
 * <code>.vue</code> could be tested on.</p>
 */

/** Which picture the scope's chart is drawing. */
export type ScopeView = 'Waveform' | 'XY' | 'Eye'

/**
 * Each carries a figure as well as a label — Joost's drawings of 2026-11-22, by name from
 * <code>scope/displayIcons.ts</code>.
 *
 * <p>Three labels of three characters each are a row a reader parses rather than scans, and the
 * pictures say what the words cannot: a trace against time, a figure against another channel, every
 * cycle drawn over the others. The figures are one ink, so the lit option's own colour and the
 * track's grey are what say which is chosen — see the figures' own note.</p>
 */
export const SCOPE_VIEWS: readonly { value: ScopeView; label: string; title: string; icon: string }[] =
  Object.freeze([
  {
    value: 'Waveform',
    label: 'Y–T',
    icon: 'viewWaveform',
    title: 'The waveforms against time — an oscilloscope’s ordinary picture, with the trigger at zero.',
  },
  {
    value: 'XY',
    label: 'X–Y',
    icon: 'viewXy',
    title:
      'The left input against the right, with time thrown away — a goniometer. A 45° line is a matched pair in phase, a circle is 90° apart, and an ellipse is everything between.',
  },
  {
    value: 'Eye',
    label: 'Eye',
    icon: 'viewEye',
    title:
      'Every cycle of the window folded on the signal’s own period and drawn over the others. A signal that repeats exactly folds into one line; anything that moves it cycle to cycle spreads that line into a band.',
  },
])

export function scopeViewIsXy(view: ScopeView): boolean {
  return view === 'XY'
}

/**
 * The square window the figure is drawn in, from the display rail's own V/div.
 *
 * <p><b>Square, and from the same V/div on both axes</b>, which is not a nicety: the whole content
 * of the picture is its <em>shape</em>, and the reading everyone takes first is whether the line
 * sits at 45°. Different scales on the two axes tilt that line by an amount no axis label reveals,
 * so a matched pair would read as a mismatched one. Eight divisions, as the waveform view's
 * vertical is — a figure and a trace of the same signal then fill the screen the same way.</p>
 */
export const XY_DIVISIONS = 8

export function xyWindow(voltsPerDivision: number): { min: number; max: number } {
  const half = Math.abs(voltsPerDivision) * (XY_DIVISIONS / 2) || 1
  return { min: -half, max: half }
}

/**
 * The square part of the canvas the figure is drawn in.
 *
 * <p><b>Square by construction rather than by letterboxing a viewport</b>, and that is not the
 * same thing. `fitTransform`'s equal-aspect mode keeps the two scales equal by <i>widening</i> the
 * window on the roomier axis, which leaves the plot box the shape the canvas happens to be — and
 * then an eight-by-eight graticule over it has oblong cells, and a line drawn corner to corner is
 * not the world line y = x. On this chart both of those are readings: the graticule is counted in
 * divisions that must mean the same on both axes, and the diagonal <em>is</em> the 45° guide a
 * matched pair is compared against. So the box is made square first and the window is then exact
 * inside it.</p>
 *
 * <p>Centred horizontally in what the labels leave, because the vertical is the constrained axis on
 * every chart column this app has.</p>
 */
export function xyPlotBox(
  width: number,
  height: number,
  insets: { left: number; right: number; top: number; bottom: number },
): { left: number; top: number; size: number } {
  const availableWidth = Math.max(1, width - insets.left - insets.right)
  const availableHeight = Math.max(1, height - insets.top - insets.bottom)
  const size = Math.max(1, Math.floor(Math.min(availableWidth, availableHeight)))
  return {
    left: insets.left + Math.floor((availableWidth - size) / 2),
    top: insets.top,
    size,
  }
}

/**
 * What a correlation reading means, in the words a stereo meter uses.
 *
 * <p>A descriptor rather than a verdict: none of these is a fault. <b>−1 is the one worth
 * recognising</b> — two channels that cancel, which is a lead swapped somewhere and is inaudible on
 * either channel alone — which is why it is the only band whose wording names a cause.</p>
 */
export function correlationDescription(r: number): string {
  if (!Number.isFinite(r)) return ''
  if (r >= 0.95) return 'the same signal on both'
  if (r >= 0.5) return 'mostly in phase'
  if (r > -0.3) return 'largely unrelated, or a quarter period apart'
  if (r > -0.9) return 'mostly opposed'
  return 'opposed — one lead is inverted, or the pair is wired out of phase'
}

/** A correlation as the panel writes it: two places, and a sign that is never dropped. */
export function formatCorrelation(r: number): string {
  if (!Number.isFinite(r)) return '—'
  const clamped = Math.min(1, Math.max(-1, r))
  return (clamped >= 0 ? '+' : '−') + Math.abs(clamped).toFixed(3)
}

/** A signed phase in degrees, written the way the Bode screens write one. */
export function formatPhaseDeg(deg: number): string {
  if (!Number.isFinite(deg)) return '—'
  return `${deg >= 0 ? '+' : '−'}${Math.abs(deg).toFixed(1)}°`
}

/**
 * The amplitude ratio, written <b>both ways</b>.
 *
 * <p>As a ratio and as decibels, because the two answer different questions: 0.707 says "the right
 * one is about seven tenths", −3.0 dB says "the right one is 3 dB down", and which of those a
 * reader wants depends on whether they are checking a divider or a gain. One row, both figures,
 * rather than a setting nobody would find.</p>
 */
export function formatAmplitudeRatio(ratio: number): string {
  if (!Number.isFinite(ratio) || ratio <= 0) return '—'
  const db = 20 * Math.log10(ratio)
  return `${ratio.toFixed(3)} · ${db >= 0 ? '+' : '−'}${Math.abs(db).toFixed(2)} dB`
}

/**
 * The figure's own readings, as the panel and the report both print them.
 *
 * <p>The correlation is always there; the other two are left out when the analyzer refused them,
 * which is the rule the whole screen follows. A phase is absent whenever no single number is this
 * figure's phase — two different tones, noise, a silent channel — and quoting one there would be a
 * number about nothing.</p>
 */
export function xyReadings(xy: ScopeXySlice | null | undefined): ScopeReading[] {
  if (!xy) return []
  const rows: ScopeReading[] = []

  rows.push({
    label: 'Correlation',
    value: formatCorrelation(xy.correlation),
    title:
      'Pearson’s r over the pairs drawn, about each channel’s own mean: +1 when the two move together, 0 when they are unrelated or a quarter period apart, −1 when they are mirror images. The stereo meter’s own reading.',
  })

  if (xy.phaseDeg !== null && Number.isFinite(xy.phaseDeg)) {
    rows.push({
      label: 'Phase',
      value: formatPhaseDeg(xy.phaseDeg),
      title:
        'How far the right input lags the left, signed — from each channel’s own transform at the common frequency, so a lead and a lag of the same size are told apart. Quoted only when both channels carry one common tone.',
    })
  }

  if (xy.amplitudeRatio !== null && Number.isFinite(xy.amplitudeRatio)) {
    rows.push({
      label: 'Right / left',
      value: formatAmplitudeRatio(xy.amplitudeRatio),
      title: 'The right input’s AC RMS over the left’s — 1.000 and 0 dB for a balanced pair.',
    })
  }

  rows.push({
    label: 'Drawn from',
    value: `${xy.frameCount.toLocaleString()} pairs · ${formatSeconds(xy.spanS)}`,
    title:
      'How many pairs of samples the figure is drawn from, and what they cover. Pairs rather than columns: a min/max per column throws away which value of the left input went with which of the right, which is the whole of what this picture is.',
  })

  return rows
}

/**
 * The note shown when the window held more pairs than the figure could carry.
 *
 * <p>Said rather than hidden, and it is also why the figure is a <em>contiguous</em> run instead of
 * every n-th pair: a stride near a whole number of periods collapses an ellipse onto a line, and a
 * reader would conclude the channels are in phase. A truncated figure is a true figure of a
 * shorter stretch.</p>
 */
export function xyTruncationNote(xy: ScopeXySlice | null | undefined): string | null {
  if (!xy || xy.frameCount >= xy.windowFrameCount) return null
  return `The first ${formatSeconds(xy.spanS)} of the window — ${xy.frameCount.toLocaleString()} of its ${xy.windowFrameCount.toLocaleString()} sample pairs. Narrow the window to move it.`
}

/** The hover readout: where the pointer is, in both channels' volts. */
export function xyCursorText(xVolts: number, yVolts: number, voltsPerDivision: number): string {
  return `Left ${formatVolts(xVolts, voltsPerDivision)} · right ${formatVolts(yVolts, voltsPerDivision)}`
}

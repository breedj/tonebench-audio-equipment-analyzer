<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { ResponsePhaseKindName } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { sweepOverlayDash } from '../measurements/sweepOverlayStyles'
import { RESULT_CHART_HEIGHT_PX } from '../charts/dimensions'
import { freqAxis } from '../freqAxis'
import { formatHz } from '../response/format'
import { useAppTheme } from '../composables/useAppTheme'

const props = defineProps<{
  traces: ResponseTrace[]
  /** Stored measurements checked on in the History list, drawn dashed behind the live curves. */
  overlays: OverlayEntry[]
  /** 'Dbv' shows each input's own level; 'Gain' shows left relative to the reference. */
  view: 'Dbv' | 'Gain'
  showLeft: boolean
  showRight: boolean
  showPhase: boolean
  showCoherence: boolean
  showOneDb: boolean
  showThreeDb: boolean
  thickLines: boolean
  showDataPoints: boolean
  /** The Gain (dB) view's own y window — independent of yTopDbv/yBottomDbv below so switching views never clobbers the other one's range. */
  yTopDb: number
  yBottomDb: number
  /** The FR (dBV) view's own y window. */
  yTopDbv: number
  yBottomDbv: number
  xFromHz: number
  xToHz: number
  /** The phase axis' own window, in degrees — set in the display panel, independent of the dB one because unwrapped phase can run past ±180°. */
  phaseTopDeg: number
  phaseBottomDeg: number
  /** What phase this run measured — decides which curve carries it and what it is called. See ResponsePhaseKind. */
  phaseKind: ResponsePhaseKindName
  /** The button that starts this screen's measurement — RUN on the chirp, RUN SWEEP on the stepped sine. */
  runLabel?: string
}>()

const { isDark } = useAppTheme()

const container = ref<HTMLDivElement | null>(null)
const plot = shallowRef<uPlot | null>(null)

/**
 * Matches the sweep chart exactly — one constant, see charts/dimensions.ts. The plot is sized from
 * the container's own height, so this is set as an inline style rather than baked into the uPlot
 * options: the ResizeObserver below then keeps reading one number, whether the change came from
 * here or from the window.
 */
const CHART_HEIGHT_PX = RESULT_CHART_HEIGHT_PX

/**
 * Hue is the quantity, matching the sweep chart's convention: the gain curve is the one the
 * bandwidth markers refer to and gets the neutral colour, the two input curves take the same
 * blue/orange the rest of the app uses for left and right.
 */
const TRACE_COLORS = {
  Gain: ['#111827', '#f3f4f6'],
  Left: ['#2563eb', '#60a5fa'],
  Right: ['#ea580c', '#fb923c'],
} as const

const PHASE_COLOR = ['#7c3aed', '#a78bfa'] as const
const COHERENCE_COLOR = ['#059669', '#34d399'] as const

/** The dashed verticals and the double-headed arrow the ±1 dB / −3 dB readings are drawn with. */
const BANDWIDTH_COLORS = {
  oneDb: ['#d97706', '#fbbf24'],
  threeDb: ['#2563eb', '#60a5fa'],
} as const

/** The horizontal line at the level both readings were measured down from. Deliberately quiet — it is context, not a curve. */
const REFERENCE_COLOR = ['#9ca3af', '#6b7280'] as const

function color(pair: readonly string[]): string {
  return isDark.value ? pair[1] : pair[0]
}

const gainTrace = computed(() => props.traces.find((t) => t.kind === 'Gain') ?? null)
const leftTrace = computed(() => props.traces.find((t) => t.kind === 'Left') ?? null)
const rightTrace = computed(() => props.traces.find((t) => t.kind === 'Right') ?? null)

/**
 * The curves currently on screen, in draw order. In gain view there is exactly one; in dBV view
 * there are the two inputs, subject to their checkboxes.
 */
const visibleTraces = computed<ResponseTrace[]>(() => {
  if (props.view === 'Gain') return gainTrace.value ? [gainTrace.value] : []

  const shown: ResponseTrace[] = []
  if (props.showLeft && leftTrace.value) shown.push(leftTrace.value)
  if (props.showRight && rightTrace.value) shown.push(rightTrace.value)
  return shown
})

/**
 * One curve per checked-on stored measurement, of the same kind the live view is showing — a stored
 * gain curve belongs under the live gain curve, not under the dBV one. Dash pattern comes from the
 * shared overlay styling, so a run is identified the same way here as on the sweep screen.
 */
const overlayCurves = computed(() =>
  props.overlays
    .map((overlay) => {
      const wanted: ResponseTrace['kind'] = props.view === 'Gain' ? 'Gain' : props.showLeft ? 'Left' : 'Right'
      const trace = overlay.responseTraces?.find((t) => t.kind === wanted)
      return trace ? { id: overlay.id, name: overlay.detail.name, trace } : null
    })
    .filter((entry): entry is { id: number; name: string; trace: ResponseTrace } => entry !== null),
)

/** The curve the bandwidth markers and the readout table describe — whichever one the view is about. */
const primaryTrace = computed<ResponseTrace | null>(() => {
  if (props.view === 'Gain') return gainTrace.value
  return (props.showLeft ? leftTrace.value : null) ?? rightTrace.value ?? leftTrace.value
})

/**
 * Phase gets its own scale and its own axis on the right, in degrees — the classic Bode layout, and
 * the only way a phase reading can actually be read off the plot. It was previously squeezed onto
 * the dB window, which put a number next to it that meant nothing.
 *
 * The window is a display-panel setting (props.phaseTopDeg/phaseBottomDeg) rather than fitted to the
 * data, so it stays put while a run comes in instead of rescaling under the curve; the panel's own
 * Autofit button covers the case the old auto-fit did. Independent of the dB window because the
 * phase here is *unwrapped* and the bulk delay already removed: a well-behaved DUT sits inside a few
 * tens of degrees, while a DUT with real excess phase runs well past 180° — the two cannot share one
 * pair of bounds.
 */
const phaseRange = computed<[number, number]>(() => [props.phaseBottomDeg, props.phaseTopDeg])

/**
 * The y window actually in effect — the FR (dBV) pair or the Gain (dB) pair, whichever `view` is
 * showing. The two are independent so switching views never clobbers the other one's range.
 */
const activeYTop = computed(() => (props.view === 'Dbv' ? props.yTopDbv : props.yTopDb))
const activeYBottom = computed(() => (props.view === 'Dbv' ? props.yBottomDbv : props.yBottomDb))

/**
 * Whether any currently-visible magnitude trace, within the plotted frequency window, sits above
 * or below the dB/dBV window — the cue that there is real data off the top or bottom of the chart.
 * Scoped to the primary magnitude axis only, not phase/coherence.
 */
const outOfRange = computed(() => {
  let above = false
  let below = false
  for (const trace of visibleTraces.value) {
    for (let i = 0; i < trace.freqHz.length; i++) {
      const freqHz = trace.freqHz[i]
      if (freqHz < props.xFromHz || freqHz > props.xToHz) continue
      if (trace.magnitudeMaxDb[i] > activeYTop.value) above = true
      if (trace.magnitudeMinDb[i] < activeYBottom.value) below = true
      if (above && below) return { above, below }
    }
  }
  return { above, below }
})

/**
 * The curve the phase series is drawn from — <b>not</b> `primaryTrace</b>, which is what the view is
 * about rather than what was measured.
 *
 * Phase is a property of the measurement, not of whichever magnitude curve is on screen. Sourcing it
 * from `primaryTrace` meant FR (dBV) view plotted the Left trace's phase, and for a stepped sine that
 * is a genuine array of zeros (only the Gain trace is a relationship between the two inputs) — drawn
 * as a solid 0° line, indistinguishable from a DUT with no phase shift at all. `PerTrace` is the one
 * case where the trace on screen really does carry its own: the chirp estimates Left and Right
 * against the synthesised sweep.
 */
const phaseTrace = computed<ResponseTrace | null>(() =>
  props.phaseKind === 'PerTrace' ? primaryTrace.value : gainTrace.value,
)

/** What the phase curve is entitled to be called — a difference between two measured paths is not a DUT's phase response. */
const phaseLabel = computed(() => (props.phaseKind === 'ChannelDifference' ? 'Phase difference' : 'Phase'))

/**
 * Draws the min of each column as a vertical stroke under its max, exactly as the spectrum chart's
 * band does. Both bounds must be drawn: the line alone is the max over the bins in that column, so
 * a rippled response would be redrawn riding along the top of its own ripple rather than through
 * the middle of it.
 */
function minMaxBandPlugin(): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          for (const trace of visibleTraces.value) {
            const path = new Path2D()
            for (let i = 0; i < trace.freqHz.length; i++) {
              const freqHz = trace.freqHz[i]
              if (!(freqHz > 0)) continue

              const x = u.valToPos(freqHz, 'x', true)
              path.moveTo(x, u.valToPos(trace.magnitudeMinDb[i], 'y', true))
              path.lineTo(x, u.valToPos(trace.magnitudeMaxDb[i], 'y', true))
            }

            ctx.strokeStyle = color(TRACE_COLORS[trace.kind])
            ctx.globalAlpha = 0.35
            ctx.lineWidth = 1
            ctx.stroke(path)
            ctx.globalAlpha = 1
          }

          ctx.restore()
        },
      ],
    },
  }
}

/**
 * The bandwidth markers: a dashed vertical at each corner, a double-headed arrow between them, and
 * a centred label — the previous app's `DrawBandwithLines`, which is what makes a bandwidth
 * readable off the plot rather than only out of the table.
 *
 * A clamped end is drawn at the edge of the measured band and labelled with a "&gt;", because the
 * curve never reached the threshold there: it is where the search stopped, not a corner.
 */
function bandwidthPlugin(): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const trace = primaryTrace.value
          if (!trace) return

          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          const readings: { label: string; reading: ResponseTrace['oneDb']; palette: readonly string[]; row: number }[] = []
          if (props.showOneDb) readings.push({ label: 'B1', reading: trace.oneDb, palette: BANDWIDTH_COLORS.oneDb, row: 0 })
          if (props.showThreeDb) readings.push({ label: 'B3', reading: trace.threeDb, palette: BANDWIDTH_COLORS.threeDb, row: 1 })

          // The reference and the thresholds the corners were found against, drawn first so the
          // arrows sit on top of them. Without these the corners are unexplainable by eye: a
          // passband-referenced reading's level is a median that appears nowhere in the readouts, so
          // a reader looking at a peak 4 dB above the trace has no way to see what the numbers were
          // measured from. Skipped entirely when referenceDb is null — a stored result from a build
          // that did not record it, where drawing a line would be inventing one.
          if (trace.referenceDb !== null && readings.length > 0) {
            const yRef = u.valToPos(trace.referenceDb, 'y', true)

            ctx.strokeStyle = color(REFERENCE_COLOR)
            ctx.fillStyle = color(REFERENCE_COLOR)
            ctx.lineWidth = 1
            ctx.setLineDash([])
            ctx.beginPath()
            ctx.moveTo(u.bbox.left, yRef)
            ctx.lineTo(u.bbox.left + u.bbox.width, yRef)
            ctx.stroke()

            ctx.font = '10px ui-sans-serif, system-ui, sans-serif'
            ctx.textAlign = 'left'
            ctx.textBaseline = 'bottom'
            ctx.fillText('ref', u.bbox.left + 3, yRef - 1)

            ctx.setLineDash([2, 3])
            for (const { reading, palette } of readings) {
              // Both sides for a symmetric reading, the lower one only for a one-sided one — which
              // is the difference between the two rows made visible rather than left in a tooltip.
              const levels = reading.symmetric
                ? [trace.referenceDb - reading.dropDb, trace.referenceDb + reading.dropDb]
                : [trace.referenceDb - reading.dropDb]

              ctx.strokeStyle = color(palette)
              for (const level of levels) {
                const y = u.valToPos(level, 'y', true)
                ctx.beginPath()
                ctx.moveTo(u.bbox.left, y)
                ctx.lineTo(u.bbox.left + u.bbox.width, y)
                ctx.stroke()
              }
            }
            ctx.setLineDash([])
          }

          for (const { label, reading, palette, row } of readings) {
            const stroke = color(palette)
            const xLow = u.valToPos(Math.max(reading.lowerHz, u.scales.x.min ?? 1), 'x', true)
            const xHigh = u.valToPos(Math.min(reading.upperHz, u.scales.x.max ?? reading.upperHz), 'x', true)

            // Rows are spaced from the top so the two arrows never overlap each other.
            const y = u.bbox.top + u.bbox.height * (0.18 + row * 0.14)

            ctx.strokeStyle = stroke
            ctx.fillStyle = stroke
            ctx.lineWidth = 1

            ctx.setLineDash([4, 3])
            for (const x of [xLow, xHigh]) {
              ctx.beginPath()
              ctx.moveTo(x, y)
              ctx.lineTo(x, u.bbox.top + u.bbox.height)
              ctx.stroke()
            }

            ctx.setLineDash([])
            ctx.beginPath()
            ctx.moveTo(xLow, y)
            ctx.lineTo(xHigh, y)
            ctx.stroke()

            const head = 5
            for (const [x, dir] of [
              [xLow, 1],
              [xHigh, -1],
            ] as const) {
              ctx.beginPath()
              ctx.moveTo(x, y)
              ctx.lineTo(x + dir * head, y - head * 0.7)
              ctx.lineTo(x + dir * head, y + head * 0.7)
              ctx.closePath()
              ctx.fill()
            }

            const text = `${label}: ${formatHz(reading.bandwidthHz)}${
              reading.lowerClamped || reading.upperClamped ? ' (partial)' : ''
            }`
            ctx.font = '11px ui-sans-serif, system-ui, sans-serif'
            ctx.textAlign = 'center'
            ctx.textBaseline = 'middle'

            const midX = (xLow + xHigh) / 2
            const width = ctx.measureText(text).width + 8
            ctx.fillStyle = isDark.value ? '#111827' : '#ffffff'
            ctx.fillRect(midX - width / 2, y - 8, width, 16)
            ctx.strokeRect(midX - width / 2, y - 8, width, 16)
            ctx.fillStyle = stroke
            ctx.fillText(text, midX, y)
          }

          ctx.restore()
        },
      ],
    },
  }
}

/**
 * The shared x grid. Every curve of one measurement was decimated on the same column boundaries, so
 * the first one's frequencies serve for all of them — but they are read from the data rather than
 * rebuilt from a log-spacing formula, for the reason streaming/protocol.ts documents.
 */
function buildData(): uPlot.AlignedData {
  // Falls back to an overlay's own grid when nothing has been measured yet: checking a stored
  // result on before running anything is a normal thing to do, and without this the chart has no x
  // axis to resample onto and silently draws nothing.
  const grid = visibleTraces.value[0] ?? primaryTrace.value ?? overlayCurves.value[0]?.trace
  if (!grid || grid.freqHz.length === 0) return [[], []]

  const x = Float64Array.from(grid.freqHz)
  const series: (Float64Array | (number | null)[])[] = []

  for (const trace of visibleTraces.value) series.push(Float64Array.from(trace.magnitudeMaxDb))

  // Both go out in their own units now that each has its own scale — degrees and 0..1 — so the
  // cursor and the axis agree with each other.
  if (props.showPhase) {
    const source = phaseTrace.value
    series.push(source ? Float64Array.from(source.phaseDeg) : new Float64Array(x.length))
  }

  if (props.showCoherence) {
    const source = primaryTrace.value
    series.push(source ? Float64Array.from(source.coherence) : new Float64Array(x.length))
  }

  // Overlays last, so they draw behind nothing and their series indices are stable against the
  // optional phase/coherence traces above.
  for (const overlay of overlayCurves.value) series.push(resampleOnto(overlay.trace, x))

  return [x, ...series] as uPlot.AlignedData
}

/**
 * A stored curve on the live curve's frequency grid. The two are decimated independently — a
 * different span or resolution puts the columns in different places — so the stored one is linearly
 * interpolated in log frequency rather than assumed to line up. Outside its own measured band it
 * yields null, which uPlot renders as a gap: a stored 24 kHz measurement must not be drawn as a flat
 * line across a 96 kHz one.
 */
function resampleOnto(trace: ResponseTrace, grid: Float64Array): (number | null)[] {
  const out: (number | null)[] = new Array(grid.length)
  const source = trace.freqHz
  let j = 0

  for (let i = 0; i < grid.length; i++) {
    const f = grid[i]
    if (source.length === 0 || f < source[0] || f > source[source.length - 1]) {
      out[i] = null
      continue
    }

    while (j < source.length - 2 && source[j + 1] < f) j++

    const f0 = source[j]
    const f1 = source[j + 1]
    const t = f1 > f0 ? (Math.log10(f) - Math.log10(f0)) / (Math.log10(f1) - Math.log10(f0)) : 0
    out[i] = trace.magnitudeMaxDb[j] + t * (trace.magnitudeMaxDb[j + 1] - trace.magnitudeMaxDb[j])
  }

  return out
}

function buildSeries(): uPlot.Series[] {
  const width = props.thickLines ? 2 : 1
  const points = { show: props.showDataPoints, size: 3 }

  const series: uPlot.Series[] = [{}]

  for (const trace of visibleTraces.value) {
    series.push({
      label: trace.kind === 'Gain' ? 'Gain' : trace.kind,
      stroke: color(TRACE_COLORS[trace.kind]),
      width,
      points,
    })
  }

  if (props.showPhase) {
    series.push({
      label: phaseLabel.value,
      scale: 'phase',
      stroke: color(PHASE_COLOR),
      width,
      dash: [4, 3],
      points: { show: false },
    })
  }

  if (props.showCoherence) {
    series.push({
      label: 'Coherence',
      scale: 'coh',
      stroke: color(COHERENCE_COLOR),
      width,
      dash: [2, 2],
      points: { show: false },
    })
  }

  for (const overlay of overlayCurves.value) {
    series.push({
      label: overlay.name,
      stroke: color(TRACE_COLORS.Gain),
      width: 1,
      dash: sweepOverlayDash(props.overlays, overlay.id) ?? [6, 3],
      points: { show: false },
      alpha: 0.7,
    })
  }

  return series
}

/**
 * One row of the cursor readout: every curve on the plot, at the frequency under the pointer.
 *
 * Built from the same three lists buildSeries walks, in the same order, because the readout indexes
 * uPlot's own data arrays — entry i is `u.data[i + 1]`. Nothing here can drift from the plot: every
 * input to this list is part of the rebuild signature below, so a change to it replaces the plot
 * whose data the readout is about to index.
 */
interface ReadoutSeries {
  key: string
  label: string
  color: string
  /** Appended with a space, except the degree sign — "12.3°", but "12.30 dB". */
  unit: string
  digits: number
}

const readoutSeries = computed<ReadoutSeries[]>(() => {
  // In gain view the one curve is a ratio; in dBV view the curves are absolute input levels. The
  // axis does not say which, because it is the same numbers either way — the readout is the only
  // place the distinction is written down.
  const magnitudeUnit = props.view === 'Gain' ? 'dB' : 'dBV'
  const entries: ReadoutSeries[] = []

  for (const trace of visibleTraces.value) {
    entries.push({
      key: `live-${trace.kind}`,
      label: trace.kind,
      color: color(TRACE_COLORS[trace.kind]),
      unit: magnitudeUnit,
      digits: 2,
    })
  }

  if (props.showPhase) {
    entries.push({ key: 'phase', label: phaseLabel.value, color: color(PHASE_COLOR), unit: '°', digits: 1 })
  }

  if (props.showCoherence) {
    entries.push({ key: 'coherence', label: 'Coh', color: color(COHERENCE_COLOR), unit: '', digits: 3 })
  }

  for (const overlay of overlayCurves.value) {
    entries.push({
      key: `overlay-${overlay.id}`,
      label: overlay.name,
      color: color(TRACE_COLORS.Gain),
      unit: magnitudeUnit,
      digits: 2,
    })
  }

  return entries
})

const cursorReadout = ref<{ freqHz: number; entries: (ReadoutSeries & { text: string })[] } | null>(null)

function formatReading(value: number, series: ReadoutSeries): string {
  if (!Number.isFinite(value)) return '—'

  // `(-0.0001).toFixed(2)` is "-0.00", which reads as a signed quantity where there is none — a
  // flat loopback's gain lands there at nearly every column. Same normalisation response/format.ts
  // applies to the readout table, generalised over the digit counts used here.
  const text = value.toFixed(series.digits).replace(/^-(0(?:\.0+)?)$/, '$1')

  if (series.unit === '') return text
  return series.unit === '°' ? `${text}°` : `${text} ${series.unit}`
}

/**
 * Whether the user has dragged a zoom. Drives the Reset zoom button, which is the only visible cue
 * that the axes are no longer the ones the panel's From/To and dB boxes name.
 *
 * <p><b>Recorded when the gesture happens</b>, rather than inferred by comparing the live scales
 * against the panel's window the way the shared `useZoomBounds` helper does. That comparison cannot
 * work on this chart, because its x axis was never at the panel's window to begin with — see the
 * range callbacks in `buildOptions`. It read "zoomed" from the first frame instead, which is what
 * put a Reset zoom button on an untouched chart.</p>
 *
 * <p>The x range callback consults it, so it has to be settled <i>before</i> the scale it guards is
 * computed. uPlot obliges: `mouseUp` fires `setSelect` before it calls `setScale` for the drag.</p>
 */
const zoomed = ref(false)

/**
 * Back to the window the display panel asks for — the Reset zoom button, and a double-click.
 *
 * <p>Clearing the flag first is not tidying: the range callback reads it, so a `setScale` made while
 * it was still true would be handed straight back out again.</p>
 */
function resetZoom() {
  const u = plot.value
  if (!u) return

  zoomed.value = false
  u.setScale('x', { min: props.xFromHz, max: props.xToHz })
  u.setScale('y', { min: activeYBottom.value, max: activeYTop.value })
}

/**
 * A signature of everything uPlot cannot be told about without rebuilding — the series list and
 * their styling. Data changes go through setData on the existing instance; only this forces a new
 * plot, per the project's "never re-create the plot per frame" rule.
 *
 * The four axis-window props are in here as well, and that is a consequence of the zoom rather than
 * of anything uPlot needs: the scale range callbacks now pass a pending window through instead of
 * always returning the props (which is what let every drag be undone the instant it was made), so a
 * changed dropdown no longer reaches the axis by itself. A rebuild is how it does — and it is also
 * the right behaviour, since the user moving a bound is the user saying which window they want, and
 * restoring the old zoom over it would make the panel look broken. See windowKey below.
 */
const signature = computed(() =>
  [
    props.view,
    visibleTraces.value.map((t) => t.kind).join(','),
    props.showPhase,
    // The series label is baked in at build time, so a change of kind needs a rebuild rather than a
    // redraw -- and the kind also decides which curve the phase is read from.
    props.phaseKind,
    props.showCoherence,
    overlayCurves.value.map((o) => o.id).join(','),
    props.thickLines,
    props.showDataPoints,
    isDark.value,
    props.xFromHz,
    props.xToHz,
    activeYBottom.value,
    activeYTop.value,
  ].join('|'),
)

/**
 * The axis-window half of the signature. A rebuild triggered by anything else — a theme flip, an
 * overlay checkbox, thick lines — has to give the user back the zoom they were looking at; a
 * rebuild triggered by one of these must not.
 */
function xWindowKey(): string {
  return `${props.xFromHz}|${props.xToHz}`
}

function yWindowKey(): string {
  return `${activeYBottom.value}|${activeYTop.value}`
}

let plotXWindowKey = ''
let plotYWindowKey = ''

type ScaleWindow = { min: number; max: number }

/** A live scale's window, or null while it is still unset. */
function windowOf(scale: uPlot.Scale | undefined): ScaleWindow | null {
  return scale?.min != null && scale.max != null ? { min: scale.min, max: scale.max } : null
}

function buildOptions(width: number, height: number): uPlot.Options {
  return {
    width,
    height,
    scales: {
      // Both windows *start* pinned to the display panel, and auto:false is what makes that stick:
      // otherwise every setData refits the axes to the data, and the response would crawl as the
      // averages came in.
      //
      // The x axis is the panel's window unless a drag is live, and the `zoomed` guard is the whole
      // of what makes both halves of that true.
      //
      // A pass-through — `min ?? props.xFromHz` — was what stood here, and on the *x* scale it is
      // dead code: uPlot never passes null for x once there is data. This chart hands real data
      // straight to the constructor, so the one call this callback gets carries the **data's own
      // span** (measured: 10..96000 on a 10 Hz-96 kHz sweep), which it then passed through. The
      // panel's From/To were discarded on every rebuild and the dropdowns did nothing at all.
      // Charts that seed an empty plot first, or push the window with an explicit setScale
      // afterwards, get nulls here and never showed it — see the note in CLAUDE.md.
      //
      // Ignoring the arguments outright is the opposite bug, and was what stood here before that:
      // uPlot re-derives x through this callback on *every* commit, including the one carrying the
      // window a drag just asked for, so it fed the props back in and undid the zoom as it was
      // made. Hence a flag rather than a `??` — it separates "uPlot is handing me a zoom" from
      // "uPlot is handing me whatever the axis happened to hold".
      x: {
        time: false,
        distr: 3,
        log: 10,
        auto: false,
        range: (_u, min, max) =>
          zoomed.value && min != null && max != null
            ? [min, max]
            : [props.xFromHz, props.xToHz],
      },
      y: {
        auto: false,
        range: (_u, min, max) => [min ?? activeYBottom.value, max ?? activeYTop.value],
      },
      // The two right-hand scales are deliberately left auto-ranged and are not what a y drag
      // zooms (uPlot zooms the scale of the first plotted series, which is a magnitude curve).
      // Phase is already fitted to its own data, and coherence is a 0..1 quantity whose axis means
      // nothing rescaled.
      phase: { range: () => phaseRange.value },
      coh: { range: () => [0, 1] as [number, number] },
    },
    axes: [
      {
        ...freqAxis(),
        stroke: isDark.value ? '#9ca3af' : '#4b5563',
        grid: { stroke: isDark.value ? '#374151' : '#e5e7eb', width: 1 },
        ticks: { stroke: isDark.value ? '#374151' : '#e5e7eb', width: 1 },
      },
      {
        size: 50,
        stroke: isDark.value ? '#9ca3af' : '#4b5563',
        grid: { stroke: isDark.value ? '#374151' : '#e5e7eb', width: 1 },
        ticks: { stroke: isDark.value ? '#374151' : '#e5e7eb', width: 1 },
      },
      // Right-hand axes, outermost last. Only the visible trace's axis is shown, and neither draws
      // gridlines: a second and third grid over the dB one turns the plot into graph paper, and the
      // dB grid is the one people read a response off.
      {
        scale: 'phase',
        side: 1,
        show: props.showPhase,
        size: 52,
        incrs: [15, 30, 45, 90, 180, 360, 720, 1440],
        values: (_u, vals) => vals.map((v) => `${v}°`),
        stroke: color(PHASE_COLOR),
        grid: { show: false },
        ticks: { stroke: color(PHASE_COLOR), width: 1 },
      },
      {
        scale: 'coh',
        side: 1,
        show: props.showCoherence,
        size: 44,
        incrs: [0.1, 0.2, 0.25, 0.5],
        values: (_u, vals) => vals.map((v) => v.toFixed(1)),
        stroke: color(COHERENCE_COLOR),
        grid: { show: false },
        ticks: { stroke: color(COHERENCE_COLOR), width: 1 },
      },
    ],
    series: buildSeries(),
    cursor: {
      // A dot on every curve at the hovered column, so the numbers in the readout above can be
      // told apart when two curves run close together.
      points: { show: true },
      // Both axes draggable, with `uni` making a drag unidirectional unless it is a real box: a
      // drag that stays under 12 px in one direction zooms the other axis across its full span.
      // Frequency-only is the common gesture (narrowing onto a corner), but a dB window matters
      // just as much on a response that is flat to a fraction of a dB, and demanding a
      // pixel-accurate horizontal drag to get one without the other would make both awkward.
      drag: { x: true, y: true, uni: 12 },
    },
    // Same reason as the other charts: uPlot's own legend doubles as a click-to-hide control, which
    // would desync from the display panel's checkboxes — those are the single source of visibility.
    legend: { show: false },
    hooks: {
      // Drives the Reset zoom button. Fires on a completed drag and nothing else — uPlot suppresses
      // it for the selection it hides afterwards — so it is the one unambiguous "the user zoomed"
      // signal. setScale is not: it fires on every setData as well.
      setSelect: [
        (u: uPlot) => {
          if (u.select.width > 0 || u.select.height > 0) zoomed.value = true
        },
      ],
      // u.cursor.idx indexes the shared x array buildData produced — not a pixel-derived value, so
      // it always names a column something was actually plotted at, and the readout can index every
      // series at it. null once the pointer leaves the plot (uPlot clears idx on mouseleave).
      setCursor: [
        (u: uPlot) => {
          const idx = u.cursor.idx
          const xs = u.data[0]
          if (idx == null || idx < 0 || xs == null || xs[idx] == null) {
            cursorReadout.value = null
            return
          }

          cursorReadout.value = {
            freqHz: xs[idx] as number,
            entries: readoutSeries.value.map((series, i) => {
              const value = u.data[i + 1]?.[idx]
              return { ...series, text: value == null ? '—' : formatReading(value as number, series) }
            }),
          }
        },
      ],
    },
    plugins: [minMaxBandPlugin(), bandwidthPlugin()],
  }
}

function rebuild() {
  const el = container.value
  if (!el) return

  // A rebuild that is not itself an axis-window change hands the zoom back afterwards — a theme
  // flip or an overlay checkbox must not throw away the window the user is reading. A changed window
  // drops it instead, on *both* axes together: the panel is the user saying which window they want,
  // and restoring half a zoom would leave the plot zoomed with no button offering to undo it.
  const nextX = xWindowKey()
  const nextY = yWindowKey()
  const windowChanged = plotXWindowKey !== nextX || plotYWindowKey !== nextY
  const restore =
    plot.value && !windowChanged
      ? { x: windowOf(plot.value.scales.x), y: windowOf(plot.value.scales.y) }
      : null
  plotXWindowKey = nextX
  plotYWindowKey = nextY
  if (windowChanged) zoomed.value = false

  plot.value?.destroy()
  cursorReadout.value = null

  const width = el.clientWidth || 800
  const height = el.clientHeight || CHART_HEIGHT_PX
  const u = new uPlot(buildOptions(width, height), buildData(), el)
  plot.value = u

  // uPlot's own double-click resets the x axis to the data extent, which on an auto:false chart is
  // whatever the zoom already shows. Ours is added afterwards on the same element, so it runs
  // second and actually gets back out. See resetZoom.
  u.over.addEventListener('dblclick', resetZoom)

  // Safe here, unlike the sweep chart's deferred restore: the data is passed to the constructor, so
  // no setData follows to clobber the scales.
  if (restore?.x) u.setScale('x', restore.x)
  if (restore?.y) u.setScale('y', restore.y)
}

function refresh() {
  if (!plot.value) {
    rebuild()
    return
  }

  plot.value.setData(buildData())
  plot.value.redraw()
}

let observer: ResizeObserver | null = null

onMounted(() => {
  rebuild()
  observer = new ResizeObserver(() => {
    const el = container.value
    if (el && plot.value) plot.value.setSize({ width: el.clientWidth, height: el.clientHeight })
  })
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  plot.value?.destroy()
  plot.value = null
})

watch(signature, () => rebuild())
watch([() => props.traces, () => props.overlays], refresh)

// The bandwidth markers are drawn straight from the props by their plugin, so toggling one needs a
// redraw and nothing more. The axis bounds are *not* here any more — they are part of the rebuild
// signature now, for the reason its own remarks give.
watch(() => [props.showOneDb, props.showThreeDb], () => refresh())

// A settings change, not a data change, but still only a re-range: the phase scale is not part of
// the drag-zoom (uPlot zooms the first plotted series, which is the dB one), so nothing about the
// series or their styling needs rebuilding here.
watch(phaseRange, () => refresh())
</script>

<template>
  <div class="flex flex-col gap-2">
    <!-- Cursor readout on the left, zoom state on the right. Both are about the plot below rather
         than about the measurement, so they sit with it and not in the display panel. -->
    <div class="flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span v-if="cursorReadout" class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-mono">
        <span class="text-neutral-800 dark:text-neutral-100">{{ formatHz(cursorReadout.freqHz) }}</span>
        <span v-for="entry in cursorReadout.entries" :key="entry.key" class="flex items-center gap-1">
          <span class="inline-block h-1.5 w-1.5 shrink-0 rounded-full" :style="{ backgroundColor: entry.color }" />
          <span class="max-w-32 truncate" :title="entry.label">{{ entry.label }}</span>
          <span class="text-neutral-800 dark:text-neutral-100">{{ entry.text }}</span>
        </span>
      </span>
      <span v-else>Hover the plot for values at a frequency</span>

      <!-- The only visible sign that the axes are no longer the window the display panel names —
           double-clicking does the same thing, but nothing on screen would say so. The hint takes
           its place while unzoomed, so the header never carries two messages about one gesture. -->
      <button
        v-if="zoomed"
        type="button"
        class="shrink-0 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Back to the frequency and dB window set in the display panel."
        @click="resetZoom"
      >
        Reset zoom
      </button>
      <span v-else class="shrink-0">Drag to zoom · double-click to reset</span>
    </div>

    <div class="relative w-full" :style="{ height: `${CHART_HEIGHT_PX}px` }">
      <div ref="container" class="h-full w-full" />
      <!-- Off-scale cue: real data sits above/below the currently visible dB/dBV window. -->
      <div
        v-if="outOfRange.above"
        class="pointer-events-none absolute right-2 top-2 text-amber-500 dark:text-amber-400"
        title="Data above the visible range"
      >
        ▲
      </div>
      <div
        v-if="outOfRange.below"
        class="pointer-events-none absolute bottom-2 right-2 text-amber-500 dark:text-amber-400"
        title="Data below the visible range"
      >
        ▼
      </div>
    </div>
    <p v-if="traces.length === 0 && overlays.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
      No frequency response measured yet. Press {{ props.runLabel ?? 'RUN' }}.
    </p>
  </div>
</template>

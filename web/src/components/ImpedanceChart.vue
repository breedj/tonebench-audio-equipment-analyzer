<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import type { ImpedanceZScale } from '../api/client'
import type { OverlayEntry } from '../composables/useMeasurements'
import { useImpedance, type ImpedanceTrace } from '../composables/useImpedance'
import { impedanceOverlayDash } from '../measurements/sweepOverlayStyles'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import {
  CHART_EXPORT_HEADER_PX,
  composeChartPng,
  nextPaint,
  exportAxisLabel,
  type ChartExportOptions,
  type ExportLegendEntry,
} from '../charts/exportChart'
import { makeChartPalette, type ChartPalette } from '../charts/palette'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { freqAxis } from '../freqAxis'
import { formatHz } from '../response/format'
import { useAppTheme } from '../composables/useAppTheme'
import { ohmAxis } from '../impedance/ohmAxis'
import { dampingFactorTicks, formatDampingFactor } from '../impedance/dampingFactorAxis'
import { dbOhmsToOhms, formatOhms } from '../impedance/equivalent'
import { standardOnGrid } from '../impedance/standard'

/**
 * |Z| against frequency, with phase on a second axis.
 *
 * <p>Structurally the frequency-response Bode chart, and deliberately so — the phase scale, the
 * min/max band plugin, the zoom bookkeeping and the cursor readout are the same problems with the
 * same answers. What differs is the left axis: <b>ohms</b> rather than dB, linear by default — the
 * way an impedance curve is usually drawn — or logarithmic on request. An impedance runs from a
 * fixture's milliohms to a valve grid's megohms, and a decade is a decade either way, so the log
 * axis is the one that shows a resonance peak and a voice coil in one view; the linear one is the
 * one that shows a 7.9 Ω resistor as the flat line it is.</p>
 *
 * <p>The wire and the stored rows carry dB relative to 1 ohm, because that is what the shared
 * decimator and <c>response_trace</c> columns hold. It is converted back to ohms here, at the last
 * possible moment — nobody reads a loudspeaker's impedance in decibels.</p>
 */
const props = defineProps<{
  traces: ImpedanceTrace[]
  /** Stored measurements checked on in the History list, drawn dashed behind the live curve. */
  overlays: OverlayEntry[]
  showPhase: boolean
  showCoherence: boolean
  /**
   * Draw the stored short standard as the impedance it represents — the floor every reading sits on
   * top of.
   *
   * <p>There is deliberately no equivalent for the <i>open</i> standard. It represents an open
   * circuit, so the impedance it inverts to is enormous — clamped at 1e9 Ω here — and it can only
   * ever sit far above the top of a useful ohms window. Drawing it pushed the axis out or vanished
   * off the top, and told you nothing the coherence trace does not.</p>
   */
  showShortCal: boolean
  thickLines: boolean
  showDataPoints: boolean
  /** The |Z| window, in ohms. On a logarithmic axis both bounds must be above zero; a linear one may start at zero. */
  zTopOhms: number
  zBottomOhms: number
  zScale: ImpedanceZScale
  xFromHz: number
  xToHz: number
  /** The phase axis' own window, in degrees. A passive one-port stays inside ±90°, but a bad calibration will not. */
  phaseTopDeg: number
  phaseBottomDeg: number
  /**
   * The output-impedance screen's second reading: a damping-factor curve, `nominal / |Z|`, on a
   * right-hand axis whose window is the ohms window inverted — so the two curves mirror each other
   * and a reader can take either. Derived at draw time from the same trace; nothing is stored twice.
   */
  showDampingFactor?: boolean
  /** What the damping factor is quoted against, in ohms. Required for `showDampingFactor`; also retitles the export. */
  dampingFactorNominalOhms?: number
}>()

const { isDark } = useAppTheme()

const root = ref<HTMLDivElement | null>(null)
const plotBox = ref<HTMLDivElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const plot = shallowRef<uPlot | null>(null)

// Fitted to the view — see charts/useResultChartHeight.ts; set as an inline style on the plot box
// so the ResizeObserver below keeps reading one number whatever changed.
const chartHeight = useResultChartHeight(root, plotBox)

const IMPEDANCE_COLOR = ['#111827', '#f3f4f6'] as const
const SHORT_CAL_COLOR = ['#c2410c', '#fb923c'] as const
const PHASE_COLOR = ['#7c3aed', '#a78bfa'] as const
const COHERENCE_COLOR = ['#059669', '#34d399'] as const
const DAMPING_FACTOR_COLOR = ['#2563eb', '#60a5fa'] as const

/** The screen's palette, following the app theme; the export builds its own light one. See charts/palette.ts. */
const palette = computed(() => makeChartPalette(isDark.value))

function color(pair: readonly string[]): string {
  return palette.value.color(pair)
}

/** The DUT curve. */
const trace = computed(() => props.traces.find((t) => t.kind === 'Impedance') ?? null)

/**
 * Whether the live measurement occupies run 0 — see runDash. With nothing measured, the first
 * checked history item is promoted to the solid line instead of every curve on screen being dashed.
 */
const liveDrawn = computed(() => trace.value !== null)

const { storedShortCal, referenceOhms } = useImpedance()

/** The x grid: the DUT's when there is one, otherwise a standard's own so it can be seen alone. */
const gridHz = computed<Float64Array>(() => {
  const t = trace.value
  const cal = props.showShortCal ? storedShortCal.value : null
  const base = t ? t.freqHz : (cal?.freqHz ?? [])

  return extendOverBands(
    base,
    overlayCurves.value.map((overlay) => overlay.trace.freqHz),
  )
})

/**
 * The measured grid, widened to cover every overlay's band.
 *
 * <p><b>Why the live grid alone will not do.</b> uPlot's AlignedData gives every series one shared x
 * array, so anything the x array does not reach simply is not drawn. A stepped sweep publishes after
 * every point, so its grid <i>grows</i> — and an overlay resampled onto it was therefore clipped to
 * however far the sweep had got. A stored result put on the chart for comparison vanished the moment
 * a new run started and then crept back in from the left over the next few minutes, which reads as
 * the overlay being consumed by the measurement.</p>
 *
 * <p>Only points <b>outside</b> the measured span are added, never between measured ones. That is
 * what keeps everything else honest: inside the span the x array is still exactly the measured
 * frequencies, so the live series lands on real samples, the Data points markers mark real
 * measurements, and the cursor reads a measured value rather than an interpolated one. Beyond the
 * span the live series is null, so the trace ends where the measurement ends and the cursor says
 * "—" — which is the truth.</p>
 */
function extendOverBands(base: ArrayLike<number>, extras: ArrayLike<number>[]): Float64Array {
  if (base.length === 0) {
    const first = extras.find((e) => e.length > 0)
    return first ? Float64Array.from(first as ArrayLike<number>) : new Float64Array()
  }

  const low = base[0]
  const high = base[base.length - 1]

  const outside: number[] = []
  for (const extra of extras) {
    for (let i = 0; i < extra.length; i++) {
      const f = extra[i]
      if (f < low || f > high) outside.push(f)
    }
  }

  if (outside.length === 0) return Float64Array.from(base as ArrayLike<number>)

  // De-duplicated: two overlays covering the same band contribute the same frequencies, and a
  // repeated x is a zero-width segment uPlot has to draw for nothing.
  const merged = [...new Set([...(Array.from(base as ArrayLike<number>) as number[]), ...outside])]
  merged.sort((a, b) => a - b)
  return Float64Array.from(merged)
}

/** The short standard resolved onto the chart's grid — magnitude and angle from one inversion. */
const shortCal = computed(() =>
  props.showShortCal ? standardOnGrid(storedShortCal.value, gridHz.value, referenceOhms.value) : null,
)

const shortCalOhms = computed(() => shortCal.value?.ohms ?? null)

/**
 * The standard's own phase, drawn only when the phase axis is showing.
 *
 * <p>Gated on both because there is nowhere to read it otherwise: it belongs on the right-hand
 * degrees axis, which only exists while Phase is ticked. Worth having there — a test lead is a small
 * resistance in series with a little inductance, so its angle is nothing at the bottom of the band
 * and climbs steeply once the reactance overtakes the resistance (measured on this fixture's leads,
 * 0.117 Ω + 0.58 µH: 1.8° at 1 kHz, 17° at 10 kHz, 32° at 20 kHz). That is the error underneath
 * every reading at the top of the band, and the magnitude trace alone does not show it.</p>
 */
const shortCalPhase = computed(() => (props.showPhase && shortCal.value ? shortCal.value.phaseDeg : null))

const overlayCurves = computed(() =>
  props.overlays
    .map((overlay) => {
      const stored = overlay.impedanceTraces?.find((t) => t.kind === 'Impedance')
      return stored ? { id: overlay.id, name: overlay.detail.name, trace: stored } : null
    })
    .filter((entry): entry is { id: number; name: string; trace: ImpedanceTrace } => entry !== null),
)

/**
 * The min/max band, drawn as a faint vertical at each column. Both bounds are sent and both must be
 * drawn: plotting the max alone throws the min away in the browser and draws a curve's own ripple as
 * a thin line above where it really sits.
 */
function minMaxBandPlugin(p: ChartPalette): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          const t = trace.value
          if (!t) return

          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          const path = new Path2D()
          for (let i = 0; i < t.freqHz.length; i++) {
            const freqHz = t.freqHz[i]
            if (!(freqHz > 0)) continue

            const x = u.valToPos(freqHz, 'x', true)
            path.moveTo(x, u.valToPos(dbOhmsToOhms(t.magnitudeMinDbOhms[i]), 'y', true))
            path.lineTo(x, u.valToPos(dbOhmsToOhms(t.magnitudeMaxDbOhms[i]), 'y', true))
          }

          ctx.strokeStyle = p.color(IMPEDANCE_COLOR)
          ctx.globalAlpha = 0.35
          ctx.lineWidth = 1
          ctx.stroke(path)
          ctx.globalAlpha = 1
          ctx.restore()
        },
      ],
    },
  }
}

/** Linear interpolation in log frequency, returning null outside the stored curve's own band so uPlot draws a gap. */
function resampleOnto(source: ImpedanceTrace, grid: Float64Array): (number | null)[] {
  return interpolateOnto(source.freqHz, source.magnitudeMaxDbOhms, grid, dbOhmsToOhms)
}

/**
 * A stored curve's phase on the chart's grid, the same way its magnitude gets there. Interpolating a
 * <i>stored</i> curve is fine where interpolating the live one is not (see `padToGrid`): the stored
 * rows are already a decimated display trace rather than the measurement's own points, and the
 * magnitude has always been drawn this way. A history overlay without its phase was half a curve —
 * on an impedance plot the phase is the half that says inductive or capacitive.
 */
function resamplePhaseOnto(source: ImpedanceTrace, grid: Float64Array): (number | null)[] {
  return interpolateOnto(source.freqHz, source.phaseDeg, grid, (v) => v)
}

function interpolateOnto(
  freqs: number[],
  values: number[],
  grid: Float64Array,
  convert: (v: number) => number,
): (number | null)[] {
  const out: (number | null)[] = new Array(grid.length)
  let j = 0

  for (let i = 0; i < grid.length; i++) {
    const f = grid[i]
    if (freqs.length === 0 || values.length !== freqs.length || f < freqs[0] || f > freqs[freqs.length - 1]) {
      out[i] = null
      continue
    }

    while (j < freqs.length - 2 && freqs[j + 1] < f) j++

    const f0 = freqs[j]
    const f1 = freqs[j + 1]
    const t = f1 > f0 ? (Math.log10(f) - Math.log10(f0)) / (Math.log10(f1) - Math.log10(f0)) : 0
    out[i] = convert(values[j] + t * (values[j + 1] - values[j]))
  }

  return out
}

/**
 * A per-measured-point array placed onto `grid`, null wherever the grid reaches past the measurement.
 *
 * <p>Not interpolated, unlike `resampleOnto`: phase and coherence are read at the point
 * rather than derived from a curve, and inventing values between them would be inventing
 * measurements. The measured frequencies are a prefix-aligned subset of the grid, so a single walk
 * placing each sample at its own frequency is enough.</p>
 */
function padToGrid(
  values: number[] | undefined,
  freqHz: number[] | undefined,
  grid: Float64Array,
): (number | null)[] {
  const out = new Array<number | null>(grid.length).fill(null)
  if (!values || !freqHz || values.length === 0) return out

  let j = 0
  for (let i = 0; i < grid.length && j < freqHz.length; i++) {
    if (grid[i] === freqHz[j]) out[i] = values[j++]
  }

  return out
}

function buildData(): uPlot.AlignedData {
  const t = trace.value
  const x = gridHz.value
  const series: (Float64Array | (number | null)[])[] = []

  if (t) {
    // Through the same resampler the overlays use, because `x` may now be wider than the measured
    // grid. Inside the measured span every x value *is* a measured frequency, so this returns the
    // measured number exactly (the interpolation lands on t = 0); outside it, null, which is what
    // stops the trace being drawn across frequencies nobody has measured yet.
    series.push(resampleOnto(t, x))
  } else {
    series.push(new Array<number | null>(x.length).fill(null))
  }

  for (const overlay of overlayCurves.value) series.push(resampleOnto(overlay.trace, x))

  if (shortCalOhms.value) series.push(shortCalOhms.value)

  // Padded to `x` for the same reason the magnitude is: these arrays are indexed by the measured
  // grid, and uPlot pairs series with x by position, so a short array would shift every phase and
  // coherence sample onto a lower frequency than it was measured at.
  if (props.showPhase) {
    series.push(padToGrid(t?.phaseDeg, t?.freqHz, x))
    for (const overlay of overlayCurves.value) series.push(resamplePhaseOnto(overlay.trace, x))
  }
  if (shortCalPhase.value) series.push(shortCalPhase.value)
  if (props.showCoherence) series.push(padToGrid(t?.coherence, t?.freqHz, x))

  return [x, ...series] as uPlot.AlignedData
}

const dampingFactorOn = computed(() => props.showDampingFactor === true && (props.dampingFactorNominalOhms ?? 0) > 0)

/**
 * One plotted curve: what uPlot draws it with, and what the legend and cursor readout say about it.
 *
 * <p>Style lives here and nowhere else. The list used to exist twice — once for uPlot, once for the
 * readout — which is a standing invitation for a legend swatch to disagree with the line it names,
 * and for the readout to index the wrong column if the two ever fall out of order.</p>
 */
type PlottedSeries = {
  key: string
  label: string
  color: string
  unit: 'ohms' | 'deg' | 'ratio'
  width: number
  /** uPlot's dash pattern, reused verbatim for the legend swatch so the two cannot disagree. */
  dash?: number[]
  /** The right-hand scales; absent means the shared ohms axis. */
  /**
   * Which uPlot scale the series is drawn against. Omitted for everything on the ohms axis, which
   * `buildSeries` resolves to <b>'y'</b> — see the note there. Never leave this to uPlot's own
   * default by passing it through undefined.
   */
  scale?: string
  points: boolean
  /** The `[light, dark]` pair `color` was picked from — the export picks again for its own palette. */
  pair: readonly string[]
}

/** Every curve on the chart, in the order buildData stacks their arrays. */
const plottedSeries = computed<PlottedSeries[]>(() => {
  const width = props.thickLines ? 2 : 1

  const entries: PlottedSeries[] = [
    {
      key: 'z',
      label: '|Z|',
      color: color(IMPEDANCE_COLOR),
      pair: IMPEDANCE_COLOR,
      unit: 'ohms',
      width,
      points: props.showDataPoints,
    },
  ]

  // Overlays share the DUT's colour and are told apart by dash pattern alone, which is exactly why
  // the legend has to draw the pattern rather than a coloured dot.
  for (const overlay of overlayCurves.value) {
    entries.push({
      key: `o${overlay.id}`,
      label: overlay.name,
      color: color(IMPEDANCE_COLOR),
      pair: IMPEDANCE_COLOR,
      unit: 'ohms',
      // Width and markers follow the live |Z| curve this is drawn to be compared against; a fixed
      // weight here made Thick lines and Data points apply to half the chart only.
      width,
      dash: impedanceOverlayDash(props.overlays, overlay.id, liveDrawn.value) ?? undefined,
      points: props.showDataPoints,
    })
  }

  if (shortCalOhms.value) {
    entries.push({
      key: 'short',
      label: 'Short cal',
      color: color(SHORT_CAL_COLOR),
      pair: SHORT_CAL_COLOR,
      unit: 'ohms',
      width: 1,
      dash: [2, 3],
      points: false,
    })
  }

  if (props.showPhase) {
    entries.push({
      key: 'phase',
      label: 'Phase',
      color: color(PHASE_COLOR),
      pair: PHASE_COLOR,
      unit: 'deg',
      width,
      dash: [4, 3],
      scale: 'phase',
      points: false,
    })

    // The same rule as the magnitude overlays, on the phase axis: the phase colour says which
    // quantity, the overlay's dash says which stored run — so a history entry's two curves share a
    // dash and can be matched by eye across the two axes.
    for (const overlay of overlayCurves.value) {
      entries.push({
        key: `o${overlay.id}p`,
        label: `${overlay.name} phase`,
        color: color(PHASE_COLOR),
        pair: PHASE_COLOR,
        unit: 'deg',
        // Markers stay off, as they are on the live phase curve: each overlay series follows the
        // live series it mirrors.
        width,
        dash: impedanceOverlayDash(props.overlays, overlay.id, liveDrawn.value) ?? undefined,
        scale: 'phase',
        points: false,
      })
    }
  }

  if (shortCalPhase.value) {
    entries.push({
      key: 'shortPhase',
      label: 'Short cal phase',
      // The standard's colour with the phase curve's dash: colour says which curve, dash says which
      // quantity, so it reads against both the orange magnitude and the purple phase beside it.
      color: color(SHORT_CAL_COLOR),
      pair: SHORT_CAL_COLOR,
      unit: 'deg',
      width: 1,
      dash: [4, 3],
      scale: 'phase',
      points: false,
    })
  }

  if (props.showCoherence) {
    entries.push({
      key: 'coh',
      label: 'Coherence',
      color: color(COHERENCE_COLOR),
      pair: COHERENCE_COLOR,
      unit: 'ratio',
      width: 1,
      scale: 'coh',
      points: false,
    })
  }


  return entries
})

/** An SVG dash-array for the legend swatch — the same numbers uPlot strokes the line with. */
function swatchDash(series: PlottedSeries): string | undefined {
  return series.dash?.join(' ')
}

/** A hairline is legible on a plot and invisible in a 14 px swatch, so the swatch has a floor. */
function swatchWidth(series: PlottedSeries): number {
  return Math.max(series.width, 1.5)
}

function buildSeries(p: ChartPalette): uPlot.Series[] {
  return [
    // Slot 0 is the x series, which is never drawn.
    {},
    // `s.scale ?? 'y'`, and the fallback is load-bearing rather than defensive. Everything on the
    // ohms axis -- |Z|, the history overlays and the short standard -- leaves `scale` unset, and
    // handing uPlot an explicit `scale: undefined` is not the same as omitting the key: it creates a
    // scale literally named "undefined", auto-ranging (`auto: true`, `distr: 1`) to whatever that
    // series happens to contain. The curve then fills the plot height whatever the numbers are,
    // while the visible axis labels belong to the 'y' scale that now has nothing drawn against it.
    //
    // It presents as a measurement that is "all over the place": a 7.9 ohm resistor reading
    // 7.8555-7.8703 -- flat to 0.02 ohm -- was drawn as a wild +/-30% swing, because 0.02 ohm of
    // noise was being stretched to full scale. The cursor readout stayed correct throughout, since
    // it reads the trace rather than the plot, which is what makes this so hard to see: every
    // number on the screen agrees except the one line you are looking at.
    ...plottedSeries.value.map((s) => ({
      label: s.label,
      scale: s.scale ?? 'y',
      stroke: p.color(s.pair),
      width: s.width,
      dash: s.dash,
      points: { show: s.points, size: 4 },
    })),
  ]
}

const phaseRange = computed<[number, number]>(() => [props.phaseBottomDeg, props.phaseTopDeg])

const logZ = computed(() => props.zScale === 'Log')

/** Guarded: a log axis cannot show zero or a negative, and a bound of 0 would blank the whole plot. A linear one may sit on zero. */
const yBottom = computed(() => (logZ.value ? Math.max(props.zBottomOhms, 1e-4) : Math.max(props.zBottomOhms, 0)))
const yTop = computed(() => Math.max(props.zTopOhms, logZ.value ? yBottom.value * 1.01 : yBottom.value + 1e-3))

// Carries the whole series description, so the readout draws the same swatch the legend does.
const cursorReadout = ref<{ freqHz: number; entries: (PlottedSeries & { text: string })[] } | null>(null)

/**
 * The header row: every plotted curve, carrying its value at the cursor while the pointer is over
 * the plot. One list rather than a legend and a readout, so a series is named once either way.
 */
const legendEntries = computed<(PlottedSeries & { text?: string })[]>(
  () => cursorReadout.value?.entries ?? plottedSeries.value,
)

function formatReading(value: number, series: PlottedSeries): string {
  // The damping factor has no curve of its own, so the impedance reading carries it — otherwise
  // turning the axis on would put a number on the axis that the cursor could not tell you.
  if (series.unit === 'ohms') {
    const nominal = props.dampingFactorNominalOhms ?? 0
    return dampingFactorOn.value && value > 0
      ? `${formatOhms(value)} · DF ${formatDampingFactor(nominal / value)}`
      : formatOhms(value)
  }
  if (series.unit === 'deg') return `${value.toFixed(1)}°`
  return value.toFixed(3)
}

/**
 * Whether the user has zoomed the plot by dragging on it.
 *
 * <p><b>Recorded when the gesture happens, rather than inferred by comparing the live scales against
 * the display panel's window.</b> The comparison the shared `useZoomBounds` helper makes cannot work
 * on this chart's x axis, because that axis is never at the panel's window in the first place — see
 * the range callbacks in `buildOptions`. It therefore read "zoomed" from the first frame, which is
 * what put a Reset zoom button on an untouched chart.</p>
 *
 * <p>It is also what the x range callback consults, so it has to be settled <i>before</i> the scale
 * it guards is computed. uPlot obliges: `mouseUp` fires `setSelect` before it calls `setScale` for
 * the drag, so the flag is already true by the time the callback is asked for the zoomed window.</p>
 */
const zoomed = ref(false)

/**
 * Back to the window the display panel asks for — the Reset zoom button, and a double-click.
 *
 * <p>Clearing the flag first is not tidying: the range callback below reads it, so a `setScale` made
 * while it was still true would be handed straight back out again.</p>
 */
function resetZoom() {
  const u = plot.value
  if (!u) return

  zoomed.value = false
  u.setScale('x', { min: props.xFromHz, max: props.xToHz })
  u.setScale('y', { min: yBottom.value, max: yTop.value })
}

/** Everything uPlot cannot be told without a new instance. */
const signature = computed(() =>
  [
    props.showPhase,
    props.showCoherence,
    dampingFactorOn.value,
    props.dampingFactorNominalOhms,
    shortCalOhms.value !== null,
    shortCalPhase.value !== null,
    overlayCurves.value.map((o) => o.id).join(','),
    props.thickLines,
    props.showDataPoints,
    isDark.value,
    props.xFromHz,
    props.xToHz,
    yBottom.value,
    yTop.value,
    // The distribution is fixed at construction, so a scale change is a rebuild.
    props.zScale,
  ].join('|'),
)

function xWindowKey(): string {
  return `${props.xFromHz}|${props.xToHz}`
}

function yWindowKey(): string {
  return `${yBottom.value}|${yTop.value}`
}

let plotXWindowKey = ''
let plotYWindowKey = ''

function windowOf(scale: uPlot.Scale | undefined): { min: number; max: number } | null {
  return scale?.min != null && scale.max != null ? { min: scale.min, max: scale.max } : null
}

/**
 * @param p The palette every colour is taken from — see charts/palette.ts.
 * @param interactive False for the export's offscreen plot: no cursor, and none of the hooks that
 *   feed the readout and the zoom flag.
 */
function buildOptions(width: number, height: number, p: ChartPalette, interactive: boolean): uPlot.Options {
  return {
    width,
    height,
    scales: {
      // The x axis is the display panel's window unless a drag is live, and the `zoomed` guard is
      // the whole of what makes both halves of that true.
      //
      // A pass-through — `min ?? props.xFromHz` — is the obvious spelling and it does not work here,
      // because uPlot never passes null for the *x* scale once there is data: measured against
      // 1.6.32, a freshly built plot calls this callback exactly once with the data's own span
      // (10..96000 for a 10 Hz–96 kHz sweep) and never with the nulls the `??` was waiting for. So
      // the panel's window was silently discarded on every rebuild, the axis always showed the full
      // sweep, and the frequency dropdowns did nothing at all. (The y scale is not the x scale and
      // does get nulls, which is why it honoured its bounds and only the frequency axis looked broken.)
      //
      // Ignoring the arguments outright is equally wrong in the other direction: uPlot re-derives the
      // x scale through this callback on *every* commit, including the one carrying the window a drag
      // just asked for, so a callback that always returned the props fed the props back in and undid
      // the zoom as it was made. Hence the flag rather than a `??`: it distinguishes "uPlot is handing
      // me a zoom" from "uPlot is handing me whatever the axis happened to hold".
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
        distr: logZ.value ? 3 : 1,
        log: 10,
        auto: false,
        range: (_u, min, max) => [min ?? yBottom.value, max ?? yTop.value],
      },
      // Left auto-ranged and not what a y drag zooms (uPlot zooms the scale of the first plotted
      // series, which is the impedance curve). Neither draws gridlines: a second and third grid over
      // the ohms one turns the plot into graph paper.
      phase: { range: () => phaseRange.value },
      coh: { range: () => [0, 1] as [number, number] },
      // The ohms window inverted, on the same distribution, so the DF curve is the |Z| curve's
      // mirror when both are log and reads against a sensible window when both are linear.
    },
    axes: [
      {
        ...freqAxis(),
        ...exportAxisLabel(interactive, 'Frequency (Hz)'),
        stroke: p.axis,
        grid: { stroke: p.grid, width: p.gridWidth },
        ticks: { stroke: p.grid, width: p.gridWidth },
      },
      {
        ...ohmAxis(logZ.value),
        size: 54,
        ...exportAxisLabel(interactive, '|Z| (Ω)'),
        stroke: p.axis,
        grid: { stroke: p.grid, width: p.gridWidth },
        ticks: { stroke: p.grid, width: p.gridWidth },
      },
      {
        scale: 'phase',
        side: 1,
        show: props.showPhase,
        size: 52,
        ...exportAxisLabel(interactive, 'Phase (°)'),
        incrs: [15, 30, 45, 90, 180, 360],
        values: (_u, vals) => vals.map((v) => `${v}°`),
        stroke: p.color(PHASE_COLOR),
        grid: { show: false },
        ticks: { stroke: p.color(PHASE_COLOR), width: 1 },
      },
      {
        // The **ohms** scale, read a second way: there is one curve, and the damping factor is a
        // relabelling of it rather than a line of its own. See impedance/dampingFactorAxis.ts for
        // why a reciprocal cannot be mirrored onto a linear axis, which is what this replaces.
        scale: 'y',
        side: 1,
        show: dampingFactorOn.value,
        size: 50,
        ...exportAxisLabel(interactive, 'Damping factor'),
        // Positions in ohms, labelled with the damping factor each one is.
        splits: (u) =>
          dampingFactorTicks(u.scales.y.min ?? yBottom.value, u.scales.y.max ?? yTop.value, props.dampingFactorNominalOhms ?? 0, logZ.value),
        // Null-guarded: uPlot hands a null for any split it does not label, and a formatter that
        // dereferences it throws inside uPlot's draw — the whole plot then stays at 0 × 0, which
        // reads as "no chart" rather than as an error.
        values: (_u, vals) =>
          vals.map((v) => (v == null || !(v > 0) ? '' : formatDampingFactor((props.dampingFactorNominalOhms ?? 0) / v))),
        stroke: p.color(DAMPING_FACTOR_COLOR),
        grid: { show: false },
        ticks: { stroke: p.color(DAMPING_FACTOR_COLOR), width: 1 },
      },
      {
        scale: 'coh',
        side: 1,
        show: props.showCoherence,
        size: 44,
        ...exportAxisLabel(interactive, 'Coherence'),
        incrs: [0.1, 0.2, 0.25, 0.5],
        values: (_u, vals) => vals.map((v) => (v == null ? '' : v.toFixed(1))),
        stroke: p.color(COHERENCE_COLOR),
        grid: { show: false },
        ticks: { stroke: p.color(COHERENCE_COLOR), width: 1 },
      },
    ],
    series: buildSeries(p),
    cursor: interactive
      ? {
          points: { show: true },
          drag: { x: true, y: true, uni: 12 },
        }
      : { show: false },
    legend: { show: false },
    hooks: interactive
      ? {
          // Fires on a completed drag and nothing else — uPlot suppresses it for the selection it hides
          // afterwards — so this is the one unambiguous "the user zoomed" signal.
          setSelect: [
            (u: uPlot) => {
              if (u.select.width > 0 || u.select.height > 0) zoomed.value = true
            },
          ],
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
                entries: plottedSeries.value.map((series, i) => {
                  const value = u.data[i + 1]?.[idx]
                  return { ...series, text: value == null ? '—' : formatReading(value as number, series) }
                }),
              }
            },
          ],
        }
      : {},
    plugins: [minMaxBandPlugin(p)],
  }
}

/**
 * The chart as a PNG of exactly the requested size, drawn with the light palette whatever the theme —
 * the same arrangement as FrequencyResponseChart.exportPng, which explains it. The offscreen plot is
 * built from the same options and data as the visible one, so the min/max band, the short standard,
 * the phase and every overlay come along.
 */
async function exportPng({ width, height, captureZoom = false, primaryLabel }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header
  const light = makeChartPalette(false, true)

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const restore =
    captureZoom && plot.value ? { x: windowOf(plot.value.scales.x), y: windowOf(plot.value.scales.y) } : null

  let u: uPlot | null = null
  try {
    u = new uPlot(buildOptions(width, plotHeight, light, false), buildData(), host)
    if (restore?.x) u.setScale('x', restore.x)
    if (restore?.y) u.setScale('y', restore.y)
    await nextPaint()

    // The primary curves carry the report's name for this measurement when it shares the chart with
    // history overlays (keys `o<id>`), which already carry theirs.
    const legend: ExportLegendEntry[] = plottedSeries.value.map((s) => ({
      label: primaryLabel && !s.key.startsWith('o') ? `${primaryLabel} — ${s.label}` : s.label,
      color: light.color(s.pair),
      dash: s.dash,
    }))

    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: `${dampingFactorOn.value ? 'Output impedance' : 'Impedance'} vs frequency${logZ.value ? ' (log Ω)' : ''}`,
      legend,
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

function rebuild() {
  const el = container.value
  if (!el) return

  // A rebuild that is not itself an axis-window change hands the zoom back afterwards — a theme flip
  // or an overlay checkbox must not throw away the window the user is reading. A changed window drops
  // it instead, on *both* axes together: the panel is the user saying which window they want, and
  // restoring half a zoom would leave the plot zoomed with no button offering to undo it.
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
  const height = el.clientHeight || chartHeight.value
  plot.value = new uPlot(buildOptions(width, height, palette.value, true), buildData(), el)

  if (restore?.x) plot.value.setScale('x', restore.x)
  if (restore?.y) plot.value.setScale('y', restore.y)
}

/** setData on the existing instance — never a new plot per frame. */
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
    if (el && plot.value) plot.value.setSize({ width: el.clientWidth || 800, height: el.clientHeight || chartHeight.value })
  })
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
  plot.value?.destroy()
  plot.value = null
})

watch(signature, () => rebuild())
watch([() => props.traces, () => props.overlays], refresh)
watch(phaseRange, () => refresh())

/** Real data outside the visible ohms window, which the axis otherwise hides without a word. */
const outOfRange = computed(() => {
  const t = trace.value
  if (!t) return { above: false, below: false }

  let above = false
  let below = false
  for (let i = 0; i < t.freqHz.length; i++) {
    if (t.freqHz[i] < props.xFromHz || t.freqHz[i] > props.xToHz) continue
    if (dbOhmsToOhms(t.magnitudeMaxDbOhms[i]) > yTop.value) above = true
    if (dbOhmsToOhms(t.magnitudeMinDbOhms[i]) < yBottom.value) below = true
  }

  return { above, below }
})
</script>

<template>
  <!-- The chart is a card like every settings and display panel beside it (the 2026-10-12
       mockups): one surface, one 12 px radius, with the cursor readout and the plot inside it
       rather than the readout floating on the page ground above a bare plot. -->
  <div ref="root" class="relative flex flex-col gap-2 panel-surface rounded-xl border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="root" />
    <div class="min-h-6 pr-9 flex items-start justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <!-- One row that is the legend at rest and gains values on hover, rather than a legend and a
           separate readout saying the same names twice. Each swatch is drawn with the series' own
           colour, width and dash pattern, which is the only thing that tells two history overlays
           apart — they share the live curve's colour deliberately. -->
      <span class="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1" :class="cursorReadout ? 'font-mono' : ''">
        <span v-if="cursorReadout" class="text-neutral-800 dark:text-neutral-100">
          {{ formatHz(cursorReadout.freqHz) }}
        </span>
        <span
          v-for="entry in legendEntries"
          :key="entry.key"
          class="flex items-center gap-1.5"
        >
          <svg class="shrink-0" width="14" height="8" aria-hidden="true">
            <line
              x1="0"
              y1="4"
              x2="14"
              y2="4"
              :stroke="entry.color"
              :stroke-width="swatchWidth(entry)"
              :stroke-dasharray="swatchDash(entry)"
            />
          </svg>
          <span class="max-w-32 truncate" :title="entry.label">{{ entry.label }}</span>
          <span v-if="entry.text" class="whitespace-nowrap text-neutral-800 dark:text-neutral-100">{{ entry.text }}</span>
        </span>
        <span v-if="!cursorReadout && legendEntries.length > 0" class="text-neutral-400 dark:text-neutral-500">
          · hover for values
        </span>
      </span>

      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
        title="Back to the frequency and impedance window set in the display panel."
        @click="resetZoom"
      >
        <ToolbarIcon name="reset-zoom" />
        Reset zoom
      </button>
      <span v-else class="shrink-0">Drag to zoom · double-click to reset</span>
    </div>

    <div ref="plotBox" class="relative w-full" :style="{ height: `${chartHeight}px` }">
      <div ref="container" class="h-full w-full" @dblclick="resetZoom" />
      <div
        v-if="outOfRange.above"
        class="pointer-events-none absolute right-2 top-2 text-amber-500 dark:text-amber-400"
        title="Data above the visible impedance range"
      >
        ▲
      </div>
      <div
        v-if="outOfRange.below"
        class="pointer-events-none absolute bottom-2 right-2 text-amber-500 dark:text-amber-400"
        title="Data below the visible impedance range"
      >
        ▼
      </div>
    </div>
    <p
      v-if="traces.length === 0 && overlays.length === 0 && !shortCalOhms"
      class="text-xs text-neutral-500 dark:text-neutral-400"
    >
      No {{ dampingFactorOn ? 'output impedance' : 'impedance' }} measured yet. Press {{ dampingFactorOn ? 'Run sweep' : 'Run' }}.
    </p>
  </div>
</template>

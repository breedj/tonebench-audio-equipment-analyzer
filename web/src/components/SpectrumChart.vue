<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, toRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { STREAM_FLAG_ZOOMED, type MetersFrame, type MultitoneFrame, type SpectrumFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { SPECTRUM_DISPLAY_KEY, useChartDisplay, type SpectrumDisplaySource } from '../composables/useChartDisplay'
import {
  CHART_EXPORT_HEADER_PX,
  composeChartPng,
  nextPaint,
  axisLabel,
  type ChartExportOptions,
  type ExportLegendEntry,
} from '../charts/exportChart'
import { useZoomBounds } from '../composables/useZoomBounds'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'
import { useAppTheme } from '../composables/useAppTheme'
import { fullHeightWithMargin, useResultChartHeight } from '../charts/useResultChartHeight'
import ExpandToggle from './ExpandToggle.vue'
import ToolbarIcon from './ToolbarIcon.vue'
import { freqAxis } from '../freqAxis'
import { resample } from '../measurements/resample'
import type { OverlayEntry } from '../composables/useMeasurements'
import { FFT_LIVE_COLOR, fftOverlayColor, soleBandOverlay, spectrumAxisOverlay } from '../measurements/spectrumOverlayColors'
import { overviewSpan, spanFrom } from '../measurements/overviewSpan'
import { api, type MeasurementChannel, type WeightingCurveResponse } from '../api/client'
import { harmonicMarkerLabel, unitAxisLabel, unitOffsetDb, unitSuffix } from '../spectrumUnits'
import { useUserWeighting } from '../composables/useUserWeighting'
import { exportGridTheme } from '../charts/palette'
import {
  DEFAULT_POINT_OFFSET,
  MAX_ANNOTATION_TEXT,
  addAnnotation,
  annotationColor,
  annotationReading,
  nextAnnotationId,
  numberedAnnotations,
  removeAnnotation,
  updateAnnotation,
  type AnnotationStyle,
} from '../measurements/spectrumAnnotations'
import {
  annotationMetrics,
  boxFromDrag,
  boxHandles,
  clampBoxToWindow,
  clampLabelOffset,
  layoutAnnotations,
  type AnnotationLayoutItem,
  type AnnotationPlacement,
  type AnnotationPlot,
  type BoxHandleKind,
  type DataBox,
} from '../charts/annotationLayout'
import { peakSnapIndex } from '../measurements/spectrumPeakSnap'

const props = defineProps<{
  spectrum: Record<number, SpectrumFrame>
  meters: Record<number, MetersFrame>
  /** Each channel's multitone figure while a comb plays — its resolved gaps' highest lines are marked, the way harmonics are for a sine. */
  multitone?: Record<number, MultitoneFrame>
  /** Stored measurements loaded onto the chart, in add order — see useMeasurements.ts. */
  overlays: OverlayEntry[]
  /**
   * The block above this chart in the same column — the metrics row and the averages line —
   * that the fit should leave room for, so the readings and the chart share the view. See
   * oneLineOfMetrics for how much of it is reserved.
   */
  fitAbove?: HTMLElement | null
  /**
   * The weighting in effect, as the annunciator names it ("A-weighted", "ITU-R 468 + User 1 (x)"),
   * or null when the trace is unweighted. Shown in the header and in the PNG export's title, so a
   * weighted trace is never mistaken for a flat one. See weighting.ts's weightingLabel.
   */
  weightingLabel?: string | null
}>()

const emit = defineEmits<{
  /** The frequency range now on screen, or null when zoomed all the way out. */
  viewportChange: [viewport: { loFreqHz: number; hiFreqHz: number } | null]
}>()

// Shared singleton (see useSpectrumConfig.ts) — reads whatever SpectrumConfigPanel's L/R checkboxes
// last set, so a channel that's been turned off doesn't keep showing its last frame forever (the
// spectrum stream only ever adds entries per channel, never removes a stale one).
const { config: spectrumConfig } = useSpectrumConfig()

function channelEnabledOnScreen(ch: number): boolean {
  if (!spectrumConfig.value) return true
  return ch === 0 ? spectrumConfig.value.processLeft : spectrumConfig.value.processRight
}

// Client-side "show this trace" toggles for the two live channels, driven by the checkboxes in
// the display-options column (a sibling in the layout, see ChartDisplayPanel.vue) — deliberately
// not uPlot's built-in legend-click-to-hide. That used to toggle only uPlot's own series line,
// leaving minMaxBandPlugin's band (a separate canvas draw, unaware of uPlot's per-series `show`
// flag) painted on regardless. Routing both live-line and band visibility through these two refs
// instead of two independently-toggleable mechanisms is what keeps them from drifting out of sync
// again.
//
// The screen's shared display state, or a stored measurement type's own when a report renders this
// chart headless — see SPECTRUM_DISPLAY_KEY.
const injectedDisplay = inject(SPECTRUM_DISPLAY_KEY, null)
const display: SpectrumDisplaySource = injectedDisplay ?? useChartDisplay()
// Which style the next click places, and which label's text box is open. Transient editing state,
// so it is taken from the screen's own singleton and never from an injected source -- a headless
// report render has no mouse, and the same guard as refreshWeightingCurve below says so.
const screenState = injectedDisplay === null ? useChartDisplay() : null
const annotationArmed = screenState?.annotationArmed ?? ref<AnnotationStyle | null>(null)
const annotationEditing = screenState?.annotationEditing ?? ref<number | null>(null)
const {
  yTop,
  yBottom,
  xFrom,
  xTo,
  thickLines,
  showDataPoints,
  visibleL,
  visibleR,
  showMinMax,
  showPeakHold,
  showFftTrace,
  spectrumUnit,
  dutGainDb,
  showWeightingCurve,
  showHarmonicMarkers,
  harmonicMarkerUnit,
  annotations,
  annotationNumbers,
  activeXRange,
  resetPeakHoldTrigger,
  autofitYTrigger,
  autofitXTrigger,
  resetYDefaults,
  resetXDefaults,
} = display

// The weighting curve overlay: the weighting in effect, fetched from the server (the curves and
// the user tables live there — see /api/acquisition/weighting-curve) and drawn as a dashed line
// with its 0 dB hung WEIGHTING_CURVE_ANCHOR_DB below the top of the level range. Shape only: a
// weighting is a gain, not a level, so there is no right height for it, and a fixed one keeps it
// out of the trace's way while the range is sensible. Only the screen's own chart fetches it (a
// headless report render of a stored result would otherwise draw the *live* weighting next to a
// trace captured under another).
const WEIGHTING_CURVE_ANCHOR_DB = 20
const WEIGHTING_CURVE_COLOR = 'rgba(245, 158, 11, 0.9)'
const weightingCurve = shallowRef<WeightingCurveResponse | null>(null)
const userWeighting = useUserWeighting()
let weightingCurveRequest = 0

async function refreshWeightingCurve() {
  if (injectedDisplay !== null || !showWeightingCurve.value) {
    weightingCurve.value = null
    plot?.redraw()
    return
  }
  const request = ++weightingCurveRequest
  try {
    const curve = await api.getWeightingCurve(5, 100_000)
    if (request !== weightingCurveRequest) return
    weightingCurve.value = curve.weighted ? curve : null
  } catch {
    if (request !== weightingCurveRequest) return
    weightingCurve.value = null
  }
  plot?.redraw()
}

watch(
  () => [
    showWeightingCurve.value,
    spectrumConfig.value?.weighting?.standard,
    spectrumConfig.value?.weighting?.user1,
    spectrumConfig.value?.weighting?.user2,
    userWeighting.user1.value?.importedUtc,
    userWeighting.user2.value?.importedUtc,
  ],
  // Not immediate: `plot` is declared further down and refreshWeightingCurve touches it, so the
  // first fetch waits for onMounted.
  () => void refreshWeightingCurve(),
)

function weightingCurvePlugin(): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          const curve = weightingCurve.value
          if (!showWeightingCurve.value || !curve) return
          const anchorDb = yTop.value - WEIGHTING_CURVE_ANCHOR_DB
          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          ctx.strokeStyle = WEIGHTING_CURVE_COLOR
          ctx.lineWidth = 1.5
          ctx.setLineDash([6, 4])
          ctx.beginPath()
          let started = false
          for (let i = 0; i < curve.freqHz.length; i++) {
            const f = curve.freqHz[i]
            if (!(f > 0)) continue
            const x = u.valToPos(f, 'x', true)
            const y = u.valToPos(anchorDb + curve.gainDb[i], 'y', true)
            if (!Number.isFinite(x) || !Number.isFinite(y)) continue
            if (started) ctx.lineTo(x, y)
            else ctx.moveTo(x, y)
            started = true
          }
          ctx.stroke()

          // The anchor: a short tick and "0 dB" at the left edge, so the curve reads as a gain
          // around that line rather than as a level.
          ctx.setLineDash([])
          const yAnchor = u.valToPos(anchorDb, 'y', true)
          ctx.beginPath()
          ctx.moveTo(u.bbox.left, yAnchor)
          ctx.lineTo(u.bbox.left + 8, yAnchor)
          ctx.stroke()
          ctx.fillStyle = WEIGHTING_CURVE_COLOR
          ctx.font = '10px ui-monospace, monospace'
          ctx.fillText('0 dB', u.bbox.left + 11, yAnchor + 3)
          ctx.restore()
        },
      ],
    },
  }
}

// The chart's unit is a per-frame constant added to every dBV value at draw time — see
// spectrumUnits.ts. A live frame carries its tick's ENBW (stamped from the noise frame); a stored
// channel carries its own, so an overlay recorded with a different window converts correctly.
// Null means the value cannot be converted (no ENBW yet under a density unit): that frame is
// skipped for the tick rather than drawn in the wrong unit.
function frameOffset(frame: SpectrumFrame): number | null {
  return unitOffsetDb(spectrumUnit.value, frame.enbwHz, dutGainDb.value)
}
function channelOffset(channel: MeasurementChannel): number | null {
  return unitOffsetDb(spectrumUnit.value, channel.enbwHz, dutGainDb.value)
}
const channelEnabled = display.channelEnabled ?? channelEnabledOnScreen
function channelVisible(ch: number): boolean {
  return channelEnabled(ch) && (ch === 0 ? visibleL.value : visibleR.value)
}

// Fundamental/harmonic markers only mean something relative to a single fundamental tone —
// Multitone's comb and White noise have none, so the peak-bin search behind ch0.harmonics would
// just label whichever tone (or noise bin) happens to read loudest that tick.
const { kind: stimulusKind } = useGeneratorStimulus()

const CHANNEL_LABELS = ['L', 'R']
// Color now identifies *which measurement* a trace is — the live capture, or one of up to
// MAX_VISIBLE_OVERLAYS history items (see spectrumOverlayColors.ts for the fixed palette and why
// it's fixed rather than tinted). Channel is told apart by dash instead: L solid, R dashed, the
// same split ThdSweepChart uses for its own live L/R.
const LIVE_COLOR = FFT_LIVE_COLOR
const CHANNEL_DASH: (number[] | undefined)[] = [undefined, [6, 3]]
// Deliberately not a shade of either run color — peak-hold needs to read as its own thing at a
// glance, not a dimmer echo of whichever trace it belongs to. yellow-600 rather than yellow-500
// (Joost, 2026-09-29): a 1 px dashed yellow-500 line was hard to follow over the light theme's white
// plot, and it is the trace most often looked at on its own, with `FFT data` unchecked. Still light
// enough to read against the dark theme's near-black well, and it is this constant the export legend
// and the report's light palette take too.
const PEAK_HOLD_COLOR = '#ca8a04'
// The min/max band is live-only (see minMaxBandPlugin — it never reads overlay data) and is drawn
// as a filled stroke per column rather than a uPlot series, so it can't carry a dash the way the
// line above it now does. It keeps its own two-hue blue/orange distinction rather than switching to
// LIVE_COLOR for both, which is the only way left to tell an overlapping L band from an R band.
const CHANNEL_BAND_COLORS = ['rgba(59, 130, 246, 0.35)', 'rgba(249, 115, 22, 0.35)']
// A stored overlay's band, when it earns one, is drawn in that run's own (opaque) colour with
// this alpha instead — the live colours bake 0.35 in, and an overlay's comes from
// fftOverlayColor, which has no translucent form. A shade lighter than the live band so the
// two are still told apart where they cross.
const OVERLAY_BAND_ALPHA = 0.3
// Peak-hold: running per-bin maximum, keyed by that bin's column frequency (as broadcast — stable
// across ticks for a given FFT size/zoom window, since the server's log-decimation always picks
// the same center bin for the same range/budget). Cleared only by the Reset button (see
// resetPeakHoldTrigger) — switching FFT size etc. just leaves old keys unused rather than an
// explicit clear, since the new frequency grid won't match them anyway.
const peakHoldMaps: [Map<number, number>, Map<number, number>] = [new Map(), new Map()]
// Series layout: x, then L, R, L peak, R peak (always present, live — the peak pair just fills
// null when Peak-hold is off, same convention as an idle/hidden L or R), then two slots per
// overlay (its L and R channel, in add order) — see buildOptions and every setData call below.
// The overlay portion's
// *length* changes only when an overlay is added/removed (ensurePlot rebuilds for that, preserving
// zoom explicitly — see ensurePlot), never on a streaming tick or a visibility toggle, which is
// what keeps this from repeating the old channelCount-keyed rebuild-every-frame bug.

// uPlot draws axis labels, ticks and grid onto a canvas, so Tailwind's `dark:` classes can't reach
// them — left unset it falls back to its own near-black defaults, which are unreadable on the dark
// background. These are canvas colours, chosen to match the surrounding Tailwind neutral palette:
// neutral-400/neutral-700 on dark, neutral-600/neutral-200 on light.
const THEME = {
  dark: {
    axis: '#a3a3a3',
    grid: '#404040',
    gridWidth: 1,
    markerLine: 'rgba(234, 179, 8, 0.45)',
    markerText: 'rgba(250, 204, 21, 0.95)',
    annotationLine: 'rgba(148, 163, 184, 0.8)',
    annotationText: '#e5e5e5',
    annotationFill: 'rgba(27, 30, 35, 0.92)',
    annotationBorder: '#525252',
    annotationShadow: 'rgba(0, 0, 0, 0.55)',
  },
  light: {
    axis: '#525252',
    grid: '#e5e5e5',
    gridWidth: 1,
    markerLine: 'rgba(234, 179, 8, 0.5)',
    markerText: 'rgba(161, 98, 7, 0.95)',
    annotationLine: 'rgba(82, 82, 82, 0.75)',
    annotationText: '#171717',
    annotationFill: 'rgba(255, 255, 255, 0.93)',
    annotationBorder: '#d4d4d4',
    annotationShadow: 'rgba(15, 23, 42, 0.3)',
  },
} as const

// Canvas colours can't follow Tailwind's `dark:` classes (see THEME's own remarks above), so this
// reads the same Auto/Light/Dark state as everything else — see useAppTheme.ts.
const { isDark } = useAppTheme()
type Theme = (typeof THEME)[keyof typeof THEME]
// The screen's theme; the export builds a light plot and hands its theme in as an argument rather
// than reading this at draw time — see charts/palette.ts for why.
function currentTheme(): Theme {
  return isDark.value ? THEME.dark : THEME.light
}

const rootRef = ref<HTMLElement | null>(null)
const containerRef = ref<HTMLDivElement>()
// Fitted to the view — see charts/useResultChartHeight.ts. uPlot sizes the container itself, so a
// new height is applied through setSize, the same call a width change makes in handleResize.
//
// The metrics row above the chart is reserved at ONE line of tiles, however many lines it wraps
// to: the first line is what a glance at the screen reads together with the chart, and a wall of
// metrics must not squeeze the chart into a strip — the row scrolls, the chart does not shrink
// past it. The averages line under the row is one line already and is taken whole.
function oneLineOfMetrics(block: HTMLElement): number {
  const row = block.querySelector<HTMLElement>('[data-metrics-row]')
  if (!row) return fullHeightWithMargin(block)
  const tiles = [...row.children] as HTMLElement[]
  const firstTop = tiles[0]?.offsetTop
  const firstLine = tiles.filter((t) => t.offsetTop === firstTop).reduce((h, t) => Math.max(h, t.offsetHeight), 0)
  return fullHeightWithMargin(block) - row.offsetHeight + firstLine
}
const chartHeight = useResultChartHeight(rootRef, containerRef, {
  above: toRef(props, 'fitAbove'),
  aboveHeight: oneLineOfMetrics,
})
const cursorReadout = ref<{ freqHz: number; dbv: number } | null>(null)

// uPlot instance is created exactly once and reused via setData()/setSize() for the component's
// whole lifetime — re-instantiating per frame is the single biggest perf mistake for a
// streaming chart like this.
let plot: uPlot | null = null
// The full-span edges, taken only from overview frames. A zoomed frame's x0/dx describe the zoom
// window, so sizing the axis from one would shrink the axis to the zoom and make zooming out
// impossible. See STREAM_FLAG_ZOOMED.
let fullLoFreqHz = 10
let fullHiFreqHz = -1
let plotNyquistHz = -1
// Axis colours are baked into the options at construction, so a theme flip is one of the few
// things that legitimately requires rebuilding the instance.
let plotIsDark = isDark.value

// The server sends min *and* max per decimated column; plotting only the max threw away half of
// what decimation deliberately preserved, and made our noise floor read as a thin line ~6 dB above
// where QuantAsylum's app draws a thick band. QA403 draws every bin, so each screen column becomes a
// vertical stroke from that column's min to its max — this reproduces that directly from the min/max
// pair rather than trying to fake it from the max alone.
//
// Drawn in `drawAxes` (fires after the grid, before the series) so the band sits above the gridlines
// and under each channel's max trace. One Path2D per channel, stroked once — 1600 columns × 2
// channels per frame is fine as a single batched path, but not as 3200 individual stroke calls.
function minMaxBandPlugin(
  getSpectrum: () => Record<number, SpectrumFrame>,
  getOverlays: () => OverlayEntry[],
): uPlot.Plugin {
  return {
    hooks: {
      drawAxes: [
        (u) => {
          if (!showMinMax.value) return
          const spectrum = getSpectrum()
          const ctx = u.ctx
          ctx.save()

          // Clip to the plot area: a column whose min falls below the y-scale would otherwise
          // paint over the axis labels.
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          for (let ch = 0; ch < CHANNEL_LABELS.length; ch++) {
            if (!channelVisible(ch)) continue
            const frame = spectrum[ch]
            if (!frame) continue
            const offset = frameOffset(frame)
            if (offset === null) continue

            const path = new Path2D()
            const { columns, header } = frame
            for (let i = 0; i < header.nPoints; i++) {
              const freqHz = columns[i * 3]
              const minDb = columns[i * 3 + 1] + offset
              const maxDb = columns[i * 3 + 2] + offset
              // Column 0 can sit at or below the log x-scale minimum, where valToPos is -Infinity.
              if (!(freqHz > 0)) continue

              const x = u.valToPos(freqHz, 'x', true)
              path.moveTo(x, u.valToPos(minDb, 'y', true))
              path.lineTo(x, u.valToPos(maxDb, 'y', true))
            }

            ctx.strokeStyle = CHANNEL_BAND_COLORS[ch]
            ctx.lineWidth = 1
            ctx.stroke(path)
          }

          // And the stored overlay's band, when it is the only one on screen — see
          // soleBandOverlay for why only then, and why it is worth drawing at all. In that run's
          // own colour rather than the live blue/orange, because here hue identifies the *run*;
          // both its channels share it, so an overlay with L and R on draws two bands the same
          // colour, which is the price of not spending hue on channel.
          const overlays = getOverlays()
          const banded = soleBandOverlay(overlays)
          if (banded) {
            ctx.globalAlpha = OVERLAY_BAND_ALPHA
            ctx.strokeStyle = fftOverlayColor(overlays, banded.id)
            ctx.lineWidth = 1

            for (let ch = 0; ch < CHANNEL_LABELS.length; ch++) {
              const channel = channelByIndex(banded.detail, ch)
              if (!channel) continue
              const offset = channelOffset(channel)
              if (offset === null) continue

              const path = new Path2D()
              for (const column of channel.columns) {
                if (!(column.freqHz > 0)) continue
                const x = u.valToPos(column.freqHz, 'x', true)
                path.moveTo(x, u.valToPos(column.minDbv + offset, 'y', true))
                path.lineTo(x, u.valToPos(column.maxDbv + offset, 'y', true))
              }
              ctx.stroke(path)
            }
          }

          ctx.restore()
        },
      ],
    },
  }
}

// Reporting the visible range upstream is what lets the server spend its whole column budget on
// what's actually on screen. Debounced and change-gated because uPlot fires setScale on every
// setData too (the x scale is auto:false, so setData re-applies the same min/max every frame) —
// without both guards this would emit 20 times a second saying nothing new.
let viewportTimer: ReturnType<typeof setTimeout> | undefined
// Seeded `undefined`, not NaN: NaN is what a full-span report *is* below, so seeding it meant a
// fresh plot's first report of "not zoomed" was deduplicated away as already said. It had not been
// said — the viewport is remembered per *client* on the server and this component is remounted by
// every measurement switch, so after a zoom the server went on serving the old window and the
// chart came back holding that slice alone, with no gesture left to correct it. The other half of
// the same bug is in overviewSpan.ts's spanFrom.
let lastReportedLo: number | undefined
let lastReportedHi: number | undefined

function reportViewport(u: uPlot) {
  const min = u.scales.x.min
  const max = u.scales.x.max
  if (min == null || max == null) return

  // Published before (and independently of) the server-facing viewport below: the spectrogram
  // stacked underneath has to track *every* x change, zoomed or not and debounced or not, or its
  // columns stop lining up with the spectrum's for 150 ms after each one. See useChartDisplay.ts.
  if (activeXRange.value?.min !== min || activeXRange.value?.max !== max)
    activeXRange.value = { min, max }

  if (fullHiFreqHz <= 0) return

  // Treat "essentially the whole span" as not zoomed, so nudging the edges doesn't leave the
  // server encoding a private frame that buys nothing.
  const atFullSpan = min <= fullLoFreqHz * 1.001 && max >= fullHiFreqHz * 0.999
  const lo = atFullSpan ? Number.NaN : min
  const hi = atFullSpan ? Number.NaN : max
  if (Object.is(lo, lastReportedLo) && Object.is(hi, lastReportedHi)) return

  lastReportedLo = lo
  lastReportedHi = hi

  clearTimeout(viewportTimer)
  viewportTimer = setTimeout(
    () => emit('viewportChange', atFullSpan ? null : { loFreqHz: min, hiFreqHz: max }),
    150,
  )
}

/**
 * The multitone markers: a short tick at the highest line of every resolved gap of the first drawn
 * channel, labelled with its level re the comb. Ticks from the bottom rather than full-height lines
 * — a 60-tone comb has 59 gaps, and 59 full lines would be a curtain over the trace.
 */
function drawMultitoneMarkers(u: uPlot, theme: Theme) {
  const frame = props.multitone?.[0] ?? props.multitone?.[1]
  if (!frame) return

  const ctx = u.ctx
  ctx.save()
  ctx.strokeStyle = theme.markerLine
  ctx.fillStyle = theme.markerText
  ctx.font = '10px ui-monospace, monospace'
  ctx.lineWidth = 1
  const bottom = u.bbox.top + u.bbox.height
  const tickHeight = Math.min(28, u.bbox.height * 0.12)

  let lastLabelX = -Infinity
  for (const band of frame.bands) {
    if (!band.resolved || band.highestFreqHz <= 0) continue
    const x = u.valToPos(band.highestFreqHz, 'x', true)
    if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) continue

    ctx.beginPath()
    ctx.moveTo(x, bottom)
    ctx.lineTo(x, bottom - tickHeight)
    ctx.stroke()
    // Labels only where they fit: adjacent gaps at the low end are a few pixels apart on a log axis.
    if (x - lastLabelX >= 34) {
      ctx.fillText(`${band.highestDbr.toFixed(0)}`, x + 2, bottom - tickHeight - 3)
      lastLabelX = x
    }
  }

  ctx.restore()
}

/**
 * The annotations the user has drawn on this chart — see measurements/spectrumAnnotations.ts for the
 * model and charts/annotationLayout.ts for the geometry. This is only the painting and the mouse.
 *
 * <p>Drawn in `draw`, i.e. over the series: a label under a dense trace cannot be read. Unlike its
 * sibling plugins this one takes no `interactive` flag, and that is deliberate — it registers no
 * hook an export would not want, because everything a mouse touches is a DOM handle above the
 * canvas (see annotationPlacements below). So the offscreen export plot gets exactly this plugin,
 * whole, and the labels land in the report's PNG, the Screenshot dialog and the black-and-white
 * print with no further code.</p>
 */
function annotationFont(px: number): string {
  return `${px}px ui-sans-serif, system-ui, sans-serif`
}

/**
 * The constant this chart is adding to every dBV value right now, which is what converts a stored
 * annotation's level to the one on screen: the live frame's, an overlay's where there is no live
 * frame, and the unit's own answer where there is neither. Null when the unit cannot be converted at
 * all, which the layout reads as "do not draw this point" — exactly as computeFrame skips a frame it
 * cannot convert, rather than drawing it in the wrong unit.
 */
function annotationLevelOffsetDb(): number | null {
  const live = props.spectrum[0] ?? props.spectrum[1]
  if (live) return frameOffset(live)
  for (const overlay of props.overlays) {
    if (!overlay.visible) continue
    const channel = overlay.detail.channelsData[0]
    if (channel) return channelOffset(channel)
  }
  return unitOffsetDb(spectrumUnit.value, undefined, dutGainDb.value)
}

/** The annotations with their levels in the unit the chart is plotting, ready to be laid out. */
function annotationItems(): AnnotationLayoutItem[] {
  const offset = annotationLevelOffsetDb()
  return numberedAnnotations(annotations.value).map((a) => ({
    id: a.id,
    number: a.number,
    style: a.style,
    text: a.text,
    freqHz: a.freqHz,
    // A point's marker and a box's top edge are both levels; a frequency rule has none, and under
    // a density unit with no ENBW nothing can be converted and the layout draws no level at all.
    levelDb: a.style !== 'frequency' && a.levelDbv !== null && offset !== null ? a.levelDbv + offset : null,
    freqHz2: a.style === 'box' ? a.freqHz2 : null,
    levelDb2: a.style === 'box' && a.levelDbv2 !== null && offset !== null ? a.levelDbv2 + offset : null,
    labelOffsetOctaves: a.labelOffsetOctaves,
    labelOffsetDb: a.labelOffsetDb,
  }))
}

/** This plot's pixels per CSS pixel — uPlot draws in device pixels, the DOM handles live in CSS ones. */
function plotPixelScale(u: uPlot): number {
  const css = u.over.clientWidth
  return css > 0 ? u.bbox.width / css : 1
}

function measureAnnotationText(u: uPlot): (text: string, fontPx: number) => number {
  return (text, fontPx) => {
    u.ctx.font = annotationFont(fontPx)
    return u.ctx.measureText(text).width
  }
}

/** The adapter the canvas draws through: device pixels, positions including the plot's own offset. */
function canvasAnnotationPlot(u: uPlot): AnnotationPlot {
  return {
    left: u.bbox.left,
    top: u.bbox.top,
    width: u.bbox.width,
    height: u.bbox.height,
    xToPos: (freqHz) => u.valToPos(freqHz, 'x', true),
    yToPos: (levelDb) => u.valToPos(levelDb, 'y', true),
    scale: plotPixelScale(u),
    measureText: measureAnnotationText(u),
  }
}

/**
 * The adapter the DOM handles use: CSS pixels relative to the plotting area's own top-left, which is
 * where the overlay div is positioned — so a handle lands exactly on the label the canvas drew.
 */
function cssAnnotationPlot(u: uPlot): AnnotationPlot {
  const scale = plotPixelScale(u)
  return {
    left: 0,
    top: 0,
    width: u.bbox.width / scale,
    height: u.bbox.height / scale,
    xToPos: (freqHz) => u.valToPos(freqHz, 'x', false),
    yToPos: (levelDb) => u.valToPos(levelDb, 'y', false),
    scale: 1,
    measureText: measureAnnotationText(u),
  }
}

function roundedRectPath(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
  ctx.beginPath()
  // roundRect is Chromium 99+; the plain rect is the same fallback bandwidthPlugin carries.
  if (typeof ctx.roundRect === 'function') ctx.roundRect(x, y, w, h, r)
  else ctx.rect(x, y, w, h)
}

/**
 * Bumped whenever the plot's geometry or its window changes, so the DOM handles follow a zoom, a pan
 * and a resize. Written from the draw hook below, which is the one place that runs on every commit —
 * and gated on an actual change, so a streaming tick does not re-render the handles twenty times a
 * second. Writing a counter from `draw` and reading it in a render schedules a re-render, not
 * another draw, so there is no loop here however much it looks like one.
 */
const plotGeometryTick = ref(0)
let lastPlotGeometry = ''
function noteGeometry(u: uPlot) {
  const key = `${u.bbox.left},${u.bbox.top},${u.bbox.width},${u.bbox.height},${u.scales.x.min},${u.scales.x.max},${u.scales.y.min},${u.scales.y.max}`
  if (key === lastPlotGeometry) return
  lastPlotGeometry = key
  plotGeometryTick.value++
}

function annotationsPlugin(theme: Theme): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          noteGeometry(u)
          const items = annotationItems()
          if (items.length === 0) return

          const adapter = canvasAnnotationPlot(u)
          const placements = layoutAnnotations(items, adapter, annotationNumbers.value)
          const ctx = u.ctx
          ctx.save()
          ctx.beginPath()
          ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height)
          ctx.clip()

          // Pass one: every region. Drawn before any label so one annotation's translucent fill can
          // never bury another's note, and never as uPlot's own `.u-select` div — the report, the
          // Screenshot dialog and the black-and-white print only ever see exportPng's canvas.
          for (const p of placements) {
            if (!p.visible || !p.box) continue
            const colour = annotationColor(p.number)
            roundedRectPath(ctx, p.box.x, p.box.y, p.box.w, p.box.h, p.fontPx * 0.25)
            ctx.fillStyle = colour + '1a'
            ctx.fill()
            // Solid, not dashed: the dash belongs to the frequency rule, which still exists on
            // measurements saved before the picker stopped offering it. Set explicitly rather than
            // assumed -- this is the first stroke the plugin makes, so it inherits whatever dash the
            // plugin drawn before it left on the context, and it came out dotted until it said so.
            ctx.setLineDash([])
            ctx.strokeStyle = colour
            ctx.lineWidth = 1.5 * adapter.scale
            ctx.stroke()
          }

          for (const p of placements) {
            if (!p.visible) continue

            // A frequency annotation's rule, in the response chart's pinned-cursor dash so the two
            // read as one family of "someone marked this".
            if (p.rule) {
              ctx.setLineDash([5, 4])
              ctx.strokeStyle = theme.annotationLine
              ctx.lineWidth = 1.5 * adapter.scale
              ctx.beginPath()
              ctx.moveTo(p.rule.x, p.rule.y1)
              ctx.lineTo(p.rule.x, p.rule.y2)
              ctx.stroke()
              ctx.setLineDash([])
            }

            if (p.leader) {
              ctx.strokeStyle = theme.annotationLine
              ctx.lineWidth = adapter.scale
              ctx.beginPath()
              ctx.moveTo(p.leader.x1, p.leader.y1)
              ctx.lineTo(p.leader.x2, p.leader.y2)
              ctx.stroke()
            }

            if (p.anchor) {
              ctx.fillStyle = theme.annotationLine
              ctx.beginPath()
              ctx.arc(p.anchor.x, p.anchor.y, p.anchor.r, 0, Math.PI * 2)
              ctx.fill()
            }

            // The box carries a small drop shadow, so a label over a dense trace reads as sitting
            // above the picture rather than being part of it. Only the fill: the shadow is cleared
            // before the border, or every stroke after it would trail one too.
            roundedRectPath(ctx, p.label.x, p.label.y, p.label.w, p.label.h, p.fontPx * 0.35)
            ctx.shadowColor = theme.annotationShadow
            ctx.shadowBlur = 4 * adapter.scale
            ctx.shadowOffsetY = adapter.scale
            ctx.fillStyle = theme.annotationFill
            ctx.fill()
            ctx.shadowColor = 'transparent'
            ctx.shadowBlur = 0
            ctx.shadowOffsetY = 0
            ctx.strokeStyle = theme.annotationBorder
            ctx.lineWidth = adapter.scale
            ctx.stroke()

            if (p.badge) {
              // The ordinal's own colour, matching its row in the Annotations panel -- see
              // annotationColor. A tinted disc rather than a ring alone: at this size a 1 px
              // coloured outline is nearly invisible over a busy trace.
              const colour = annotationColor(p.number)
              ctx.fillStyle = colour + '26'
              ctx.beginPath()
              ctx.arc(p.badge.x, p.badge.y, p.badge.r, 0, Math.PI * 2)
              ctx.fill()
              ctx.strokeStyle = colour
              ctx.stroke()
              ctx.fillStyle = colour
              ctx.font = annotationFont(p.fontPx * 0.78)
              ctx.textAlign = 'center'
              ctx.textBaseline = 'middle'
              ctx.fillText(`${p.number}`, p.badge.x, p.badge.y)
            }

            if (p.text) {
              ctx.fillStyle = theme.annotationText
              ctx.font = annotationFont(p.fontPx)
              ctx.textAlign = 'left'
              ctx.textBaseline = 'middle'
              ctx.fillText(p.text, p.textX, p.textY)
            }
          }

          ctx.restore()
        },
      ],
    },
  }
}

/**
 * Which live series the cursor readout and a click both read: the channel that is drawn, and its
 * live line or — with `FFT data` off — its peak-hold trace, which is the only curve left under the
 * cursor then. Series layout is x, L, R, L peak, R peak (see buildOptions), so the live line is
 * `1 + channel` and its peak-hold `3 + channel`.
 *
 * <p>One helper rather than two copies, because the readout and an annotation placed by clicking
 * have to agree about the level of the column they are both looking at: a label that recorded a
 * different figure from the one the readout was showing at the moment of the click would be wrong
 * about the chart it is drawn on.</p>
 */
function readoutSeries(u: uPlot): { channel: number; values: (number | null | undefined)[] } | null {
  const channel = channelVisible(0) ? 0 : channelVisible(1) ? 1 : null
  if (channel === null) return null
  const values = u.data[(showFftTrace.value ? 1 : 3) + channel] as (number | null | undefined)[] | undefined
  return values ? { channel, values } : null
}

/** The column the cursor is obviously aimed at — see measurements/spectrumPeakSnap.ts. */
function snapToPeak(u: uPlot, idx: number): number {
  const xs = u.data[0]
  const read = readoutSeries(u)
  if (!read) return idx
  return peakSnapIndex({
    count: xs.length,
    posAt: (i) => u.valToPos(xs[i] as number, 'x', false),
    valueAt: (i) => (xs[i] == null ? null : read.values[i] ?? null),
    cursorPx: u.cursor.left ?? -1,
    startIdx: idx,
  })
}

function harmonicMarkersPlugin(theme: Theme, interactive: boolean): uPlot.Plugin {
  const drawHook = (u: uPlot) => {
    // A comb has no single fundamental to hang harmonics on; its markers are the highest line of
    // each resolved gap between tones, labelled in dBr — the multitone table's "highest above".
    if (stimulusKind.value === 'Multitone') {
      drawMultitoneMarkers(u, theme)
      return
    }
    if (stimulusKind.value !== 'Tones') return
    // The display panel's "Harmonic markers". Only the harmonic lines: a comb's gap markers above
    // are the multitone table's own reading, not harmonics of anything, and have no checkbox.
    if (!showHarmonicMarkers.value) return
    const ch0 = props.meters[0]
    if (!ch0 || ch0.harmonics.length === 0) return

    const ctx = u.ctx
    ctx.save()
    ctx.strokeStyle = theme.markerLine
    ctx.fillStyle = theme.markerText
    ctx.font = '10px ui-monospace, monospace'
    ctx.lineWidth = 1

    for (const h of ch0.harmonics) {
      if (h.freqHz <= 0) continue
      const x = u.valToPos(h.freqHz, 'x', true)
      if (x < u.bbox.left || x > u.bbox.left + u.bbox.width) continue

      ctx.beginPath()
      ctx.moveTo(x, u.bbox.top)
      ctx.lineTo(x, u.bbox.top + u.bbox.height)
      ctx.stroke()
      // The line is drawn whatever the label says: it marks where the order falls, which is the
      // half of the marker that cannot be missing. Only the text waits on a fundamental.
      const label = harmonicMarkerLabel(harmonicMarkerUnit.value, h.dbc, ch0.fundamentalDbv)
      if (label !== null) ctx.fillText(label, x + 3, u.bbox.top + 10)
    }

    ctx.restore()
  }

  // The offscreen export plot draws the markers and nothing else: no viewport to report, no zoom
  // to note, no readout to feed.
  if (!interactive) return { hooks: { draw: [drawHook] } }

  return {
    hooks: {
      draw: [drawHook],
      setScale: [
        (u, key) => {
          if (key === 'x') {
            reportViewport(u)
            noteScaleIfReady(u)
          }
        },
      ],
      setCursor: [
        (u) => {
          const idx = u.cursor.idx
          const xs = u.data[0]
          const read = readoutSeries(u)
          if (idx == null || read === null || xs[idx] == null || read.values[idx] == null) {
            cursorReadout.value = null
            return
          }
          const snapped = snapToPeak(u, idx)
          cursorReadout.value = { freqHz: xs[snapped] as number, dbv: read.values[snapped] as number }
        },
      ],
    },
  }
}

function formatDbTick(v: number | null): string | null {
  return v == null ? null : `${v}`
}

/**
 * @param theme Which theme to build with — see currentTheme.
 * @param interactive False for the export's offscreen plot: no cursor, and none of the hooks that
 *   report the viewport, note the zoom or feed the readout.
 */
function buildOptions(
  width: number,
  height: number,
  overlays: OverlayEntry[],
  theme: Theme,
  interactive: boolean,
): uPlot.Options {
  const liveWidth = thickLines.value ? 3 : 1.5
  const overlayWidth = thickLines.value ? 2.5 : 1.25
  const points = { show: showDataPoints.value }
  const series: uPlot.Series[] = [
    {},
    { label: CHANNEL_LABELS[0], stroke: LIVE_COLOR, width: liveWidth, dash: CHANNEL_DASH[0] && [...CHANNEL_DASH[0]], points },
    { label: CHANNEL_LABELS[1], stroke: LIVE_COLOR, width: liveWidth, dash: CHANNEL_DASH[1] && [...CHANNEL_DASH[1]], points },
    { label: `${CHANNEL_LABELS[0]} peak`, stroke: PEAK_HOLD_COLOR, width: 1, dash: [2, 2], points: { show: false } },
    { label: `${CHANNEL_LABELS[1]} peak`, stroke: PEAK_HOLD_COLOR, width: 1, dash: [2, 2], points: { show: false } },
  ]
  // Fixed per-run color (fftOverlayColor), not per-channel — see LIVE_COLOR's own comment. `overlays`
  // here is ensurePlot's full, possibly-mixed-kind list; fftOverlayColor filters to FFT-kind and
  // visible-only internally, so a stray sweep-kind entry just gets the hidden-swatch gray back
  // (harmless — its line stays null-filled regardless, same as before this change).
  for (const overlay of overlays) {
    const color = fftOverlayColor(overlays, overlay.id)
    series.push(
      { label: `${overlay.detail.name} L`, stroke: color, width: overlayWidth, dash: CHANNEL_DASH[0] && [...CHANNEL_DASH[0]], points },
      { label: `${overlay.detail.name} R`, stroke: color, width: overlayWidth, dash: CHANNEL_DASH[1] && [...CHANNEL_DASH[1]], points },
    )
  }

  return {
    width,
    height,
    scales: {
      // auto:false — uPlot's x scale defaults to auto-ranging from each setData() call's own
      // data, which re-fits the view to the incoming frame on every one of these (up to
      // targetFrameRateHz times a second) and silently overwrites any zoom/pan the user just did.
      // With auto:false, setData() re-applies the scale's own current min/max instead (see
      // uPlot's setData: `xsc.auto(...) ? autoScaleX() : _setScale(xScaleKey, xsc.min, xsc.max)`).
      //
      // `range` (a callback), not `min`/`max` directly: the plot is first constructed with empty
      // data (see ensurePlot below), and uPlot only ever seeds a scale's live min/max from `range`
      // — static `min`/`max` fields are read *by* the default range fn, they aren't a substitute
      // for one. With no `range` callback, the built-in log-range fn hands back [null, null] for
      // zero data, and since auto:false means nothing ever recomputes it later, the x scale would
      // stay stuck at null forever — every point maps to an invalid position and the chart never
      // draws again once real data replaces the empty construction data.
      //
      // Passing dataMin/dataMax straight through (only substituting when null) matters just as
      // much as auto:false itself: setScales() unconditionally re-derives the x-series' range by
      // calling this same fn with whatever's pending — including the exact min/max a user's drag-
      // zoom just requested via uPlot's own setScale(). A version that ignored those args and
      // always returned the fixed default (tried first) fed the zoomed request straight back into
      // range() and got the full range back, silently overriding every zoom the instant it was
      // set — the same bug as before, just moved one level down.
      //
      // **The null branch is the panel's From/To, not the full span, and that is the whole of what
      // the Frequency range dropdowns do at construction.** It is the only moment the window is
      // decided: this callback runs with nulls exactly once, on the empty construction above, and
      // after that auto:false means setData only ever feeds the scale's own current min/max back
      // through here. The watcher on [xFrom, xTo] below is a *change* handler, so a window chosen
      // while no plot existed -- which is the ordinary case, since the plot is first built by the
      // frame RUN produces -- never reached uPlot at all. Seeded at 10..Nyquist the chart then
      // reported that as its viewport, the server spent its column budget on the full span, and
      // the data span fed straight back in: self-consistent, and 10 Hz to 96 kHz whatever the
      // panel said. Reported as "when FFT starts the frequency axis does not look at the set
      // Frequency range -- it resets to about 90 kHz when Run is pressed".
      x: { time: false, distr: 3, log: 10, auto: false, range: (_u, dataMin, dataMax) => [dataMin ?? xFrom.value, dataMax ?? xTo.value] },
      // Initial range only — the dBV range fields in the display-options column (ChartDisplayPanel)
      // adjust it live afterward via the watcher below, without rebuilding the plot.
      y: { range: [yBottom.value, yTop.value] },
    },
    axes: [
      {
        // Ticks and labels come from freqAxis.ts, shared with the spectrogram stacked underneath so
        // the two axes agree column for column — see the reasoning there for why uPlot's own
        // log-axis ticking isn't usable once you zoom in.
        ...freqAxis(),
        ...axisLabel(true, interactive, 'Frequency (Hz)'),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: theme.gridWidth },
        ticks: { stroke: theme.grid, width: theme.gridWidth },
      },
      {
        values: (_u, vals) => vals.map(formatDbTick),
        size: 50,
        ...axisLabel(true, interactive, unitAxisLabel(spectrumUnit.value)),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: theme.gridWidth },
        ticks: { stroke: theme.grid, width: theme.gridWidth },
      },
    ],
    series,
    cursor: interactive
      ? {
          points: { show: false },
          // uPlot's own defaults already give exactly this, but the box gesture *depends* on the
          // resting state being x-only -- syncBoxDragMode flips `y` and `setScale` and must have
          // something known to flip back to. A silent dependence on a vendored library's defaults
          // is what these comments exist to prevent. `uni` is deliberately left unset: its default
          // is null, which is what makes an armed drag sweep a true rectangle rather than
          // collapsing to one axis the way the response and impedance charts' `uni: 12` does.
          drag: { x: true, y: false },
        }
      : { show: false },
    // uPlot's built-in legend doubles as a click-to-hide-series control — that's what caused a
    // channel's band to keep drawing after its line was hidden (the band plugin didn't know about
    // uPlot's internal per-series `show`). Visibility is now this component's own checkboxes
    // (live L/R) and the overlay list (each stored trace), both driving null-fill instead of
    // uPlot's `show`, so there's exactly one place that decides what's visible.
    legend: { show: false },
    plugins: [
      minMaxBandPlugin(() => props.spectrum, () => props.overlays),
      harmonicMarkersPlugin(theme, interactive),
      weightingCurvePlugin(),
      annotationsPlugin(theme),
    ],
  }
}

// The wire payload interleaves (freqHz,min,max) per column. The uPlot series is the max — it never
// misses a harmonic spike the way a mean would (see docs/tonebench-stack-spec.md §3.1) — and it is
// what the legend and cursor readout report. The min is drawn by minMaxBandPlugin above rather than
// as a second series, which keeps one legend row per channel and the cursor on the trace people
// actually read values off.
// freqHz comes from the server per-column (never reconstructed from a spacing formula — see
// streaming/protocol.ts for why that was tried and broke at small FFT sizes).
function freqTrace(columns: Float32Array, nPoints: number): Float64Array {
  const out = new Float64Array(nPoints)
  for (let i = 0; i < nPoints; i++) out[i] = columns[i * 3]
  return out
}

/** The column maxima in the chart's unit — `offset` is frameOffset's, already known to be non-null. */
function maxTrace(columns: Float32Array, nPoints: number, offset: number): Float64Array {
  const out = new Float64Array(nPoints)
  for (let i = 0; i < nPoints; i++) out[i] = columns[i * 3 + 2] + offset
  return out
}

/** Folds this tick's column maxima into the running per-bin peak-hold map — see peakHoldMaps. */
function updatePeakHold(map: Map<number, number>, frame: SpectrumFrame): void {
  const { columns, header } = frame
  for (let i = 0; i < header.nPoints; i++) {
    const freqHz = columns[i * 3]
    const maxDb = columns[i * 3 + 2]
    const prev = map.get(freqHz)
    if (prev === undefined || maxDb > prev) map.set(freqHz, maxDb)
  }
}

/**
 * The peak-hold map resampled onto the active x grid — a plain lookup, not resample()'s
 * interpolation, since a bin the map hasn't seen yet (a fresh key from a just-changed zoom/FFT
 * size) has no peak to interpolate from; it just stays null until this tick's updatePeakHold has
 * a chance to populate it.
 */
function peakTrace(map: Map<number, number>, xs: Float64Array | number[], offset: number): (number | null)[] {
  const out = new Array<number | null>(xs.length)
  for (let i = 0; i < xs.length; i++) {
    const v = map.get(xs[i])
    // The maps hold dBV (what the frames carry); the unit is applied on the way out, like the
    // live trace's, so a unit switch does not need the maps rebuilt -- the owner clears them anyway.
    out[i] = v === undefined ? null : v + offset
  }
  return out
}

// Deliberately not keyed on the frame's point count or on channelCount: nothing in buildOptions
// depends on either, and including the point count meant a zoom window with fewer bins than
// columns tore the plot down and rebuilt it — losing the very zoom that produced the frame.
// setData handles a changing length by itself.
//
// The overlay list *does* rebuild the plot (its series count is baked into buildOptions, unlike
// L/R which are always present) — on an explicit add/remove, and now also on a visibility toggle:
// fftOverlayColor's color assignment is a *position among the currently visible* FFT overlays (see
// its own comment for why), so showing or hiding one can change what color every other visible one
// should draw in, and that's baked into the series at construction the same as the color itself.
// None of this happens per streaming tick, so it's rare enough that the rebuild cost doesn't
// matter. What does matter is not silently discarding the user's zoom every time they add one more
// overlay to compare — the min/max is saved before destroy and reapplied after, same idea as
// resetZoom below just round-tripped through a rebuild instead of a click.
let plotOverlaySignature = ''
function overlaySignature(overlays: OverlayEntry[]): string {
  return overlays.map((o) => `${o.id}:${o.visible ? 1 : 0}`).join(',')
}
// Set by ensurePlot when a rebuild needs to reapply the zoom the user had before it; consumed by
// update() right after its own setData() call. It can't be applied inside ensurePlot itself: uPlot
// defers a setScale() call's effect (confirmed live — reading u.scales.x right after setScale still
// showed the pre-call value), so calling it before update()'s subsequent setData() just gets that
// deferred change clobbered by setData()'s own scale reapplication, which runs synchronously.
let pendingZoomRestore: { min: number; max: number } | null = null
// Thick-lines/show-data-points are baked into each series at construction (uPlot has no live
// setter for stroke width or point visibility), so — like the overlay signature above — a change
// has to go through the same destroy/recreate ensurePlot already does for those, not a setData.
let plotStyleKey = ''
function styleKey(): string {
  // The unit is in the axis label, which is baked into the options: a switch rebuilds the plot.
  return `${thickLines.value}|${showDataPoints.value}|${spectrumUnit.value}`
}

// nyquistHz is a rebuild *key*, not something buildOptions reads any more — the x window comes
// from the panel now, and nothing else in the options depends on the analysed span. It is kept
// because a sample-rate change is still a change of what the plot is drawing, and the rebuild is
// rare, cheap and round-trips the current window through savedScale.
function ensurePlot(nyquistHz: number, overlays: OverlayEntry[]) {
  const el = containerRef.value
  if (!el) return

  const signature = overlaySignature(overlays)
  const style = styleKey()
  if (plot && plotNyquistHz === nyquistHz && plotIsDark === isDark.value && plotOverlaySignature === signature && plotStyleKey === style)
    return

  const savedScale = plot ? { min: plot.scales.x.min, max: plot.scales.x.max } : null
  plot?.destroy()
  const width = el.getBoundingClientRect().width || 800
  // Same AlignedData-typing wrinkle as setData below: a literal tuple like `[[], [], []]` gets
  // contextually typed against uPlot's tuple type, but a dynamically-sized array built via
  // Array.from can't be, since its length (and therefore its tuple shape) isn't known until
  // runtime. The runtime shape is fine — every slot is a plain empty array — only the static type
  // needs the assertion.
  const dataSlots = 5 + overlays.length * 2
  const emptyData = Array.from({ length: dataSlots }, () => [] as number[]) as unknown as uPlot.AlignedData
  plot = new uPlot(buildOptions(width, chartHeight.value, overlays, currentTheme(), true), emptyData, el)

  // uPlot's own double-click handler calls autoScaleX(), which fits the axis to the *data* extent.
  // While a zoomed frame is on screen that extent is the zoom window, so its reset would be a
  // no-op and there'd be no way back out. Ours runs after it (added later on the same element) and
  // restores the real full span.
  plot.over.addEventListener('dblclick', resetZoom)

  // Click-to-annotate. Both listeners are no-ops unless the Annotations panel has armed the chart
  // (see onPlotClick), so an unarmed plot behaves exactly as it did before they existed — uPlot's
  // own cursor drag and the reset above are untouched.
  plot.over.addEventListener('mousedown', notePressOrigin)
  plot.over.addEventListener('click', onPlotClick)

  // A rebuild (theme, overlay count, style key) hands back a fresh instance carrying uPlot's own
  // defaults, so an armed chart would silently go back to zooming on the next drag.
  syncBoxDragMode()

  pendingZoomRestore = savedScale && savedScale.min != null && savedScale.max != null ? { min: savedScale.min, max: savedScale.max } : null

  plotNyquistHz = nyquistHz
  plotIsDark = isDark.value
  plotOverlaySignature = signature
  plotStyleKey = style
}

/**
 * "Not zoomed" here means "showing the data's own full span" — resetZoom restores fullLoFreqHz/
 * fullHiFreqHz, not the display panel's From/To (unlike the sweep and response charts). Y is never
 * actually draggable (no `drag.y` below), so its half of the check is trivially always true; it's
 * still supplied so useZoomBounds has one consistent shape across all three charts.
 */
const { zoomed, noteScale, resetZoom: resetZoomToFullSpan } = useZoomBounds(
  () => plot,
  () => ({ xMin: fullLoFreqHz, xMax: fullHiFreqHz, yMin: yBottom.value, yMax: yTop.value }),
)

function resetZoom() {
  if (fullHiFreqHz > 0) resetZoomToFullSpan()
}

/** Guards noteScale the same way resetZoom guards its own reset -- before a real span is known, fullHiFreqHz is a placeholder and would read as a bogus zoom. */
function noteScaleIfReady(u: uPlot) {
  if (fullHiFreqHz > 0) noteScale(u)
}


// --- Annotations: placing by clicking, dragging a label, editing its text -------------------

/**
 * Where the button went down, so a drag-zoom can be told from a click: a zoom gesture ends with a
 * `click` on the same element, and without this every zoom while armed would also drop a label. The
 * threshold is in CSS pixels and deliberately small — a hand is never perfectly still on a press,
 * and anything larger would swallow a very short drag. Same figure and reasoning as
 * FrequencyResponseChart's, which pins its cursor the same way.
 */
const CLICK_SLOP_PX = 4
let pressOrigin: { x: number; y: number } | null = null

function notePressOrigin(event: MouseEvent) {
  pressOrigin = { x: event.clientX, y: event.clientY }
  if (annotationArmed.value === 'box') boxGesture = true
}

/**
 * A click places an annotation — but only while the Annotations panel has armed the chart. Unarmed,
 * this returns at once and the plot behaves exactly as it always has: uPlot's own cursor drag still
 * zooms and the double-click still resets.
 */
function onPlotClick(event: MouseEvent) {
  const origin = pressOrigin
  pressOrigin = null

  const style = annotationArmed.value
  const u = plot
  if (!u || style === null) return
  // A box is drawn by dragging, finished on the window mouseup above. A still press would otherwise
  // reach placeAnnotation and store a region of no size.
  if (style === 'box') return
  // A double-click fires two `click` events, so without this the reset gesture would place two
  // labels on top of one another while armed.
  if (event.detail !== 1) return
  if (origin && (Math.abs(event.clientX - origin.x) > CLICK_SLOP_PX || Math.abs(event.clientY - origin.y) > CLICK_SLOP_PX)) {
    return
  }
  placeAnnotation(u, style)
}

/**
 * Places an annotation where the cursor is: on the peak the click was obviously aimed at — the same
 * snap the readout uses, so the label records the figure the readout was showing at that moment —
 * and at the level of the trace under it, or simply where the click landed when there is no trace
 * there, so a note can be dropped on an empty chart or beside a hidden channel.
 *
 * <p>The frequency style snaps too, deliberately: clicking "near the third harmonic" should rule
 * exactly on it.</p>
 */
function placeAnnotation(u: uPlot, style: AnnotationStyle) {
  const xs = u.data[0]
  const idx = u.cursor.idx
  if (idx == null || xs?.[idx] == null) return

  const offset = annotationLevelOffsetDb()
  // Under a density unit with no ENBW to convert by there is no honest dBV to record, and the chart
  // is not drawing a trace either; leave the chart armed rather than storing a wrong level.
  if (style === 'point' && offset === null) return

  const snapped = snapToPeak(u, idx)
  const freqHz = xs[snapped] as number
  const read = readoutSeries(u)
  const plotted = read?.values[snapped]

  let levelDbv: number | null = null
  let channel: number | null = null
  if (style === 'point') {
    if (plotted != null) {
      levelDbv = plotted - (offset as number)
      channel = read?.channel ?? null
    } else {
      const top = u.cursor.top
      if (top == null || top < 0) return
      levelDbv = u.posToVal(top, 'y') - (offset as number)
    }
  }

  const id = nextAnnotationId(annotations.value)
  const next = addAnnotation(annotations.value, {
    style,
    text: '',
    freqHz,
    levelDbv,
    freqHz2: null,
    levelDbv2: null,
    channel,
    labelOffsetOctaves: style === 'point' ? DEFAULT_POINT_OFFSET.labelOffsetOctaves : 0,
    labelOffsetDb: style === 'point' ? DEFAULT_POINT_OFFSET.labelOffsetDb : 0,
  })
  // The chart is full — addAnnotation hands the list straight back. Say nothing here: the panel's
  // Add button is already disabled with the reason on it.
  if (next.length === annotations.value.length) return

  annotations.value = next
  annotationArmed.value = null
  annotationEditing.value = id
}


/**
 * Arming for a Box turns uPlot's own drag-selection into the region gesture, by mutating the live
 * cursor rather than adding an input layer of our own.
 *
 * <p>Three facts about uPlot 1.6.32 make this work, and all three are worth knowing before touching
 * it. It keeps <b>one reference</b> to `cursor.drag` for the instance's life, so mutating fields
 * works and replacing the object does not. It re-reads `drag.y` on every mousemove and
 * `drag.setScale` at mouseup, so both can be flipped per gesture. And `mouseUp` fires the
 * `setSelect` hook <i>before</i> it tests `setScale` to zoom — which is the other way this could
 * have been built, and is the way it is not: a flag that is a pure function of the armed state
 * cannot be left half-restored, whereas cancelling from inside the hook zooms to the half-drawn
 * rectangle if Escape lands mid-drag.</p>
 */
function syncBoxDragMode() {
  const u = plot
  // uPlot always merges its own defaults, so `drag` is there at runtime; the typings say it may not
  // be, and mutating the object it hands back is the whole mechanism -- see above.
  const drag = u?.cursor.drag
  if (!u || !drag) return
  const armedForBox = annotationArmed.value === 'box'
  drag.y = armedForBox
  drag.setScale = !armedForBox
  // With setScale off uPlot never calls its own hideSelect(), so the rubber band stays painted
  // until someone clears it.
  if (!armedForBox) u.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false)
}

watch(annotationArmed, () => {
  syncBoxDragMode()
  plot?.redraw()
})

/**
 * How big a drag has to be, in CSS pixels, to mean a region — on <b>both</b> axes.
 *
 * <p>It cannot be uPlot's own `drag.dist`: with `uni == null` uPlot promotes a pass on one axis to
 * both, so a 300 x 2 px drag clears `dist` and yields a hairline box. Twelve is the figure `uni`
 * already uses on the four two-axis charts here. Under it nothing is placed and <b>the chart stays
 * armed</b> — a slip of the hand should not cost a trip back to the panel.</p>
 */
const MIN_BOX_DRAG_PX = 12

/** True between mousedown and mouseup of a gesture that began while armed for a box. */
let boxGesture = false

/**
 * Finishes a box gesture.
 *
 * <p>Bound on <b>window, bubble phase</b>, and neither half of that is incidental. uPlot's own
 * `drag.click` is registered capture-phase on an ancestor and calls `stopImmediatePropagation()`
 * whenever the pointer moved at all, so nothing drag-shaped can be driven off a `click`; and a
 * window listener is the one place that runs after uPlot's own document-level `mouseUp`, so
 * `u.select` is still readable and the restore cannot race the zoom test. It also fires when the
 * button comes up outside the plot, which a listener on `over` would miss.</p>
 */
function onWindowMouseUp() {
  if (!boxGesture) return
  boxGesture = false
  const u = plot
  if (!u) return
  if (annotationArmed.value === 'box') placeBoxFromSelection(u)
  u.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false)
  syncBoxDragMode()
}

/**
 * The region the drag just swept, as an annotation. `u.select` is in CSS pixels relative to the
 * plotting area — the same space the label handles already work in.
 */
function placeBoxFromSelection(u: uPlot) {
  const sel = u.select
  if (sel.width < MIN_BOX_DRAG_PX || sel.height < MIN_BOX_DRAG_PX) return

  const offset = annotationLevelOffsetDb()
  // Both of a box's levels are stored in dBV like every other annotation level; under a density
  // unit with no ENBW there is no honest figure to store, so leave the chart armed.
  if (offset === null) return

  // No peak snapping, deliberately, where a point and a rule both snap: a region's edges are where
  // the hand put them, and jumping one to the nearest bin could move it by an arbitrary amount.
  const loHz = u.posToVal(sel.left, 'x')
  const hiHz = u.posToVal(sel.left + sel.width, 'x')
  const topDb = u.posToVal(sel.top, 'y')
  const bottomDb = u.posToVal(sel.top + sel.height, 'y')
  if (!(loHz > 0) || !(hiHz > loHz) || !Number.isFinite(topDb) || !Number.isFinite(bottomDb)) return

  const id = nextAnnotationId(annotations.value)
  const next = addAnnotation(annotations.value, {
    style: 'box',
    text: '',
    freqHz: loHz,
    levelDbv: topDb - offset,
    freqHz2: hiHz,
    levelDbv2: bottomDb - offset,
    channel: null,
    // Above the region and centred on it, which works precisely because the stored anchor is the
    // box's top-left corner.
    labelOffsetOctaves: Math.log2(hiHz / loHz) / 2,
    labelOffsetDb: 10,
  })
  if (next.length === annotations.value.length) return

  annotations.value = next
  annotationArmed.value = null
  annotationEditing.value = id
}

/**
 * Every annotation's geometry in CSS pixels, from the same layout the canvas drew with — which is
 * what makes a handle land exactly on its label. Re-runs when the list changes and when the plot's
 * geometry or window does (see plotGeometryTick).
 */
const annotationPlacements = computed<AnnotationPlacement[]>(() => {
  void plotGeometryTick.value
  const u = plot
  if (!u || annotations.value.length === 0) return []
  return layoutAnnotations(annotationItems(), cssAnnotationPlot(u), annotationNumbers.value)
})

/** The overlay sits exactly over the plotting area, so a placement's coordinates are its own. */
const annotationOverlayStyle = computed(() => {
  void plotGeometryTick.value
  const u = plot
  if (!u) return { display: 'none', left: '0px', top: '0px', width: '0px', height: '0px' }
  const scale = plotPixelScale(u)
  return {
    left: `${u.bbox.left / scale}px`,
    top: `${u.bbox.top / scale}px`,
    width: `${u.bbox.width / scale}px`,
    height: `${u.bbox.height / scale}px`,
  }
})

/**
 * One transparent handle per *point* label. A frequency rule's label is stacked at the top of the
 * plot out of its neighbours' way, which a manual nudge would fight, so it has nothing to drag.
 */
const annotationHandles = computed(() =>
  annotationPlacements.value
    // A box's label drags exactly like a point's, because a box's stored anchor is its top-left
    // corner — onHandleMove's maths needs no branch.
    .filter((p) => p.visible && (p.style === 'point' || p.style === 'box'))
    .map((p) => {
      const annotation = annotations.value.find((a) => a.id === p.id)
      return {
        id: p.id,
        title: annotation ? `${annotation.text || 'Annotation'} — ${annotationReading(annotation)}. Drag to move the label, double-click to edit.` : '',
        style: {
          left: `${p.label.x}px`,
          top: `${p.label.y}px`,
          width: `${p.label.w}px`,
          height: `${p.label.h}px`,
        },
      }
    }),
)


/**
 * How small a box may be dragged, in CSS pixels, before the floor in clampBoxToWindow stops it.
 *
 * <p>A <i>UI-integrity</i> floor rather than a measurement one: at about a grip's width the nine
 * grips can no longer collapse into each other and strand the region. Deliberately a different
 * number, and a different reason, from MIN_BOX_DRAG_PX, which is how big a *gesture* has to be to
 * mean a region at all.</p>
 */
const MIN_BOX_SPAN_PX = 10

/** The size floor in the data units clampBoxToWindow works in, for the window now on screen. */
function minBoxSpan(u: uPlot): { octaves: number; db: number } {
  const width = u.over.clientWidth || 1
  const height = u.over.clientHeight || 1
  const loHz = u.scales.x.min ?? 1
  const hiHz = u.scales.x.max ?? 1
  const bottomDb = u.scales.y.min ?? 0
  const topDb = u.scales.y.max ?? 0
  return {
    octaves: hiHz > loHz && loHz > 0 ? (MIN_BOX_SPAN_PX / width) * Math.log2(hiHz / loHz) : 0,
    db: (MIN_BOX_SPAN_PX / height) * (topDb - bottomDb),
  }
}

const GRIP_CURSOR: Record<BoxHandleKind, string> = {
  move: 'cursor-move',
  n: 'cursor-ns-resize',
  s: 'cursor-ns-resize',
  e: 'cursor-ew-resize',
  w: 'cursor-ew-resize',
  nw: 'cursor-nwse-resize',
  se: 'cursor-nwse-resize',
  ne: 'cursor-nesw-resize',
  sw: 'cursor-nesw-resize',
}

/**
 * The move and resize grips of every drawn box, in the same CSS pixels the label handles use.
 *
 * <p>Move is a <b>grip</b> and not the box's interior, which is the one thing to understand here: a
 * region can cover the plot, and its interior is where drag-zoom lives — marking a region and then
 * zooming into it is the obvious next thing a user does. The same judgement keeps the label's own
 * handle small. The cost is that a drag-zoom begun exactly on a box's border resizes it instead;
 * that is a few pixels per edge and the cursor says so on hover.</p>
 */
const boxGrips = computed(() => {
  void plotGeometryTick.value
  const u = plot
  if (!u) return []
  const m = annotationMetrics(cssAnnotationPlot(u))
  return annotationPlacements.value
    .filter((p) => p.visible && p.box)
    .flatMap((p) =>
      boxHandles(p.box as { x: number; y: number; w: number; h: number }, m).map((handle) => ({
        key: `${p.id}:${handle.kind}`,
        id: p.id,
        kind: handle.kind,
        cursor: GRIP_CURSOR[handle.kind],
        title: handle.kind === 'move' ? 'Drag to move this region.' : 'Drag to resize this region.',
        style: {
          left: `${handle.rect.x}px`,
          top: `${handle.rect.y}px`,
          width: `${handle.rect.w}px`,
          height: `${handle.rect.h}px`,
        },
      })),
    )
})

let boxDrag: { id: number; kind: BoxHandleKind; grabOctaves: number; grabDb: number; start: DataBox } | null = null

/** The box an annotation describes, in the unit the chart is plotting, or null when it is not one. */
function plottedBox(id: number, offset: number | null): DataBox | null {
  const a = annotations.value.find((x) => x.id === id)
  if (!a || a.style !== 'box' || offset === null) return null
  if (a.freqHz2 === null || a.levelDbv === null || a.levelDbv2 === null) return null
  return { freqHz: a.freqHz, freqHz2: a.freqHz2, levelDb: a.levelDbv + offset, levelDb2: a.levelDbv2 + offset }
}

function onBoxGripDown(id: number, kind: BoxHandleKind, event: PointerEvent) {
  const u = plot
  const offset = annotationLevelOffsetDb()
  const start = plottedBox(id, offset)
  if (!u || !start) return

  const rect = u.over.getBoundingClientRect()
  const pointerHz = u.posToVal(event.clientX - rect.left, 'x')
  const pointerDb = u.posToVal(event.clientY - rect.top, 'y')
  if (!(pointerHz > 0) || !Number.isFinite(pointerDb)) return

  // Where the pointer sits relative to the thing it grabbed, so the edge (or the corner, or the
  // whole region for a move) does not jump under the cursor on the first move.
  const anchorHz = kind === 'move' || kind.includes('w') ? start.freqHz : kind.includes('e') ? start.freqHz2 : pointerHz
  const anchorDb = kind === 'move' || kind.includes('n') ? start.levelDb : kind.includes('s') ? start.levelDb2 : pointerDb
  boxDrag = {
    id,
    kind,
    grabOctaves: Math.log2(pointerHz / anchorHz),
    grabDb: pointerDb - anchorDb,
    start,
  }
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  event.preventDefault()
}

function onBoxGripMove(event: PointerEvent) {
  const u = plot
  const drag = boxDrag
  if (!u || !drag) return
  const offset = annotationLevelOffsetDb()
  if (offset === null) return

  const rect = u.over.getBoundingClientRect()
  const freqHz = u.posToVal(event.clientX - rect.left, 'x') * Math.pow(2, -drag.grabOctaves)
  const levelDb = u.posToVal(event.clientY - rect.top, 'y') - drag.grabDb
  if (!(freqHz > 0) || !Number.isFinite(levelDb)) return

  // Always from the box the gesture *started* on, so a resize that crosses its opposite edge keeps
  // tracking the pointer rather than fighting its own normalised result.
  const proposed = boxFromDrag(drag.kind, drag.start, { freqHz, levelDb })
  const clamped = clampBoxToWindow(
    proposed,
    {
      loHz: u.scales.x.min ?? 0,
      hiHz: u.scales.x.max ?? 0,
      bottomDb: u.scales.y.min ?? 0,
      topDb: u.scales.y.max ?? 0,
    },
    minBoxSpan(u),
  )
  annotations.value = updateAnnotation(annotations.value, drag.id, {
    freqHz: clamped.freqHz,
    freqHz2: clamped.freqHz2,
    levelDbv: clamped.levelDb - offset,
    levelDbv2: clamped.levelDb2 - offset,
  })
}

function onBoxGripUp() {
  boxDrag = null
}

let dragging: { id: number; grabDx: number; grabDy: number } | null = null

function onHandleDown(id: number, event: PointerEvent) {
  const u = plot
  const placement = annotationPlacements.value.find((p) => p.id === id)
  if (!u || !placement) return
  const rect = u.over.getBoundingClientRect()
  dragging = {
    id,
    grabDx: event.clientX - rect.left - (placement.label.x + placement.label.w / 2),
    grabDy: event.clientY - rect.top - (placement.label.y + placement.label.h / 2),
  }
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  event.preventDefault()
}

function onHandleMove(event: PointerEvent) {
  const u = plot
  const drag = dragging
  if (!u || !drag) return
  const annotation = annotations.value.find((a) => a.id === drag.id)
  const placement = annotationPlacements.value.find((p) => p.id === drag.id)
  const offset = annotationLevelOffsetDb()
  if (!annotation || !placement || annotation.levelDbv === null || offset === null) return

  const rect = u.over.getBoundingClientRect()
  // posToVal is the one place this file goes from pixels back to values. The x scale is log
  // (distr: 3), so what comes back is a frequency and the stored offset is the log2 of its ratio to
  // the marker's own — see SpectrumAnnotation.labelOffsetOctaves for why the offset is in data units.
  const freqHz = u.posToVal(event.clientX - rect.left - drag.grabDx, 'x')
  const levelDb = u.posToVal(event.clientY - rect.top - drag.grabDy, 'y')
  if (!(freqHz > 0) || !Number.isFinite(levelDb)) return

  const anchorDb = annotation.levelDbv + offset
  const clamped = clampLabelOffset({
    freqHz: annotation.freqHz,
    levelDb: anchorDb,
    octaves: Math.log2(freqHz / annotation.freqHz),
    db: levelDb - anchorDb,
    window: {
      loHz: u.scales.x.min ?? 0,
      hiHz: u.scales.x.max ?? 0,
      bottomDb: u.scales.y.min ?? 0,
      topDb: u.scales.y.max ?? 0,
    },
    label: placement.label,
    plot: { width: u.over.clientWidth, height: u.over.clientHeight },
  })
  annotations.value = updateAnnotation(annotations.value, drag.id, {
    labelOffsetOctaves: clamped.octaves,
    labelOffsetDb: clamped.db,
  })
}

function onHandleUp() {
  dragging = null
}

/** The label whose text box is open, and where to put it. */
const editingPlacement = computed(
  () => annotationPlacements.value.find((p) => p.id === annotationEditing.value && p.visible) ?? null,
)
const annotationEditorStyle = computed(() => {
  const placement = editingPlacement.value
  if (!placement) return { display: 'none', left: '0px', top: '0px', width: '0px' }
  return {
    left: `${placement.label.x}px`,
    top: `${placement.label.y}px`,
    width: `${Math.max(160, placement.label.w)}px`,
  }
})

const annotationEditorRef = ref<HTMLInputElement | null>(null)
const editingText = ref('')
let cancellingEdit = false

watch(annotationEditing, async (id) => {
  editingText.value = id === null ? '' : annotations.value.find((a) => a.id === id)?.text ?? ''
  if (id === null) return
  await nextTick()
  annotationEditorRef.value?.focus()
  annotationEditorRef.value?.select()
})

function commitAnnotationEdit() {
  const id = annotationEditing.value
  if (id === null) return
  annotations.value = updateAnnotation(annotations.value, id, { text: editingText.value.trim() })
  annotationEditing.value = null
}

/**
 * Escape leaves the text as it was — and removes the annotation outright when it was only just
 * placed and still says nothing, so a stray click leaves nothing behind.
 */
function cancelAnnotationEdit() {
  const id = annotationEditing.value
  if (id === null) return
  cancellingEdit = true
  // A box is exempt: it took a deliberate drag of at least MIN_BOX_DRAG_PX to make, and a region
  // with no note still says "look here". Only a click-placed marker is thrown away unnamed.
  const existing = annotations.value.find((a) => a.id === id)
  if (existing && existing.style !== 'box' && existing.text.length === 0) {
    annotations.value = removeAnnotation(annotations.value, id)
  }
  annotationEditing.value = null
}

/** Clicking away commits, which is what a text box on a chart has to mean — unless Escape did it. */
function onAnnotationEditorBlur() {
  if (cancellingEdit) {
    cancellingEdit = false
    return
  }
  commitAnnotationEdit()
}

function nullFill(n: number): (number | null)[] {
  return new Array<number | null>(n).fill(null)
}

/** An overlay's stored channel by index (0=L, 1=R) — absent when that channel wasn't captured (e.g. a single-channel result). */
function channelByIndex(detail: OverlayEntry['detail'], ch: number): MeasurementChannel | undefined {
  return detail.channelsData.find((c) => c.channel === ch)
}

/** A stored channel's own frequency axis, for use as the active x grid when nothing is streaming. */
function channelFreqs(channel: MeasurementChannel): Float64Array {
  return Float64Array.from(channel.columns, (c) => c.freqHz)
}

/**
 * A stored channel as a resample()-ready trace. maxDbv only, matching what the live series plots
 * (the max, not the full min/max band — minMaxBandPlugin stays live-only, see its own remarks;
 * a band per stored trace would make a four-trace comparison unreadable).
 */
function toTrace(channel: MeasurementChannel, offset: number) {
  return { freqHz: channel.columns.map((c) => c.freqHz), value: channel.columns.map((c) => c.maxDbv + offset) }
}

/**
 * Highest/lowest dBV across whatever is actually plotted right now — visible live channels plus
 * visible overlays — for the dBV range "Autofit" button. Reads straight from the same sources
 * update() draws from, not from uPlot's own data (which nulls out hidden series rather than
 * omitting them, and would need unpacking back out of the flat setData arrays).
 */
function computeYExtent(): { min: number; max: number } | null {
  let min = Infinity
  let max = -Infinity

  function consider(v: number) {
    if (!Number.isFinite(v)) return
    if (v > max) max = v
    if (v < min) min = v
  }

  for (let ch = 0; ch < 2; ch++) {
    if (!channelVisible(ch)) continue
    const frame = props.spectrum[ch]
    if (!frame) continue
    const offset = frameOffset(frame)
    if (offset === null) continue
    const { columns, header } = frame
    for (let i = 0; i < header.nPoints; i++) consider(columns[i * 3 + 2] + offset)
  }

  for (const overlay of props.overlays) {
    if (!overlay.visible) continue
    for (const ch of [0, 1]) {
      const channel = channelByIndex(overlay.detail, ch)
      if (!channel) continue
      const offset = channelOffset(channel)
      if (offset === null) continue
      for (const c of channel.columns) consider(c.maxDbv + offset)
    }
  }

  return Number.isFinite(min) && Number.isFinite(max) ? { min, max } : null
}

/**
 * Whether a visible live channel or overlay has a plotted point, within the current frequency
 * window, above or below the current dBV window — the cue that there is real data off the top or
 * bottom of the chart. Unlike computeYExtent (which the Autofit button uses and deliberately reads
 * the whole span), this is scoped to what's actually in view on the frequency axis.
 */
const outOfRange = computed(() => {
  let above = false
  let below = false

  function consider(freqHz: number, min: number, max: number) {
    if (freqHz < xFrom.value || freqHz > xTo.value) return
    if (Number.isFinite(max) && max > yTop.value) above = true
    if (Number.isFinite(min) && min < yBottom.value) below = true
  }

  for (let ch = 0; ch < 2; ch++) {
    if (!channelVisible(ch)) continue
    const frame = props.spectrum[ch]
    if (!frame) continue
    const offset = frameOffset(frame)
    if (offset === null) continue
    const { columns, header } = frame
    for (let i = 0; i < header.nPoints; i++) consider(columns[i * 3], columns[i * 3 + 1] + offset, columns[i * 3 + 2] + offset)
  }

  for (const overlay of props.overlays) {
    if (!overlay.visible) continue
    for (const ch of [0, 1]) {
      const channel = channelByIndex(overlay.detail, ch)
      if (!channel) continue
      const offset = channelOffset(channel)
      if (offset === null) continue
      for (const c of channel.columns) consider(c.freqHz, c.minDbv + offset, c.maxDbv + offset)
    }
  }

  return { above, below }
})

/** The Frequency range "Autofit" target's upper edge — a fixed step above the current sample
 * rate's Nyquist frequency, not a true data-driven autofit (unlike the dBV range button): 48 kHz
 * -> 25 kHz, 96 kHz -> 50 kHz, 192 kHz -> 100 kHz, 384 kHz -> 200 kHz, and reasonable buckets for
 * anything else. The 384 kHz arm is not optional decoration: without it Autofit at that rate shows
 * 0-100 kHz of a 0-192 kHz band, which on screen is indistinguishable from the sample rate not
 * having applied at all. */
function autofitFreqToFor(sampleRateHz: number): number {
  if (sampleRateHz <= 48000) return 25000
  if (sampleRateHz <= 96000) return 50000
  if (sampleRateHz <= 192000) return 100000
  return 200000
}

watch(autofitYTrigger, () => {
  const extent = computeYExtent()
  if (extent) {
    // +10/-10 headroom, then rounded out to the nearest multiple of 10 (up for Top, down for
    // Bottom) so the range lines up with the Top/Bottom fields' own step-10 spinners.
    yTop.value = Math.ceil((extent.max + 10) / 10) * 10
    yBottom.value = Math.floor((extent.min - 10) / 10) * 10
  } else {
    resetYDefaults()
  }
})

watch(autofitXTrigger, () => {
  if (fullHiFreqHz > 0) {
    xFrom.value = 10
    xTo.value = autofitFreqToFor(fullHiFreqHz * 2)
  } else {
    resetXDefaults()
  }
})

/** What one tick draws: the full span the axis is sized from, and every series on the active x grid, in buildOptions' order. */
interface FrameData {
  fullLoFreqHz: number
  fullHiFreqHz: number
  data: uPlot.AlignedData
}

/**
 * The plot's data for the current props, or null when there is nothing to draw — no live frame and
 * no overlay to fall back to. Shared by update() and the export, so the PNG is exactly this tick.
 * The full span carries over from the previous call for a zoomed frame, which only describes its
 * window; peak-hold folds this tick's columns into the running maps as a side effect (a maximum,
 * so calling it twice for one tick is harmless).
 */
function computeFrame(): FrameData | null {
  const ch0 = props.spectrum[0]
  const ch1 = props.spectrum[1]
  // The frame that drives the x grid this tick — L when it's being processed, otherwise R. A
  // measurement with only Right enabled (SpectrumConfigPanel's Left checkbox off) never populates
  // spectrum[0] at all, so gating everything below on ch0 specifically left the chart permanently
  // on "Waiting for data…" while Spectrogram/Oscilloscope (which have no such L-only assumption) kept
  // working off the very same stream.
  const primary = ch0 ?? ch1
  // The overlay the x grid falls back to when nothing is streaming — deliberately not `overlays[0]`,
  // see spectrumAxisOverlay for what that cost.
  const firstOverlayEntry = spectrumAxisOverlay(props.overlays)
  const firstOverlay = firstOverlayEntry?.detail
  const firstOverlayChannel = firstOverlay && (channelByIndex(firstOverlay, 0) ?? channelByIndex(firstOverlay, 1))

  if (!primary && !firstOverlayChannel) return null

  // Only an overview frame describes the whole spectrum; a zoomed one describes the zoom window.
  // The axis must always be built from the former, or zooming would redefine "fully zoomed out".
  // With no live data at all (viewing loaded overlays before ever streaming), fall back to the
  // first overlay — recomputed every call, so switching overlays re-fits it.
  let lo = fullLoFreqHz
  let hi = fullHiFreqHz
  if (primary) {
    // `spanFrom` rather than "overview frames only", because this plot's span is its own: the two
    // module-level edges above are reset by every remount, and switching measurement away and back
    // remounts this component. With only a zoomed frame in the store at that moment — which is the
    // ordinary state of a stopped capture, see ResendRetainedTo — refusing its span left the chart
    // with nothing to scale to and it drew its empty state over a capture it was holding. The band
    // kept being painted, since minMaxBandPlugin reads the frames and not the plot's data.
    ;({ lo, hi } = spanFrom(
      { lo, hi },
      { lo: primary.loFreqHz, hi: primary.hiFreqHz },
      (primary.header.flags & STREAM_FLAG_ZOOMED) !== 0,
    ))
  } else if (firstOverlayEntry && firstOverlayChannel) {
    // `spectrumWindow` is the overlay's STREAM_FLAG_ZOOMED. The rule, and the request loop that
    // not applying it here produced, are written out in overviewSpan -- which is where it lives so
    // it can be tested, nothing in a .vue being testable here.
    ;({ lo, hi } = overviewSpan({ lo, hi }, firstOverlayChannel.columns, firstOverlayEntry.spectrumWindow))
  }

  if (hi <= 0) return null

  // The active x grid: whichever of L/R is actually streaming (L preferred when both are, matching
  // the original single-channel assumption everywhere else in this file), otherwise the first
  // overlay's — everything else gets resampled onto this, since uPlot draws one x array per plot.
  // See measurements/resample.ts.
  const xs = primary ? freqTrace(primary.columns, primary.header.nPoints) : channelFreqs(firstOverlayChannel!)
  const nPoints = xs.length

  // Only real data for channels actually streaming, enabled, and toggled visible — an idle,
  // disabled, or hidden channel must not show up as a phantom trace (idle: single-channel
  // acquisition; disabled: the Acquisition panel's L/R checkbox turned off, but the stream record
  // for it never gets cleared — see useSpectrumStream.ts; hidden: this component's own L/R
  // checkboxes, see channelVisible). Everything else fills with null rather than being omitted:
  // the series themselves are always present (see buildOptions), only their data varies per tick.
  const ch0Offset = ch0 ? frameOffset(ch0) : null
  const ch1Offset = ch1 ? frameOffset(ch1) : null
  // `showFftTrace` hides these two lines here; the min/max band is drawn by minMaxBandPlugin off the
  // frames themselves, and goes with them because useChartDisplay's watcher clears `showMinMax` at
  // the same time — one checkbox, both halves of the same capture. Peak-hold below is deliberately
  // not gated on it: leaving that trace legible on its own is the point of the toggle.
  const left = showFftTrace.value && ch0 && channelVisible(0) && ch0Offset !== null ? maxTrace(ch0.columns, ch0.header.nPoints, ch0Offset) : nullFill(nPoints)

  // R can only be drawn when it was decimated over the same span as whatever xs came from (the
  // one frame after a zoom where L has already switched and R has not, is the case this guards).
  // When R is itself the primary (xs) source — L not being processed — it's trivially comparable
  // to itself, so the ch0 side of the check is skipped rather than required.
  const rComparable =
    ch1 && channelVisible(1) && ch1.header.nPoints === nPoints && ch1Offset !== null &&
    (!ch0 || (ch1.header.flags & STREAM_FLAG_ZOOMED) === (ch0.header.flags & STREAM_FLAG_ZOOMED))
  const right = showFftTrace.value && rComparable ? maxTrace(ch1!.columns, ch1!.header.nPoints, ch1Offset!) : nullFill(nPoints)

  // Peak-hold: fold this tick's columns into the running maps (only while enabled — leaving it off
  // is how a user keeps yesterday's peak run from bleeding into today's), then read the maps back
  // out onto the same x grid as everything else. Gated the same way as left/right above (visible,
  // channel present) so a hidden or idle channel's peak trace disappears along with its live one.
  if (showPeakHold.value) {
    if (ch0 && channelVisible(0)) updatePeakHold(peakHoldMaps[0], ch0)
    if (rComparable) updatePeakHold(peakHoldMaps[1], ch1!)
  }
  const leftPeak = showPeakHold.value && ch0 && channelVisible(0) && ch0Offset !== null ? peakTrace(peakHoldMaps[0], xs, ch0Offset) : nullFill(nPoints)
  const rightPeak = showPeakHold.value && rComparable ? peakTrace(peakHoldMaps[1], xs, ch1Offset!) : nullFill(nPoints)

  // Passed to setData as a literal rather than being accumulated in a pre-typed local array:
  // uPlot's AlignedData permits a TypedArray x with y-series that are TypedArray *or*
  // (number|null)[], but only through its tuple form. Annotating an intermediate array collapses
  // that to a plain union-element array, which matches neither branch — and `vue-tsc -b` (what
  // `npm run build` runs) rejects it even though a bare `vue-tsc --noEmit` lets it through.
  //
  // The 1-arg setData is also deliberate: passing resetScales=false looks like the way to stop
  // zoom-reset (see the x scale's `auto:false` above for the actual fix), but in this uPlot build
  // setData()'s commit() — the call that actually repaints the canvas — only runs inside the same
  // `if (_resetScales !== false)` branch as the scale reset. Passing false skips both: the chart
  // would freeze on frame one and never draw again.
  const overlayData = props.overlays.flatMap((overlay) => {
    const chL = channelByIndex(overlay.detail, 0)
    const chR = channelByIndex(overlay.detail, 1)
    const offL = chL ? channelOffset(chL) : null
    const offR = chR ? channelOffset(chR) : null
    return [
      overlay.visible && chL && offL !== null ? resample(toTrace(chL, offL), xs) : nullFill(nPoints),
      overlay.visible && chR && offR !== null ? resample(toTrace(chR, offR), xs) : nullFill(nPoints),
    ]
  })
  return { fullLoFreqHz: lo, fullHiFreqHz: hi, data: [xs, left, right, leftPeak, rightPeak, ...overlayData] }
}

/**
 * The chart with nothing in it: axes on the panel's own frequency and level windows, and no trace
 * between them. This is the state before the very first frame of a session and after the Clear
 * button, and it used to *destroy* the plot — which left the panel collapsed to its title row, so
 * an FFT screen that had not been run yet was three header strips rather than three charts and
 * pressing RUN made the whole column jump. Destroying was right about one thing, and that half is
 * kept: whatever the last setData painted must not stay on the canvas, so every series is emptied
 * rather than simply left alone.
 *
 * The span is the panel's From/To, because there is no data to have a span of its own — which is
 * also what the x scale's `range` callback falls back to, so the two agree and `zoomed` reads
 * false on an untouched chart. Any zoom saved across a rebuild is dropped for the same reason:
 * there is nothing left to be zoomed into.
 */
/** Whether what is on the canvas is the empty state — see the [xFrom, xTo] watcher, its one reader. */
let showingEmpty = false

function drawEmpty() {
  showingEmpty = true
  fullLoFreqHz = xFrom.value
  fullHiFreqHz = xTo.value
  ensurePlot(fullHiFreqHz, props.overlays)
  if (!plot) return
  // Same AlignedData-typing wrinkle as ensurePlot's own empty construction — see its comment.
  const dataSlots = 5 + props.overlays.length * 2
  plot.setData(Array.from({ length: dataSlots }, () => [] as number[]) as unknown as uPlot.AlignedData)
  pendingZoomRestore = null
}

function update() {
  const frame = computeFrame()

  // Nothing to draw — no live frame and no overlay to fall back to (the Clear button, or before
  // the very first frame ever arrives).
  if (!frame) {
    drawEmpty()
    return
  }

  showingEmpty = false
  fullLoFreqHz = frame.fullLoFreqHz
  fullHiFreqHz = frame.fullHiFreqHz

  ensurePlot(fullHiFreqHz, props.overlays)
  if (!plot) return

  plot.setData(frame.data)

  // See ensurePlot's pendingZoomRestore comment — must run after setData, not before.
  if (pendingZoomRestore) {
    plot.setScale('x', pendingZoomRestore)
    pendingZoomRestore = null
  }
}

/**
 * The chart as a PNG of exactly the requested size, drawn with the light theme whatever the screen's
 * — the same arrangement as FrequencyResponseChart.exportPng, which explains it. This chart's own
 * ordering is kept: empty construction, then setData — which seeds x from the panel's From/To, see
 * the scale's own comment. The explicit setScale below is still needed even so: with captureZoom it
 * is the live plot's window rather than the panel's, and a headless render of a stored result holds
 * the whole stored span rather than the server-cut live one.
 */
async function exportPng({ width, height, captureZoom = false, primaryLabel }: ChartExportOptions): Promise<Blob> {
  const frame = computeFrame()
  if (!frame) throw new Error('There is no spectrum to export.')

  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header

  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${width}px;height:${plotHeight}px;pointer-events:none`
  document.body.appendChild(host)

  const live = plot
  const xWindow =
    captureZoom && live?.scales.x.min != null && live.scales.x.max != null
      ? { min: live.scales.x.min, max: live.scales.x.max }
      : { min: xFrom.value, max: xTo.value }

  let u: uPlot | null = null
  try {
    const dataSlots = 5 + props.overlays.length * 2
    const emptyData = Array.from({ length: dataSlots }, () => [] as number[]) as unknown as uPlot.AlignedData
    u = new uPlot(buildOptions(width, plotHeight, props.overlays, exportGridTheme(THEME.light), false), emptyData, host)
    u.setData(frame.data)
    u.setScale('x', xWindow)
    await nextPaint()

    const own = (label: string) => (primaryLabel ? `${primaryLabel} — ${label}` : label)
    const legend: ExportLegendEntry[] = []
    if (showFftTrace.value && props.spectrum[0] && channelVisible(0)) legend.push({ label: own('L'), color: LIVE_COLOR })
    if (showFftTrace.value && props.spectrum[1] && channelVisible(1)) legend.push({ label: own('R'), color: LIVE_COLOR, dash: [6, 3] })
    if (showPeakHold.value) legend.push({ label: own('Peak hold'), color: PEAK_HOLD_COLOR, dash: [2, 2] })
    if (showWeightingCurve.value && weightingCurve.value) legend.push({ label: 'Weighting curve (0 dB at top − 20 dB)', color: WEIGHTING_CURVE_COLOR, dash: [6, 4] })
    for (const overlay of props.overlays) {
      if (!overlay.visible) continue
      legend.push({ label: overlay.detail.name, color: fftOverlayColor(props.overlays, overlay.id) })
    }

    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: `Spectrum (${unitSuffix(spectrumUnit.value)})${props.weightingLabel ? ` · ${props.weightingLabel}` : ''}`,
      legend,
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

/**
 * Puts the chart back to the display panel's own window, dropping a drag-zoom.
 *
 * <p>Called when the live display is handed from one measurement to another (App.vue's
 * swapFftDisplay): the two measurements may well store the same From/To, in which case the
 * `[xFrom, xTo]` watcher above never fires and the second one would be drawn inside the first one's
 * zoom. Goes through the panel's window rather than the data's full span, because that is the
 * window the arriving measurement stored; the Reset button's own answer is deliberately the data's
 * span instead (see resetZoom).</p>
 */
function resetToPanelWindow() {
  plot?.setScale('x', { min: xFrom.value, max: xTo.value })
  plot?.setScale('y', { min: yBottom.value, max: yTop.value })
  if (showingEmpty) drawEmpty()
}

defineExpose({ exportPng, resetToPanelWindow })

watch(() => props.spectrum, update)
watch(() => props.overlays, update, { deep: true })
watch(visibleL, update)
watch(visibleR, update)
watch(showMinMax, update)
watch(showPeakHold, update)
watch(showFftTrace, update)
watch(showWeightingCurve, () => plot?.redraw())
// A draw-hook-only flag, like the weighting curve's: nothing about the data or the scales changes,
// so a redraw is the whole update.
watch([showHarmonicMarkers, harmonicMarkerUnit], () => plot?.redraw())
// Annotations are draw-hook-only state as well, and they must *never* reach styleKey() or
// overlaySignature(): a rebuild there would destroy the plot on every edit and throw the zoom away,
// so a flicker while editing a label means one of them has grown an annotation.
watch(annotations, () => plot?.redraw())
watch(annotationNumbers, () => plot?.redraw())
watch(yTop, () => plot?.redraw())
watch(thickLines, update)
watch(showDataPoints, update)

watch(resetPeakHoldTrigger, () => {
  peakHoldMaps[0].clear()
  peakHoldMaps[1].clear()
  update()
})

// dBV range: a live setScale, not a rebuild — uPlot has no per-call range override, but the scale
// itself can be nudged directly and takes effect on the next paint.
watch([yTop, yBottom], ([top, bottom]) => {
  plot?.setScale('y', { min: bottom, max: top })
})

// Frequency range: same idea, driven by the From/To dropdowns in ChartDisplayPanel rather than a
// mouse drag on the chart.
watch([xFrom, xTo], ([from, to]) => {
  plot?.setScale('x', { min: from, max: to })
  // An empty chart's full span *is* the panel's window (see drawEmpty), so moving the window moves
  // the span with it. Without this the chart would read as zoomed the moment the dropdowns were
  // touched before a run, and offer a Reset that changed nothing.
  if (showingEmpty) drawEmpty()
})

// Observes the container rather than the window: the column this chart sits in also changes width
// when the measurement sidebar is pinned or unpinned, which is not a window resize — measured, the
// canvas stayed at 432 px inside a column that had grown to 661 px. Same mechanism as the
// ResizeObserver in FrequencyResponseChart.
let resizeObserver: ResizeObserver | null = null
function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: chartHeight.value })
}
watch(chartHeight, handleResize)

// Rebuilds with the new palette on a Light/Dark/Auto change (OS or the Configuration dialog).
// Rebuilding is only acceptable because this fires on a theme change, not per frame —
// ensurePlot's identity check keeps every other path on setData().
function handleThemeChange() {
  if (!plot) return
  const nyquistHz = plotNyquistHz
  plot.destroy()
  plot = null
  plotNyquistHz = -1
  ensurePlot(nyquistHz, props.overlays)
  update()
}
watch(isDark, handleThemeChange)

/** Escape disarms, which is what a hand reaches for mid-gesture; the panel's Cancel does the same. */
function onWindowKeyDown(event: KeyboardEvent) {
  // Gated on the inline editor being closed, or this would fight its own Escape handler.
  if (event.key !== 'Escape' || annotationArmed.value === null || annotationEditing.value !== null) return
  annotationArmed.value = null
}

onMounted(() => {
  window.addEventListener('mouseup', onWindowMouseUp)
  window.addEventListener('keydown', onWindowKeyDown)
  resizeObserver = new ResizeObserver(handleResize)
  if (containerRef.value) resizeObserver.observe(containerRef.value)
  void refreshWeightingCurve()
  // Draw what the store already holds. Every path into `update` is a watcher on changing data, so a
  // remount with *unchanged* data drew nothing: leaving an FFT measurement and coming back showed
  // empty charts until the next frame, although the spectrum was still in the store the whole time.
  update()
})
onBeforeUnmount(() => {
  window.removeEventListener('mouseup', onWindowMouseUp)
  window.removeEventListener('keydown', onWindowKeyDown)
  resizeObserver?.disconnect()
  clearTimeout(viewportTimer)
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section ref="rootRef" class="relative flex flex-col gap-2 panel-surface rounded-xl border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="rootRef" />
    <!-- Three slots — title, cursor readout, zoom state — that never take each other's place, and a
         min-height the Reset zoom button fits inside: the readout used to *replace* the button while
         the cursor was over the plot, and since the button is taller than a line of text the header
         shrank on every hover and the plot below jumped up by the difference. -->
    <div class="min-h-6 pr-9 flex items-center justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <!-- Title and the weighting annunciator in one fixed group, and the middle slot always
           flex-1 whatever it holds: with the chip as its own flex item and the readout/spacer
           sized differently, justify-between redistributed the chip every time the cursor entered
           the plot, so it slid sideways on hover. -->
      <span class="flex shrink-0 items-center gap-2">
        <span>Spectrum</span>
        <!-- The weighting annunciator: a weighted trace and its noise figures read differently
             from flat ones, and nothing else on the chart would say which it is. -->
        <span
          v-if="props.weightingLabel"
          data-weighting-annunciator
          class="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"
          :title="`The trace and the noise figures are weighted: ${props.weightingLabel}. The fundamental, harmonics and THD are not.`"
        >
          {{ props.weightingLabel }}
        </span>
      </span>
      <span v-if="cursorReadout" class="min-w-0 flex-1 truncate text-right font-mono">
        {{ cursorReadout.freqHz.toFixed(1) }} Hz · {{ cursorReadout.dbv.toFixed(1) }} {{ unitSuffix(spectrumUnit) }}
      </span>
      <span v-else-if="!spectrum[0] && !spectrum[1]" class="min-w-0 flex-1 truncate text-right">Waiting for data…</span>
      <span v-else class="flex-1"></span>
      <!-- The only visible sign that the frequency axis is no longer showing the full data span —
           double-clicking does the same thing, but nothing else on screen would say so. -->
      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border px-2 py-0.5 btn-action"
        title="Back to the full frequency span."
        @click="resetZoom"
      >
        <ToolbarIcon name="reset-zoom" />
        Reset zoom
      </button>
    </div>
    <!-- L/R/min-max/thick-lines/show-points/dBV-range/frequency-range controls now live in
         ChartDisplayPanel.vue, a sibling column next to the chart — see App.vue — all reading and
         writing the same useChartDisplay() state this component reacts to. -->
    <div class="relative" :class="annotationArmed ? 'cursor-crosshair' : ''">
      <div ref="containerRef" class="w-full"></div>
      <!-- The annotations' drag handles and inline text box: DOM elements over the canvas-drawn
           labels, laid out by the same layoutAnnotations call the plugin draws with. They are a
           sibling of the plot's own container, so a pointerdown on a handle never reaches uPlot's
           `over` element and a label drag can therefore never start a drag-zoom — which is the
           whole reason the hit-testing is not done on the canvas. The labels themselves stay
           canvas-drawn, because the report only ever sees exportPng's canvas. -->
      <div class="pointer-events-none absolute" :style="annotationOverlayStyle">
        <div
          v-for="handle in annotationHandles"
          :key="handle.id"
          class="pointer-events-auto absolute cursor-move rounded hover:bg-neutral-500/10"
          :style="handle.style"
          :title="handle.title"
          @pointerdown="onHandleDown(handle.id, $event)"
          @pointermove="onHandleMove"
          @pointerup="onHandleUp"
          @pointercancel="onHandleUp"
          @dblclick="annotationEditing = handle.id"
        ></div>
        <div
          v-for="grip in boxGrips"
          :key="grip.key"
          class="pointer-events-auto absolute rounded-sm hover:bg-neutral-500/20"
          :class="grip.cursor"
          :style="grip.style"
          :title="grip.title"
          @pointerdown="onBoxGripDown(grip.id, grip.kind, $event)"
          @pointermove="onBoxGripMove"
          @pointerup="onBoxGripUp"
          @pointercancel="onBoxGripUp"
        ></div>
        <input
          v-if="editingPlacement"
          ref="annotationEditorRef"
          v-model="editingText"
          type="text"
          :maxlength="MAX_ANNOTATION_TEXT"
          placeholder="What is this?"
          class="tb-input pointer-events-auto absolute h-7 text-xs"
          :style="annotationEditorStyle"
          @keydown.enter.prevent="commitAnnotationEdit"
          @keydown.esc.prevent="cancelAnnotationEdit"
          @blur="onAnnotationEditorBlur"
        />
      </div>
      <!-- Off-scale cue: a visible channel or overlay has a value above/below the current dBV window. -->
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
  </section>
</template>

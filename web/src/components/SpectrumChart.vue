<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref, toRef, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { STREAM_FLAG_ZOOMED, type MetersFrame, type SpectrumFrame } from '../streaming/protocol'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { SPECTRUM_DISPLAY_KEY, useChartDisplay, type SpectrumDisplaySource } from '../composables/useChartDisplay'
import {
  CHART_EXPORT_HEADER_PX,
  composeChartPng,
  nextPaint,
  exportAxisLabel,
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
import { FFT_LIVE_COLOR, fftOverlayColor } from '../measurements/spectrumOverlayColors'
import type { MeasurementChannel } from '../api/client'

const props = defineProps<{
  spectrum: Record<number, SpectrumFrame>
  meters: Record<number, MetersFrame>
  /** Stored measurements loaded onto the chart, in add order — see useMeasurements.ts. */
  overlays: OverlayEntry[]
  /**
   * The block above this chart in the same column — the metrics row and the averages line —
   * that the fit should leave room for, so the readings and the chart share the view. See
   * oneLineOfMetrics for how much of it is reserved.
   */
  fitAbove?: HTMLElement | null
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
const display: SpectrumDisplaySource = inject(SPECTRUM_DISPLAY_KEY, null) ?? useChartDisplay()
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
  activeXRange,
  resetPeakHoldTrigger,
  autofitYTrigger,
  autofitXTrigger,
  resetYDefaults,
  resetXDefaults,
} = display
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
// glance, not a dimmer echo of whichever trace it belongs to.
const PEAK_HOLD_COLOR = '#eab308'
// The min/max band is live-only (see minMaxBandPlugin — it never reads overlay data) and is drawn
// as a filled stroke per column rather than a uPlot series, so it can't carry a dash the way the
// line above it now does. It keeps its own two-hue blue/orange distinction rather than switching to
// LIVE_COLOR for both, which is the only way left to tell an overlapping L band from an R band.
const CHANNEL_BAND_COLORS = ['rgba(59, 130, 246, 0.35)', 'rgba(249, 115, 22, 0.35)']
// Peak-hold: running per-bin maximum, keyed by that bin's column frequency (as broadcast — stable
// across ticks for a given FFT size/zoom window, since the server's log-decimation always picks
// the same center bin for the same range/budget). Cleared only by the Reset button (see
// resetPeakHoldTrigger) — switching FFT size etc. just leaves old keys unused rather than an
// explicit clear, since the new frequency grid won't match them anyway.
const peakHoldMaps: [Map<number, number>, Map<number, number>] = [new Map(), new Map()]
// How far (in CSS pixels) the cursor readout searches for a local peak around the mouse — see
// setCursor's remarks below.
const PEAK_SNAP_RADIUS_PX = 15

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
    markerLine: 'rgba(234, 179, 8, 0.45)',
    markerText: 'rgba(250, 204, 21, 0.95)',
  },
  light: {
    axis: '#525252',
    grid: '#e5e5e5',
    markerLine: 'rgba(234, 179, 8, 0.5)',
    markerText: 'rgba(161, 98, 7, 0.95)',
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
function minMaxBandPlugin(getSpectrum: () => Record<number, SpectrumFrame>): uPlot.Plugin {
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

            const path = new Path2D()
            const { columns, header } = frame
            for (let i = 0; i < header.nPoints; i++) {
              const freqHz = columns[i * 3]
              const minDb = columns[i * 3 + 1]
              const maxDb = columns[i * 3 + 2]
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
let lastReportedLo = Number.NaN
let lastReportedHi = Number.NaN

function reportViewport(u: uPlot) {
  const min = u.scales.x.min
  const max = u.scales.x.max
  if (min == null || max == null) return

  // Published before (and independently of) the server-facing viewport below: the waterfall
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

function harmonicMarkersPlugin(theme: Theme, interactive: boolean): uPlot.Plugin {
  const drawHook = (u: uPlot) => {
    if (stimulusKind.value !== 'Tones') return
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
      ctx.fillText(`${h.dbc.toFixed(0)}`, x + 3, u.bbox.top + 10)
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
          const ys = u.data[1]
          if (idx == null || xs[idx] == null || ys?.[idx] == null) {
            cursorReadout.value = null
            return
          }

          // Snaps to the local peak (max) within a small pixel radius of the cursor, rather than
          // literally whatever column uPlot's own nearest-x lookup landed on. Reading a
          // harmonic's or the fundamental's true level off the chart would otherwise need
          // pixel-perfect mouse placement on that exact column — easy to miss by a column or two,
          // especially at higher frequencies where many raw bins collapse into one narrow
          // on-screen column. The search radius itself bounds how far this can jump, so it never
          // latches onto some distant, unrelated peak.
          let bestIdx = idx
          let bestY = ys[idx] as number
          const cursorPx = u.cursor.left
          if (cursorPx != null && cursorPx >= 0) {
            for (let i = 0; i < xs.length; i++) {
              const x = xs[i]
              const y = ys[i]
              if (x == null || y == null) continue
              if (Math.abs(u.valToPos(x, 'x', false) - cursorPx) > PEAK_SNAP_RADIUS_PX) continue
              if (y > bestY) {
                bestY = y
                bestIdx = i
              }
            }
          }

          cursorReadout.value = { freqHz: xs[bestIdx] as number, dbv: bestY }
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
  nyquistHz: number,
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
      x: { time: false, distr: 3, log: 10, auto: false, range: (_u, dataMin, dataMax) => [dataMin ?? 10, dataMax ?? nyquistHz] },
      // Initial range only — the dBV range fields in the display-options column (ChartDisplayPanel)
      // adjust it live afterward via the watcher below, without rebuilding the plot.
      y: { range: [yBottom.value, yTop.value] },
    },
    axes: [
      {
        // Ticks and labels come from freqAxis.ts, shared with the waterfall stacked underneath so
        // the two axes agree column for column — see the reasoning there for why uPlot's own
        // log-axis ticking isn't usable once you zoom in.
        ...freqAxis(),
        ...exportAxisLabel(interactive, 'Frequency (Hz)'),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      {
        values: (_u, vals) => vals.map(formatDbTick),
        size: 50,
        ...exportAxisLabel(interactive, 'Level (dBV)'),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
    ],
    series,
    cursor: interactive ? { points: { show: false } } : { show: false },
    // uPlot's built-in legend doubles as a click-to-hide-series control — that's what caused a
    // channel's band to keep drawing after its line was hidden (the band plugin didn't know about
    // uPlot's internal per-series `show`). Visibility is now this component's own checkboxes
    // (live L/R) and the overlay list (each stored trace), both driving null-fill instead of
    // uPlot's `show`, so there's exactly one place that decides what's visible.
    legend: { show: false },
    plugins: [minMaxBandPlugin(() => props.spectrum), harmonicMarkersPlugin(theme, interactive)],
  }
}

// The wire payload interleaves (freqHz,min,max) per column. The uPlot series is the max — it never
// misses a harmonic spike the way a mean would (see docs/qa403-app-stack-spec.md §3.1) — and it is
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

function maxTrace(columns: Float32Array, nPoints: number): Float64Array {
  const out = new Float64Array(nPoints)
  for (let i = 0; i < nPoints; i++) out[i] = columns[i * 3 + 2]
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
function peakTrace(map: Map<number, number>, xs: Float64Array | number[]): (number | null)[] {
  const out = new Array<number | null>(xs.length)
  for (let i = 0; i < xs.length; i++) {
    const v = map.get(xs[i])
    out[i] = v === undefined ? null : v
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
  return `${thickLines.value}|${showDataPoints.value}`
}

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
  plot = new uPlot(buildOptions(width, chartHeight.value, nyquistHz, overlays, currentTheme(), true), emptyData, el)

  // uPlot's own double-click handler calls autoScaleX(), which fits the axis to the *data* extent.
  // While a zoomed frame is on screen that extent is the zoom window, so its reset would be a
  // no-op and there'd be no way back out. Ours runs after it (added later on the same element) and
  // restores the real full span.
  plot.over.addEventListener('dblclick', resetZoom)

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
function toTrace(channel: MeasurementChannel) {
  return { freqHz: channel.columns.map((c) => c.freqHz), value: channel.columns.map((c) => c.maxDbv) }
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
    const { columns, header } = frame
    for (let i = 0; i < header.nPoints; i++) consider(columns[i * 3 + 2])
  }

  for (const overlay of props.overlays) {
    if (!overlay.visible) continue
    for (const ch of [0, 1]) {
      const channel = channelByIndex(overlay.detail, ch)
      if (!channel) continue
      for (const c of channel.columns) consider(c.maxDbv)
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
    const { columns, header } = frame
    for (let i = 0; i < header.nPoints; i++) consider(columns[i * 3], columns[i * 3 + 1], columns[i * 3 + 2])
  }

  for (const overlay of props.overlays) {
    if (!overlay.visible) continue
    for (const ch of [0, 1]) {
      const channel = channelByIndex(overlay.detail, ch)
      if (!channel) continue
      for (const c of channel.columns) consider(c.freqHz, c.minDbv, c.maxDbv)
    }
  }

  return { above, below }
})

/** The Frequency range "Autofit" target's upper edge — a fixed step above the current sample
 * rate's Nyquist frequency, not a true data-driven autofit (unlike the dBV range button): 48 kHz
 * -> 25 kHz, 96 kHz -> 50 kHz, 192 kHz -> 100 kHz, and reasonable buckets for anything else. */
function autofitFreqToFor(sampleRateHz: number): number {
  if (sampleRateHz <= 48000) return 25000
  if (sampleRateHz <= 96000) return 50000
  return 100000
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
  // on "Waiting for data…" while Waterfall/Oscilloscope (which have no such L-only assumption) kept
  // working off the very same stream.
  const primary = ch0 ?? ch1
  const firstOverlay = props.overlays[0]?.detail
  const firstOverlayChannel = firstOverlay && (channelByIndex(firstOverlay, 0) ?? channelByIndex(firstOverlay, 1))

  if (!primary && !firstOverlayChannel) return null

  // Only an overview frame describes the whole spectrum; a zoomed one describes the zoom window.
  // The axis must always be built from the former, or zooming would redefine "fully zoomed out".
  // With no live data at all (viewing loaded overlays before ever streaming), fall back to the
  // first overlay — recomputed every call, so switching overlays re-fits it.
  let lo = fullLoFreqHz
  let hi = fullHiFreqHz
  if (primary && (primary.header.flags & STREAM_FLAG_ZOOMED) === 0) {
    lo = primary.loFreqHz
    hi = primary.hiFreqHz
  } else if (!primary && firstOverlayChannel && firstOverlayChannel.columns.length > 0) {
    lo = firstOverlayChannel.columns[0].freqHz
    hi = firstOverlayChannel.columns[firstOverlayChannel.columns.length - 1].freqHz
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
  const left = ch0 && channelVisible(0) ? maxTrace(ch0.columns, ch0.header.nPoints) : nullFill(nPoints)

  // R can only be drawn when it was decimated over the same span as whatever xs came from (the
  // one frame after a zoom where L has already switched and R has not, is the case this guards).
  // When R is itself the primary (xs) source — L not being processed — it's trivially comparable
  // to itself, so the ch0 side of the check is skipped rather than required.
  const rComparable =
    ch1 && channelVisible(1) && ch1.header.nPoints === nPoints &&
    (!ch0 || (ch1.header.flags & STREAM_FLAG_ZOOMED) === (ch0.header.flags & STREAM_FLAG_ZOOMED))
  const right = rComparable ? maxTrace(ch1!.columns, ch1!.header.nPoints) : nullFill(nPoints)

  // Peak-hold: fold this tick's columns into the running maps (only while enabled — leaving it off
  // is how a user keeps yesterday's peak run from bleeding into today's), then read the maps back
  // out onto the same x grid as everything else. Gated the same way as left/right above (visible,
  // channel present) so a hidden or idle channel's peak trace disappears along with its live one.
  if (showPeakHold.value) {
    if (ch0 && channelVisible(0)) updatePeakHold(peakHoldMaps[0], ch0)
    if (rComparable) updatePeakHold(peakHoldMaps[1], ch1!)
  }
  const leftPeak = showPeakHold.value && ch0 && channelVisible(0) ? peakTrace(peakHoldMaps[0], xs) : nullFill(nPoints)
  const rightPeak = showPeakHold.value && rComparable ? peakTrace(peakHoldMaps[1], xs) : nullFill(nPoints)

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
    return [
      overlay.visible && chL ? resample(toTrace(chL), xs) : nullFill(nPoints),
      overlay.visible && chR ? resample(toTrace(chR), xs) : nullFill(nPoints),
    ]
  })
  return { fullLoFreqHz: lo, fullHiFreqHz: hi, data: [xs, left, right, leftPeak, rightPeak, ...overlayData] }
}

function update() {
  const frame = computeFrame()

  // Nothing to draw — no live frame and no overlay to fall back to (the Clear button, or before
  // the very first frame ever arrives). Destroying rather than merely returning matters here: a
  // bare return leaves whatever the last setData() call painted sitting on the canvas, so the
  // chart would keep showing a stale trace under the "Waiting for data…" label in the header. This
  // also clears the stale fullHiFreqHz that return would otherwise leave behind.
  if (!frame) {
    plot?.destroy()
    plot = null
    fullHiFreqHz = -1
    return
  }

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
 * ordering is kept: empty construction, then setData. On screen the x axis is then the data's span,
 * which the server already cut to the panel's window; a headless render of a stored result has the
 * whole stored span as data, so the panel's From/To is applied explicitly — the same setScale the
 * dropdowns make.
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
    u = new uPlot(buildOptions(width, plotHeight, frame.fullHiFreqHz, props.overlays, THEME.light, false), emptyData, host)
    u.setData(frame.data)
    u.setScale('x', xWindow)
    await nextPaint()

    const own = (label: string) => (primaryLabel ? `${primaryLabel} — ${label}` : label)
    const legend: ExportLegendEntry[] = []
    if (props.spectrum[0] && channelVisible(0)) legend.push({ label: own('L'), color: LIVE_COLOR })
    if (props.spectrum[1] && channelVisible(1)) legend.push({ label: own('R'), color: LIVE_COLOR, dash: [6, 3] })
    if (showPeakHold.value) legend.push({ label: own('Peak hold'), color: PEAK_HOLD_COLOR, dash: [2, 2] })
    for (const overlay of props.overlays) {
      if (!overlay.visible) continue
      legend.push({ label: overlay.detail.name, color: fftOverlayColor(props.overlays, overlay.id) })
    }

    return await composeChartPng({
      width,
      height,
      plotCanvas: u.ctx.canvas,
      headerHeight: header,
      title: 'Spectrum (dBV)',
      legend,
    })
  } finally {
    u?.destroy()
    host.remove()
  }
}

defineExpose({ exportPng })

watch(() => props.spectrum, update)
watch(() => props.overlays, update, { deep: true })
watch(visibleL, update)
watch(visibleR, update)
watch(showMinMax, update)
watch(showPeakHold, update)
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

onMounted(() => {
  resizeObserver = new ResizeObserver(handleResize)
  if (containerRef.value) resizeObserver.observe(containerRef.value)
})
onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  clearTimeout(viewportTimer)
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section ref="rootRef" class="relative flex flex-col gap-2 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <ExpandToggle class="absolute right-3 top-3" :target="rootRef" />
    <!-- Three slots — title, cursor readout, zoom state — that never take each other's place, and a
         min-height the Reset zoom button fits inside: the readout used to *replace* the button while
         the cursor was over the plot, and since the button is taller than a line of text the header
         shrank on every hover and the plot below jumped up by the difference. -->
    <div class="min-h-6 pr-7 flex items-center justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
      <span class="shrink-0">Spectrum</span>
      <span v-if="cursorReadout" class="min-w-0 truncate font-mono">
        {{ cursorReadout.freqHz.toFixed(1) }} Hz · {{ cursorReadout.dbv.toFixed(1) }} dBV
      </span>
      <span v-else-if="!spectrum[0] && !spectrum[1]" class="min-w-0 truncate">Waiting for data…</span>
      <span v-else class="flex-1"></span>
      <!-- The only visible sign that the frequency axis is no longer showing the full data span —
           double-clicking does the same thing, but nothing else on screen would say so. -->
      <button
        v-if="zoomed"
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
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
    <div class="relative">
      <div ref="containerRef" class="w-full"></div>
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

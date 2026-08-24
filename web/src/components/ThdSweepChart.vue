<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'
import { useAppTheme } from '../composables/useAppTheme'
import { useLoadOhms } from '../composables/useLoadOhms'
import { formatHz, useSweep } from '../composables/useSweep'
import { freqAxis } from '../freqAxis'
import { percentToDb } from '../sweep/points'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { ThdSweepPoint } from '../api/client'

/**
 * The THD-vs-frequency result chart.
 *
 * Structurally a trimmed SpectrumChart: one uPlot instance created once and fed through setData(),
 * rebuilt only on a theme/series-shape change, and the same shared log-frequency x-axis from
 * freqAxis.ts so a sweep and a spectrum tick their frequency axis identically. What it drops is
 * everything tied to a streaming min/max spectrum — no band plugin, no peak hold, no viewport
 * reporting — because a sweep point is a single value per frequency, not a decimated column.
 *
 * The y axis has two modes. Percent is logarithmic (distr: 3): distortion spans four or five
 * decades across a sweep and a linear axis renders all of it as a flat line on zero. dB is the same
 * data as dBc (20·log10(pct/100)) on a linear axis, which is the same shape — the toggle is about
 * which numbers people want to read off, matching the previous app's two graph types.
 */

const props = defineProps<{
  /** Per-trace visibility. Every trace is individually switchable from the display panel. */
  showMagnitude: boolean
  showThd: boolean
  showD2: boolean
  showD3: boolean
  showD4: boolean
  showD5: boolean
  showD6Plus: boolean
  showNoiseFloor: boolean
  showLeft: boolean
  showRight: boolean
  unit: 'percent' | 'db'
  thickLines: boolean
  showDataPoints: boolean
  /**
   * Axis bounds, set from the display panel rather than fitted to the data.
   *
   * The two y axes carry their own pair. They are not two views of one window: the percent axis
   * bottoms out at a distortion figure and its top is 100 % at most, while the dB axis has to
   * reach *above* 0 dB for the Magnitude trace, which no percentage can express. Deriving one
   * from the other through percentToDb made the dB top unreachable.
   */
  yMaxPercent: number
  yMinPercent: number
  yTopDb: number
  yBottomDb: number
  xFromHz: number
  xToHz: number
  /**
   * Stored sweeps plotted alongside the live one, checked on in the History list — the same
   * OverlayEntry list SpectrumChart draws, filtered to the ones that actually carry sweep points.
   */
  overlays: OverlayEntry[]
  /**
   * The point currently "selected" per channel — the last one measured while a sweep runs, or the
   * one nearest the chart cursor once it's done (App.vue's selectedSweepPointFor, the same state
   * that drives the small FFT/scope preview charts). Drives the metrics pane below the chart, which
   * replaces the legend's old per-series hover values with the full reading at one frequency: F,
   * THD, Magnitude, D2..D6+, power and noise floor, one row per channel that has data.
   */
  selectedLeft: ThdSweepPoint | null
  selectedRight: ThdSweepPoint | null
}>()

/**
 * The frequency under the pointer, or null once it leaves the plot area — App.vue uses this to
 * drive the small FFT/scope preview charts once a sweep is done ("nearest point to the cursor").
 * uPlot's own cursor.idx is an index into the shared x (frequency) array built in update(), so it
 * names an actual measured/plotted frequency, not an interpolated pixel position.
 */
const emit = defineEmits<{ cursorFreq: [freqHz: number | null] }>()

const { points } = useSweep()
const { isDark } = useAppTheme()
const { loadOhms } = useLoadOhms()

const containerRef = ref<HTMLDivElement | null>(null)

const CHART_HEIGHT = 560

/**
 * One entry per drawn trace. Order is fixed and shared by buildOptions and update() — the series
 * array and the data array have to agree slot for slot, and deriving both from this list is what
 * keeps them from drifting apart when a trace is added.
 *
 * Hue identifies the *quantity*, matching the previous app's palette (THD black, D2 blue, D3
 * orange, D4 green, D5 red, D6+ purple) so a trace is recognisable at a glance from either app.
 * Channel is carried by the line style instead — see CHANNEL_DASH. Each colour is a light/dark
 * pair because near-black THD is invisible on a dark background and the saturated harmonic hues
 * are hard to read against one.
 */
const TRACES = [
  // Magnitude is the measured fundamental relative to the generator setpoint, i.e. the DUT's
  // frequency response riding along with the sweep. It is already a dB quantity, so it has no
  // meaning on the percent axis and `dbOnly` keeps it off it -- the previous app greys its
  // checkbox out on the D% graph for the same reason.
  { key: 'magnitude', label: 'Magn', width: 1, color: ['#be185d', '#f472b6'], show: 'showMagnitude', dbOnly: true },
  { key: 'thd', label: 'THD', width: 1, color: ['#111827', '#e5e7eb'], show: 'showThd' },
  { key: 'd2', label: 'D2', width: 0.8, color: ['#1d6fd0', '#60a5fa'], show: 'showD2' },
  { key: 'd3', label: 'D3', width: 0.8, color: ['#e07b16', '#fbbf24'], show: 'showD3' },
  { key: 'd4', label: 'D4', width: 0.8, color: ['#1a7a3c', '#4ade80'], show: 'showD4' },
  { key: 'd5', label: 'D5', width: 0.8, color: ['#c62828', '#f87171'], show: 'showD5' },
  { key: 'd6plus', label: 'D6+', width: 0.8, color: ['#6b3fa0', '#c084fc'], show: 'showD6Plus' },
  // Not in the previous app's legend, so it gets a hue that stays clear of the six above.
  { key: 'noise', label: 'Noise', width: 0.8, color: ['#525252', '#a3a3a3'], show: 'showNoiseFloor' },
] as const

type TraceKey = (typeof TRACES)[number]['key']
type TraceToggle = (typeof TRACES)[number]['show']

/**
 * Left solid, right dashed. The channel has to be encoded in something other than colour now that
 * colour means "which harmonic": a stereo sweep draws the same twelve hues twice, and telling L
 * from R by line style keeps the palette readable instead of needing sixteen distinct colours.
 */
const CHANNEL_DASH: (number[] | undefined)[] = [undefined, [6, 3]]
const CHANNEL_LABELS = ['L', 'R']


const THEME = {
  dark: { axis: '#a3a3a3', grid: '#404040' },
  light: { axis: '#525252', grid: '#e5e5e5' },
} as const

function currentTheme() {
  return isDark.value ? THEME.dark : THEME.light
}

/** Picks the light or dark half of a [light, dark] colour pair. */
function themed(pair: readonly [string, string] | string[]): string {
  return isDark.value ? pair[1] : pair[0]
}

const activeChannels = computed(() => {
  const channels: number[] = []
  if (props.showLeft) channels.push(0)
  if (props.showRight) channels.push(1)
  return channels
})

const activeTraces = computed(() =>
  TRACES.filter((t) => props[t.show as TraceToggle] && (!('dbOnly' in t) || props.unit === 'db')),
)

/**
 * The overlays actually drawable here: visible, and carrying stored sweep points. An FFT result
 * checked on while the sweep screen is open has no points and is skipped rather than drawn empty.
 */
const activeOverlays = computed(() => props.overlays.filter((o) => o.visible && o.sweepPoints?.length))

/**
 * Distinct sweep frequencies, ascending — the shared x array every series is aligned to.
 *
 * The union of the live sweep's grid and every visible overlay's, not just the live one: a stored
 * sweep run at different start/end/steps lands on different frequencies, and uPlot aligns all
 * series to one x array, so anything missing from it simply would not be drawn. Frequencies the
 * live sweep has no point at read back as gaps, which is what they are.
 */
const frequencies = computed(() => {
  const all = new Set(points.value.map((p) => p.freqHz))
  for (const overlay of activeOverlays.value)
    for (const point of overlay.sweepPoints ?? []) all.add(point.freqHz)
  return [...all].sort((a, b) => a - b)
})

const hasData = computed(() => frequencies.value.length > 0)

function valueOf(point: ThdSweepPoint, key: TraceKey): number {
  switch (key) {
    case 'magnitude':
      return point.gainDb
    case 'thd':
      return point.thdPercent
    case 'd2':
    case 'd3':
    case 'd4':
    case 'd5': {
      const order = Number(key.slice(1)) - 2
      return point.harmonicPercent[order] ?? Number.NaN
    }
    case 'd6plus':
      return point.d6PlusPercent
    case 'noise':
      return point.noisePercent
  }
}

/**
 * A percentage as the y axis currently wants it. Non-positive values become null (a gap) rather
 * than 0: on the log percent axis 0 has no position at all, and on the dB axis it would plunge the
 * whole scale to the -infinity floor and flatten every real trace against the top.
 */
function toY(value: number, key: TraceKey): number | null {
  if (!Number.isFinite(value)) return null

  // Magnitude is a gain in dB and goes straight onto the axis -- it is legitimately negative or
  // zero, so it must not go through the percent guard below, and there is nothing to convert.
  if (key === 'magnitude') return value

  if (value <= 0) return null
  return props.unit === 'db' ? percentToDb(value) : value
}

/** The y window in axis units, always [bottom, top]. */
function yRange(): [number, number] {
  return props.unit === 'db' ? [props.yBottomDb, props.yTopDb] : [props.yMinPercent, props.yMaxPercent]
}

function buildOptions(width: number): uPlot.Options {
  const theme = currentTheme()
  const lineWidth = props.thickLines ? 2 : 1
  const points = { show: props.showDataPoints, size: props.thickLines ? 7 : 5 }

  const series: uPlot.Series[] = [{}]
  // uPlot's legend swatch is a CSS border and legend.markers.dash defaults to the literal string
  // "solid" -- it never looks at series.dash. Without this the plot would draw R dashed while the
  // legend showed every entry solid, which is exactly the distinction the legend exists to explain.
  const markerDash: string[] = ['solid']
  for (const ch of activeChannels.value) {
    for (const trace of activeTraces.value) {
      series.push({
        label: `${CHANNEL_LABELS[ch]} ${trace.label}`,
        stroke: themed(trace.color),
        width: lineWidth * trace.width,
        dash: CHANNEL_DASH[ch] ? [...CHANNEL_DASH[ch]] : undefined,
        points,
        scale: 'y',
      })
      markerDash.push(CHANNEL_DASH[ch] ? 'dashed' : 'solid')
    }
  }

  // Overlays follow SpectrumChart's convention rather than this chart's own: colour identifies the
  // stored measurement, dash identifies its channel. Hue cannot keep meaning "which quantity" here
  // — two runs of the same sweep would draw two identical black THD traces — and telling the runs
  // apart is the entire point of putting them on one chart. Within one overlay the quantity is read
  // off the legend, which is why only the *active* traces get an overlay series: whatever is
  // checked on is what is being compared.
  for (const overlay of activeOverlays.value) {
    for (const ch of activeChannels.value) {
      for (const trace of activeTraces.value) {
        series.push({
          label: `${overlay.detail.name} ${CHANNEL_LABELS[ch]} ${trace.label}`,
          stroke: overlay.color,
          width: lineWidth * trace.width,
          dash: CHANNEL_DASH[ch] ? [...CHANNEL_DASH[ch]] : undefined,
          points,
          scale: 'y',
        })
        markerDash.push(CHANNEL_DASH[ch] ? 'dashed' : 'solid')
      }
    }
  }

  return {
    width,
    height: CHART_HEIGHT,
    scales: {
      // Both axes are pinned to the dropdowns rather than fitted to the data. auto:false plus a
      // range() that ignores dataMin/dataMax is what makes that stick — otherwise every setData()
      // re-fits, and a sweep calls setData once per point, so the axes would crawl as it ran.
      x: {
        time: false,
        distr: 3,
        log: 10,
        auto: false,
        range: () => [props.xFromHz, props.xToHz],
      },
      // Percent is log (distr 3) across up to seven decades; dB is the same numbers on a linear
      // axis, so the bounds go through percentToDb and swap ends -- a smaller percentage is a more
      // negative dB, but it is still the bottom of the window.
      y:
        props.unit === 'db'
          ? { auto: false, range: () => yRange() }
          : { distr: 3, log: 10, auto: false, range: () => yRange() },
    },
    axes: [
      {
        ...freqAxis(),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
      {
        scale: 'y',
        size: 62,
        values: (_u, vals) => vals.map((v) => (v == null ? null : formatYTick(v))),
        stroke: theme.axis,
        grid: { stroke: theme.grid, width: 1 },
        ticks: { stroke: theme.grid, width: 1 },
      },
    ],
    series,
    cursor: { points: { show: false } },
    // Display only. uPlot's legend rows toggle their series on click, and now that the display
    // panel owns visibility that click would silently desync the two -- a trace off in the chart
    // with its checkbox still ticked, and nothing to un-stick it but another click on the legend.
    // live:false drops the per-series value column -- the metrics pane below the chart is what
    // shows values now (frequency, THD, D2..D6+, power, noise floor, one row per channel), so the
    // legend's only job is naming which line is which.
    legend: {
      show: true,
      live: false,
      markers: { dash: (_u: uPlot, i: number) => markerDash[i] ?? 'solid' },
    },
    hooks: {
      // u.cursor.idx indexes the shared x (frequency) array from update() -- not a pixel-derived
      // value, so it always names a frequency something was actually plotted at. null once the
      // pointer leaves the plot area (uPlot clears idx on mouseleave), which is the "nothing
      // hovered" state the small preview charts fall back from.
      setCursor: [
        (u: uPlot) => {
          const idx = u.cursor.idx
          emit('cursorFreq', idx != null && idx >= 0 ? (frequencies.value[idx] ?? null) : null)
        },
      ],
    },
  }
}

/** 0.001 / 0.01 / 0.1 / 1 / 10 for percent (no trailing-zero noise across five decades), plain integers for dB. */
function formatYTick(v: number): string {
  if (props.unit === 'db') return `${v}`
  if (v >= 1) return `${v}`
  const decimals = Math.min(6, Math.max(0, Math.ceil(-Math.log10(v))))
  return v.toFixed(decimals)
}

/**
 * A distortion/noise percentage as the metrics pane's active unit wants it — the same
 * percentToDb conversion the chart itself plots with, so a row's numbers always match wherever
 * the corresponding trace actually sits on screen. Unlike toY (which the chart uses), this never
 * returns null for a non-positive value: the pane reports a real reading, not a gap in a line.
 */
function formatDistortion(percent: number): string {
  if (!Number.isFinite(percent)) return '—'
  if (props.unit === 'db') return `${percentToDb(percent).toFixed(2)} dB`
  if (!(percent > 0)) return '0%'
  const decimals = percent >= 1 ? 2 : Math.min(6, Math.max(0, Math.ceil(-Math.log10(percent)) + 1))
  return `${percent.toFixed(decimals)}%`
}

/** Magnitude is a gain in dB on both unit modes — see TRACES' own remarks on why it has no percent form. */
function formatMagnitude(gainDb: number): string {
  return Number.isFinite(gainDb) ? `${gainDb.toFixed(2)} dB` : '—'
}

/** "1 mW (8 Ω)" — the load a sweep's Power figure was computed against, from the same field SweepConfigPanel's own load input edits. */
function formatPower(watt: number): string {
  if (!Number.isFinite(watt)) return '—'
  const mw = watt * 1000
  const value = mw >= 100 ? mw.toFixed(0) : mw >= 10 ? mw.toFixed(1) : mw.toFixed(2)
  return `${value} mW (${loadOhms.value} Ω)`
}

/** One channel's readout row for the metrics pane below the chart — F, THD, Magnitude, D2..D6+, power, noise floor. */
function metricsFor(point: ThdSweepPoint): { label: string; value: string }[] {
  const [d2, d3, d4, d5] = point.harmonicPercent
  return [
    { label: 'THD', value: formatDistortion(point.thdPercent) },
    { label: 'Magnitude', value: formatMagnitude(point.gainDb) },
    { label: 'D2', value: formatDistortion(d2 ?? Number.NaN) },
    { label: 'D3', value: formatDistortion(d3 ?? Number.NaN) },
    { label: 'D4', value: formatDistortion(d4 ?? Number.NaN) },
    { label: 'D5', value: formatDistortion(d5 ?? Number.NaN) },
    { label: 'D6+', value: formatDistortion(point.d6PlusPercent) },
    { label: 'Power', value: formatPower(point.powerWatt) },
    { label: 'Noise floor', value: formatDistortion(point.noisePercent) },
  ]
}

/** The metrics pane's two rows — colours match the small preview charts' own left-blue/right-red convention. */
const channelRows = computed(() => [
  { channel: 0, point: props.selectedLeft, label: 'Left channel', colorClass: 'text-blue-600 dark:text-blue-400' },
  { channel: 1, point: props.selectedRight, label: 'Right channel', colorClass: 'text-red-600 dark:text-red-400' },
])

let plot: uPlot | null = null

// Everything baked into series/axes at construction time — uPlot has no live setter for any of it,
// so a change here has to destroy and rebuild rather than go through setData().
let plotSignature = ''
function signature(): string {
  return [
    activeChannels.value.join(','),
    activeTraces.value.map((t) => t.key).join(','),
    // Series count and colours are baked in at construction, so checking an overlay on or off has
    // to rebuild the plot the same way adding a trace does.
    activeOverlays.value.map((o) => o.id).join(','),
    props.unit,
    props.thickLines,
    props.showDataPoints,
    isDark.value,
    // The range functions above close over props and would return the new bounds on their own, but
    // uPlot only calls them on setData/setScale -- so a dropdown change has to force a rebuild to
    // be visible. A sweep is ~50 points; rebuilding costs nothing next to tracking scale updates.
    props.yMaxPercent,
    props.yMinPercent,
    props.xFromHz,
    props.xToHz,
    props.yTopDb,
    props.yBottomDb,
  ].join('|')
}

function ensurePlot() {
  const el = containerRef.value
  if (!el) return

  const next = signature()
  if (plot && plotSignature === next) return

  plot?.destroy()
  plotSignature = next

  const width = el.getBoundingClientRect().width || 800
  const blocks = 1 + activeOverlays.value.length // the live sweep, plus one block per overlay
  const slots = 1 + blocks * activeChannels.value.length * activeTraces.value.length
  // Same AlignedData typing wrinkle as SpectrumChart: a runtime-sized array cannot be contextually
  // typed against uPlot's tuple type. The runtime shape is correct; only the static type needs it.
  const empty = Array.from({ length: slots }, () => [] as number[]) as unknown as uPlot.AlignedData
  plot = new uPlot(buildOptions(width), empty, el)
}

function update() {
  ensurePlot()
  if (!plot) return

  const freqs = frequencies.value
  if (freqs.length === 0) {
    plot.setData([[]] as unknown as uPlot.AlignedData)
    return
  }

  const data: (number | null)[][] = [freqs]
  pushSeriesData(data, freqs, points.value)
  // Order has to match buildOptions' series order exactly — live block first, then one block per
  // active overlay in the same iteration order.
  for (const overlay of activeOverlays.value) pushSeriesData(data, freqs, overlay.sweepPoints ?? [])

  plot.setData(data as unknown as uPlot.AlignedData)
}

/**
 * Appends one channel x trace block of y arrays for a set of sweep points, aligned to `freqs`.
 * Shared by the live sweep and every overlay so a stored sweep goes through the same valueOf/toY
 * conversion the running one does.
 */
function pushSeriesData(data: (number | null)[][], freqs: number[], sweepPoints: ThdSweepPoint[]) {
  // Index the points once per block rather than searching per series per frequency: a stereo
  // sweep with nine traces would otherwise be O(traces x freqs x points).
  const byChannel = new Map<number, Map<number, ThdSweepPoint>>()
  for (const point of sweepPoints) {
    let map = byChannel.get(point.channel)
    if (!map) byChannel.set(point.channel, (map = new Map()))
    map.set(point.freqHz, point)
  }

  for (const ch of activeChannels.value) {
    const map = byChannel.get(ch)
    for (const trace of activeTraces.value) {
      data.push(freqs.map((hz) => (map ? toY(valueOf(map.get(hz) ?? emptyPoint, trace.key), trace.key) : null)))
    }
  }
}

/** Stand-in for a frequency a channel has no point at yet — every field reads as "no value". */
const emptyPoint: ThdSweepPoint = {
  freqHz: 0,
  channel: 0,
  fundamentalDbv: Number.NaN,
  gainDb: Number.NaN,
  powerWatt: Number.NaN,
  thdPercent: Number.NaN,
  thdNPercent: Number.NaN,
  snrDb: Number.NaN,
  harmonicPercent: [],
  d6PlusPercent: Number.NaN,
  noisePercent: Number.NaN,
  previewFft: null,
  previewScope: null,
}

watch(points, update)
watch(
  () => signature(),
  () => update(),
)

function handleResize() {
  if (plot && containerRef.value)
    plot.setSize({ width: containerRef.value.getBoundingClientRect().width, height: CHART_HEIGHT })
}

onMounted(() => {
  window.addEventListener('resize', handleResize)
  update()
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResize)
  plot?.destroy()
  plot = null
})
</script>

<template>
  <section class="flex flex-col gap-2 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <div class="flex items-center justify-between text-xs text-neutral-500 dark:text-neutral-400">
      <span>Distortion vs frequency</span>
      <span v-if="!hasData">No sweep data yet — press Run sweep.</span>
    </div>
    <div ref="containerRef" class="sweep-chart w-full"></div>

    <!-- The full reading at the selected frequency, one row per channel that has data — replaces
         the legend's old per-series hover values (see legend.live:false above). -->
    <div v-if="selectedLeft || selectedRight" class="flex flex-col gap-2 border-t border-neutral-200 pt-2 dark:border-neutral-800">
      <div v-for="row in channelRows" :key="row.channel">
        <template v-if="row.point">
          <div class="flex flex-wrap items-start gap-x-5 gap-y-1.5 text-xs">
            <div class="font-mono text-neutral-700 dark:text-neutral-300">F: {{ formatHz(row.point.freqHz) }}</div>
            <div v-for="m in metricsFor(row.point)" :key="m.label" class="flex flex-col">
              <span class="text-neutral-400 dark:text-neutral-500">{{ m.label }}</span>
              <span class="font-mono text-neutral-800 dark:text-neutral-100">{{ m.value }}</span>
            </div>
          </div>
          <div class="mt-1 flex items-center gap-1.5 text-xs" :class="row.colorClass">
            <span class="h-1.5 w-1.5 rounded-full bg-current"></span>
            {{ row.label }}
          </div>
        </template>
      </div>
    </div>
  </section>
</template>

<style scoped>
/*
 * uPlot has no option to make its legend read-only, and its rows toggle their series on click. The
 * display panel owns trace visibility now, so a stray click there would leave a trace hidden with
 * its checkbox still ticked and no way back except clicking the legend again. :deep() because the
 * legend is created by uPlot, not by this template, so it never gets the scope attribute.
 */
.sweep-chart :deep(.u-legend) {
  pointer-events: none;
}

/*
 * uPlot's default legend swatch is a 1em square (see its own uPlot.min.css .u-marker rule) --
 * reshaped into a short horizontal bar so it reads as "a line", matching what it actually
 * represents. Same element for a dashed (right-channel) series; uPlot switches that one to a
 * border-based dash pattern instead of a solid background, which this sizing still applies to.
 */
.sweep-chart :deep(.u-legend .u-marker) {
  width: 14px;
  height: 2px;
  border-radius: 0;
}
</style>

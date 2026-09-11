<script setup lang="ts">
import { computed } from 'vue'
import { freqTickLabels, freqTickSplits } from '../../freqAxis'

/**
 * A small static chart for the manual, drawn as inline SVG so it follows the page's theme, scales
 * with its column and ships no image. Not uPlot: nothing here is live, zoomable or hovered, and the
 * manual's pages should stay plain markup.
 *
 * The x axis ticks through the app's own freqTickSplits/freqTickLabels when it is a log frequency
 * axis, so a manual chart is labelled exactly the way the screen it describes is.
 */

export interface ManualSeries {
  name: string
  points: readonly (readonly [number, number])[]
  /** One of the palette keys below; defaults to the series' position in the list. */
  color?: 'left' | 'right' | 'gain' | 'phase' | 'coherence' | 'amber' | 'grey'
  /** Which vertical axis the series is drawn against. */
  axis?: 'y' | 'y2'
  /**
   * `stem` draws a vertical line from the bottom of the plot to each point — a tone in a spectrum.
   * `points` draws a dot per point and no line — a stepped sweep's readings.
   */
  kind?: 'line' | 'stem' | 'points'
  dashed?: boolean
}

export interface ManualMarker {
  /** A vertical line at this x, or a horizontal one at this y. Exactly one of the two. */
  x?: number
  y?: number
  axis?: 'y' | 'y2'
  label?: string
  color?: ManualSeries['color']
  dashed?: boolean
}

const props = withDefaults(
  defineProps<{
    series: ManualSeries[]
    xMin: number
    xMax: number
    yMin: number
    yMax: number
    /** Log frequency axis (the default) or a linear one. */
    xLog?: boolean
    /** Log left axis — THD in percent, ohms. Decade ticks; yMin and yMax must be positive. */
    yLog?: boolean
    xLabel?: string
    yLabel?: string
    y2Min?: number
    y2Max?: number
    y2Label?: string
    /** Fixed tick steps, when the 1/2/5 default does not suit — 90 for a phase axis in degrees. */
    yStep?: number
    y2Step?: number
    markers?: ManualMarker[]
    /** Caption under the plot. */
    caption?: string
    height?: number
  }>(),
  { xLog: true, yLog: false, height: 240, markers: () => [] },
)

const WIDTH = 640
const PAD_LEFT = 52
const PAD_TOP = 12
const PAD_BOTTOM = 34

const hasY2 = computed(() => props.y2Min !== undefined && props.y2Max !== undefined)
const padRight = computed(() => (hasY2.value ? 52 : 16))
const plotWidth = computed(() => WIDTH - PAD_LEFT - padRight.value)
const plotHeight = computed(() => props.height - PAD_TOP - PAD_BOTTOM)

function xPos(x: number): number {
  const t = props.xLog
    ? (Math.log10(x) - Math.log10(props.xMin)) / (Math.log10(props.xMax) - Math.log10(props.xMin))
    : (x - props.xMin) / (props.xMax - props.xMin)
  return PAD_LEFT + t * plotWidth.value
}

function yPos(y: number, axis: 'y' | 'y2' = 'y'): number {
  const min = axis === 'y2' ? props.y2Min! : props.yMin
  const max = axis === 'y2' ? props.y2Max! : props.yMax
  const t =
    axis === 'y' && props.yLog
      ? (Math.log10(y) - Math.log10(min)) / (Math.log10(max) - Math.log10(min))
      : (y - min) / (max - min)
  return PAD_TOP + (1 - t) * plotHeight.value
}

/** Decade ticks for a log axis, labelled without exponent notation (0.001, 0.01, … 10, 100). */
function decadeTicks(min: number, max: number): { y: number; label: string }[] {
  const out: { y: number; label: string }[] = []
  for (let e = Math.ceil(Math.log10(min) - 1e-9); e <= Math.floor(Math.log10(max) + 1e-9); e++) {
    const v = Math.pow(10, e)
    out.push({ y: v, label: e >= 0 ? v.toFixed(0) : v.toFixed(-e) })
  }
  return out
}

/** 1/2/5 steps aiming at about six labelled ticks, unless a step is given. */
function linearTicks(min: number, max: number, target = 6, fixedStep?: number): number[] {
  const span = max - min
  if (!(span > 0)) return [min]
  const rough = span / target
  const magnitude = Math.pow(10, Math.floor(Math.log10(rough)))
  const step =
    fixedStep ?? ([1, 2, 5, 10].map((m) => m * magnitude).find((s) => span / s <= target) ?? 10 * magnitude)
  const ticks: number[] = []
  for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-9; v += step) {
    ticks.push(Math.abs(v) < step * 1e-9 ? 0 : v)
  }
  return ticks
}

function formatLinear(v: number, ticks: number[]): string {
  const gap = ticks.length > 1 ? Math.abs(ticks[1]! - ticks[0]!) : Math.abs(v) || 1
  const decimals = Math.max(0, Math.ceil(-Math.log10(gap)))
  return v.toFixed(decimals)
}

const xTicks = computed<{ x: number; label: string | null }[]>(() => {
  if (props.xLog) {
    const splits = freqTickSplits(props.xMin, props.xMax, plotWidth.value, 48)
    const labels = freqTickLabels(splits, props.xMin, props.xMax)
    return splits.map((v, i) => ({ x: v, label: labels[i] ?? null }))
  }
  const ticks = linearTicks(props.xMin, props.xMax, 8)
  return ticks.map((v) => ({ x: v, label: formatLinear(v, ticks) }))
})

const yTicks = computed(() => {
  if (props.yLog) return decadeTicks(props.yMin, props.yMax)
  const ticks = linearTicks(props.yMin, props.yMax, 6, props.yStep)
  return ticks.map((v) => ({ y: v, label: formatLinear(v, ticks) }))
})

const y2Ticks = computed(() => {
  if (!hasY2.value) return []
  const ticks = linearTicks(props.y2Min!, props.y2Max!, 6, props.y2Step)
  return ticks.map((v) => ({ y: v, label: formatLinear(v, ticks) }))
})

const PALETTE_ORDER: NonNullable<ManualSeries['color']>[] = ['left', 'right', 'phase', 'coherence', 'amber', 'grey']

function colorClass(color: ManualSeries['color'] | undefined, index: number): string {
  return `manual-chart-${color ?? PALETTE_ORDER[index % PALETTE_ORDER.length]}`
}

function inRange(x: number, y: number, axis: 'y' | 'y2'): boolean {
  if (!(x >= props.xMin && x <= props.xMax) || !Number.isFinite(y)) return false
  if (axis === 'y' && props.yLog) return y > 0
  // A point one full span outside the axis still gets a path segment, so a line leaving the plot
  // exits at the right angle; the clip path hides the rest.
  const min = axis === 'y2' ? props.y2Min! : props.yMin
  const max = axis === 'y2' ? props.y2Max! : props.yMax
  return y >= min - (max - min) && y <= max + (max - min)
}

function linePath(s: ManualSeries): string {
  const axis = s.axis ?? 'y'
  let d = ''
  let pen = false
  for (const [x, y] of s.points) {
    if (!inRange(x, y, axis)) {
      pen = false
      continue
    }
    d += `${pen ? 'L' : 'M'}${xPos(x).toFixed(1)},${yPos(y, axis).toFixed(1)}`
    pen = true
  }
  return d
}

const clipId = `manual-chart-clip-${Math.random().toString(36).slice(2, 9)}`
</script>

<template>
  <figure class="manual-chart">
    <svg :viewBox="`0 0 ${WIDTH} ${height}`" role="img" :aria-label="caption">
      <defs>
        <clipPath :id="clipId">
          <rect :x="PAD_LEFT" :y="PAD_TOP" :width="plotWidth" :height="plotHeight" />
        </clipPath>
      </defs>

      <!-- Grid -->
      <g class="manual-chart-grid">
        <line
          v-for="t in xTicks"
          :key="`xg${t.x}`"
          :x1="xPos(t.x)"
          :x2="xPos(t.x)"
          :y1="PAD_TOP"
          :y2="PAD_TOP + plotHeight"
        />
        <line
          v-for="t in yTicks"
          :key="`yg${t.y}`"
          :x1="PAD_LEFT"
          :x2="PAD_LEFT + plotWidth"
          :y1="yPos(t.y)"
          :y2="yPos(t.y)"
        />
      </g>

      <!-- Markers: reference lines with a label at one end -->
      <g class="manual-chart-markers">
        <template v-for="(m, i) in markers" :key="`m${i}`">
          <template v-if="m.x !== undefined">
            <line
              :class="[colorClass(m.color ?? 'grey', 0), { dashed: m.dashed !== false }]"
              :x1="xPos(m.x)"
              :x2="xPos(m.x)"
              :y1="PAD_TOP"
              :y2="PAD_TOP + plotHeight"
            />
            <text
              v-if="m.label"
              :class="colorClass(m.color ?? 'grey', 0)"
              :x="xPos(m.x) + 4"
              :y="PAD_TOP + 12 + (i % 3) * 12"
            >
              {{ m.label }}
            </text>
          </template>
          <template v-else-if="m.y !== undefined">
            <line
              :class="[colorClass(m.color ?? 'grey', 0), { dashed: m.dashed !== false }]"
              :x1="PAD_LEFT"
              :x2="PAD_LEFT + plotWidth"
              :y1="yPos(m.y, m.axis ?? 'y')"
              :y2="yPos(m.y, m.axis ?? 'y')"
            />
            <text
              v-if="m.label"
              :class="colorClass(m.color ?? 'grey', 0)"
              :x="PAD_LEFT + plotWidth - 4"
              :y="yPos(m.y, m.axis ?? 'y') - 3"
              text-anchor="end"
            >
              {{ m.label }}
            </text>
          </template>
        </template>
      </g>

      <!-- Series -->
      <g :clip-path="`url(#${clipId})`">
        <template v-for="(s, i) in series" :key="s.name">
          <g v-if="s.kind === 'stem'" :class="colorClass(s.color, i)" class="manual-chart-stems">
            <template v-for="([x, y], j) in s.points" :key="j">
              <line
                v-if="inRange(x, y, s.axis ?? 'y')"
                :x1="xPos(x)"
                :x2="xPos(x)"
                :y1="PAD_TOP + plotHeight"
                :y2="yPos(y, s.axis ?? 'y')"
              />
            </template>
          </g>
          <g v-else-if="s.kind === 'points'" :class="colorClass(s.color, i)" class="manual-chart-points">
            <template v-for="([x, y], j) in s.points" :key="j">
              <circle v-if="inRange(x, y, s.axis ?? 'y')" :cx="xPos(x)" :cy="yPos(y, s.axis ?? 'y')" r="2.4" />
            </template>
          </g>
          <path v-else :class="[colorClass(s.color, i), { dashed: s.dashed }]" class="manual-chart-line" :d="linePath(s)" />
        </template>
      </g>

      <!-- Axes -->
      <g class="manual-chart-axes">
        <rect :x="PAD_LEFT" :y="PAD_TOP" :width="plotWidth" :height="plotHeight" fill="none" />
        <template v-for="t in xTicks" :key="`xl${t.x}`">
          <text v-if="t.label" :x="xPos(t.x)" :y="PAD_TOP + plotHeight + 14" text-anchor="middle">{{ t.label }}</text>
        </template>
        <text v-for="t in yTicks" :key="`yl${t.y}`" :x="PAD_LEFT - 6" :y="yPos(t.y) + 3.5" text-anchor="end">
          {{ t.label }}
        </text>
        <text
          v-for="t in y2Ticks"
          :key="`y2l${t.y}`"
          :x="PAD_LEFT + plotWidth + 6"
          :y="yPos(t.y, 'y2') + 3.5"
          text-anchor="start"
        >
          {{ t.label }}
        </text>
        <text v-if="xLabel" :x="PAD_LEFT + plotWidth / 2" :y="height - 6" text-anchor="middle">{{ xLabel }}</text>
        <text
          v-if="yLabel"
          :transform="`translate(12 ${PAD_TOP + plotHeight / 2}) rotate(-90)`"
          text-anchor="middle"
        >
          {{ yLabel }}
        </text>
        <text
          v-if="y2Label && hasY2"
          :transform="`translate(${WIDTH - 8} ${PAD_TOP + plotHeight / 2}) rotate(90)`"
          text-anchor="middle"
        >
          {{ y2Label }}
        </text>
      </g>
    </svg>

    <!-- Legend -->
    <div v-if="series.length > 1" class="manual-chart-legend">
      <span v-for="(s, i) in series" :key="s.name" :class="colorClass(s.color, i)">
        <svg width="18" height="8" aria-hidden="true">
          <line x1="0" y1="4" x2="18" y2="4" :class="{ dashed: s.dashed }" />
        </svg>
        <span>{{ s.name }}</span>
      </span>
    </div>
    <figcaption v-if="caption">{{ caption }}</figcaption>
  </figure>
</template>

<style>
/* Unscoped, like ManualDialog's .manual-prose rules: one place decides how every manual chart
   looks, and the palette mirrors the app's own charts (FrequencyResponseChart.vue's colours). */
.manual-chart {
  margin: 0.5rem 0 1rem;
  --mc-left: #2563eb;
  --mc-right: #ea580c;
  --mc-gain: #111827;
  --mc-phase: #7c3aed;
  --mc-coherence: #059669;
  --mc-amber: #d97706;
  --mc-grey: #9ca3af;
  --mc-grid: #e5e7eb;
  --mc-axis: #4b5563;
}
.dark .manual-chart {
  --mc-left: #60a5fa;
  --mc-right: #fb923c;
  --mc-gain: #f3f4f6;
  --mc-phase: #a78bfa;
  --mc-coherence: #34d399;
  --mc-amber: #fbbf24;
  --mc-grey: #6b7280;
  --mc-grid: #374151;
  --mc-axis: #9ca3af;
}
.manual-chart svg {
  display: block;
  width: 100%;
  height: auto;
  font-size: 11px;
  font-family: ui-sans-serif, system-ui, sans-serif;
}
.manual-chart-grid line {
  stroke: var(--mc-grid);
  stroke-width: 1;
}
.manual-chart-axes rect {
  stroke: var(--mc-grid);
}
.manual-chart-axes text {
  fill: var(--mc-axis);
}
.manual-chart-line {
  fill: none;
  stroke-width: 1.6;
  stroke-linejoin: round;
}
.manual-chart-stems line {
  stroke-width: 1.6;
}
.manual-chart-points circle {
  fill: currentColor;
  stroke: none;
}
.manual-chart-markers line {
  stroke-width: 1;
}
.manual-chart-markers text {
  font-size: 10px;
  fill: currentColor;
  stroke: none;
}
.manual-chart .dashed {
  stroke-dasharray: 4 3;
}
/* A colour class sets the stroke and the current colour only; what is filled — a point, a label —
   says so itself via currentColor, so a line path can keep fill: none whatever colour it is. */
.manual-chart-left {
  stroke: var(--mc-left);
  color: var(--mc-left);
}
.manual-chart-right {
  stroke: var(--mc-right);
  color: var(--mc-right);
}
.manual-chart-gain {
  stroke: var(--mc-gain);
  color: var(--mc-gain);
}
.manual-chart-phase {
  stroke: var(--mc-phase);
  color: var(--mc-phase);
}
.manual-chart-coherence {
  stroke: var(--mc-coherence);
  color: var(--mc-coherence);
}
.manual-chart-amber {
  stroke: var(--mc-amber);
  color: var(--mc-amber);
}
.manual-chart-grey {
  stroke: var(--mc-grey);
  color: var(--mc-grey);
}
.manual-chart-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem 1rem;
  font-size: 0.75rem;
  color: var(--mc-axis);
  padding-left: 52px;
}
.manual-chart-legend span {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  white-space: nowrap;
}
.manual-chart-legend svg line {
  stroke: currentColor;
  stroke-width: 2;
}
.manual-chart-legend span > span {
  color: var(--mc-axis);
}
.manual-chart figcaption {
  font-size: 0.75rem;
  color: rgb(115 115 115);
  margin-top: 0.35rem;
  padding-left: 52px;
}
.dark .manual-chart figcaption {
  color: rgb(163 163 163);
}
</style>

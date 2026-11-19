<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useAppTheme } from '../composables/useAppTheme'
import { distributionOf } from '../scope/distribution'
import { layOutTrend, trendTickLabel } from '../scope/trend'

/**
 * One chosen measurement's readings over the records they came from — as a trend against record
 * number, or as a distribution of their values.
 *
 * <p><b>Hand-drawn rather than uPlot</b>, and for the plainest of this project's reasons: neither
 * picture wants any of what a plot instance brings. There is no zoom (the x axis is a record
 * ordinal), no cursor to sync with the chart above, no min/max band, no second series and no
 * rebuild key — and the distribution is bars against a value axis, which is a different plot from
 * the trend sharing one component. Two more uPlot instances for that is cost without a return; the
 * three plane charts on this screen are hand-drawn for the same kind of reason.</p>
 *
 * <p><b>A fixed height, and the card is always rendered by its caller</b>, because the chart above
 * is sized by <code>useResultChartHeight</code> from its own card's furniture — a sibling card
 * appearing and disappearing changes the length of the column rather than the height of the chart,
 * which is the arrangement that keeps the trace from jumping.</p>
 *
 * <p>The arithmetic is in <code>scope/trend.ts</code> and <code>scope/distribution.ts</code>, which
 * are testable where this is not.</p>
 */

const props = defineProps<{
  /** The readings, oldest first, NaN where a record had none. */
  series: Float64Array
  /** Which record the first reading was, counting from one. */
  firstRecord: number
  view: 'Trend' | 'Distribution'
  /** How a figure of this measurement is written — the row's own formatter, so the units match the table's. */
  format: (value: number) => string
  /** What the y axis (trend) or x axis (distribution) is of. */
  label: string
}>()

const { isDark } = useAppTheme()

const container = ref<HTMLDivElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)

const HEIGHT_PX = 168
const INSETS = { left: 72, right: 14, top: 10, bottom: 30 }

const TRACE_COLOR = ['#2563eb', '#60a5fa'] as const
const BAR_COLOR = ['#2563eb', '#3b82f6'] as const
const MEAN_COLOR = ['#b45309', '#fbbf24'] as const
const BAND_COLOR = ['rgba(180,83,9,0.10)', 'rgba(251,191,36,0.14)'] as const
const AXIS_COLOR = ['#4b5563', '#9ca3af'] as const
const GRID_COLOR = ['#e5e7eb', '#374151'] as const

function color(pair: readonly string[]): string {
  return isDark.value ? pair[1] : pair[0]
}

const distribution = computed(() =>
  props.view === 'Distribution' ? distributionOf(props.series) : null,
)

/**
 * What the caption under the card says.
 *
 * <p>Three things it has to be able to say, each of which a plot alone would misrepresent: which
 * records these are (the series is bounded, so a long run's plot is of its newest thousand), how
 * many of them had no reading, and — the one most easily mistaken for a broken chart — that the
 * measurement did not move at all.</p>
 */
const caption = computed(() => {
  const count = props.series.length
  if (count === 0) return 'Nothing read yet.'

  const span =
    count === 1
      ? `record ${props.firstRecord}`
      : `records ${props.firstRecord}–${props.firstRecord + count - 1}`

  if (props.view === 'Distribution') {
    const d = distribution.value
    if (!d) return `No reading on any of ${span}.`
    if (d.flat) return `Every one of ${span} read the same figure, ${props.format(d.min)}.`
    return `${d.total.toLocaleString()} readings over ${span}, in ${d.bins.length} bins.`
  }

  let gaps = 0
  for (let i = 0; i < count; i += 1) if (!Number.isFinite(props.series[i])) gaps += 1
  if (gaps === count) return `No reading on any of ${span}.`
  const missing = gaps === 0 ? '' : ` ${gaps.toLocaleString()} had no reading and are drawn as gaps.`
  return `${span}, with the mean and ±1σ of what is drawn.${missing}`
})

function plotBox(width: number) {
  return {
    left: INSETS.left,
    top: INSETS.top,
    width: Math.max(1, Math.floor(width - INSETS.left - INSETS.right)),
    height: Math.max(1, Math.floor(HEIGHT_PX - INSETS.top - INSETS.bottom)),
  }
}

function draw() {
  const c = canvas.value
  const host = container.value
  if (!c || !host) return

  const width = host.getBoundingClientRect().width || 600
  const dpr = window.devicePixelRatio || 1

  c.width = Math.round(width * dpr)
  c.height = Math.round(HEIGHT_PX * dpr)
  c.style.width = `${width}px`
  c.style.height = `${HEIGHT_PX}px`

  const ctx = c.getContext('2d')
  if (!ctx) return
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, width, HEIGHT_PX)

  const box = plotBox(width)
  const ink = color(AXIS_COLOR)
  const grid = color(GRID_COLOR)

  ctx.save()
  ctx.font = '11px system-ui, sans-serif'

  if (props.view === 'Trend') drawTrend(ctx, box, ink, grid)
  else drawDistribution(ctx, box, ink, grid)

  // The frame last, so nothing drawn inside it paints over its edge.
  ctx.strokeStyle = ink
  ctx.lineWidth = 1
  ctx.strokeRect(box.left + 0.5, box.top + 0.5, box.width - 1, box.height - 1)
  ctx.restore()
}

function drawTrend(
  ctx: CanvasRenderingContext2D,
  box: { left: number; top: number; width: number; height: number },
  ink: string,
  grid: string,
) {
  const layout = layOutTrend(props.series, props.firstRecord, box)

  ctx.strokeStyle = grid
  ctx.lineWidth = 1
  ctx.beginPath()
  for (const value of layout.axis.ticks) {
    const y = Math.round(box.top + box.height * (1 - (value - layout.axis.min) / (layout.axis.max - layout.axis.min)))
    ctx.moveTo(box.left, y + 0.5)
    ctx.lineTo(box.left + box.width, y + 0.5)
  }
  ctx.stroke()

  if (layout.segments.length === 0) {
    drawEmptyNote(ctx, box, ink)
    labelTrendAxes(ctx, box, layout, ink)
    return
  }

  // The ±1σ band first, under the trace: it is context for the line rather than a reading of its
  // own, and drawn over it a translucent band lightens exactly the pixels being read.
  if (layout.band) {
    ctx.fillStyle = color(BAND_COLOR)
    const top = Math.min(layout.band.top, layout.band.bottom)
    const height = Math.abs(layout.band.bottom - layout.band.top)
    ctx.fillRect(box.left, top, box.width, Math.max(1, height))
  }

  if (layout.meanY !== null) {
    ctx.save()
    ctx.strokeStyle = color(MEAN_COLOR)
    ctx.setLineDash([4, 3])
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.moveTo(box.left, Math.round(layout.meanY) + 0.5)
    ctx.lineTo(box.left + box.width, Math.round(layout.meanY) + 0.5)
    ctx.stroke()
    ctx.restore()
  }

  ctx.save()
  ctx.beginPath()
  ctx.rect(box.left, box.top, box.width, box.height)
  ctx.clip()
  ctx.strokeStyle = color(TRACE_COLOR)
  ctx.lineWidth = 1.5
  ctx.lineJoin = 'round'
  for (const segment of layout.segments) {
    ctx.beginPath()
    segment.points.forEach((point, index) => {
      if (index === 0) ctx.moveTo(point.x, point.y)
      else ctx.lineTo(point.x, point.y)
    })
    // A single-reading segment strokes nothing, so it is drawn as a dot instead — a gap either
    // side of one record is still a reading, and a picture that omitted it would be wrong.
    if (segment.points.length === 1) {
      ctx.arc(segment.points[0].x, segment.points[0].y, 1.6, 0, Math.PI * 2)
      ctx.fillStyle = color(TRACE_COLOR)
      ctx.fill()
    } else {
      ctx.stroke()
    }
  }
  ctx.restore()

  labelTrendAxes(ctx, box, layout, ink)
}

function labelTrendAxes(
  ctx: CanvasRenderingContext2D,
  box: { left: number; top: number; width: number; height: number },
  layout: ReturnType<typeof layOutTrend>,
  ink: string,
) {
  ctx.fillStyle = ink
  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'
  // Against the middle of the axis rather than each in full — see trendTickLabel for why three
  // labels reading the same four significant figures is the one way this plot misleads.
  const centre = (layout.axis.min + layout.axis.max) / 2
  for (const value of layout.axis.ticks) {
    const y = box.top + box.height * (1 - (value - layout.axis.min) / (layout.axis.max - layout.axis.min))
    ctx.fillText(trendTickLabel(value, centre, props.format), box.left - 6, y)
  }

  ctx.textAlign = 'left'
  ctx.textBaseline = 'top'
  if (layout.lastRecord > 0) {
    ctx.fillText(`${layout.firstRecord}`, box.left, box.top + box.height + 6)
    ctx.textAlign = 'right'
    ctx.fillText(`${layout.lastRecord}`, box.left + box.width, box.top + box.height + 6)
  }
  ctx.textAlign = 'center'
  ctx.fillText('Record', box.left + box.width / 2, box.top + box.height + 6)

  axisTitle(ctx, box, props.label, ink)
}

function drawDistribution(
  ctx: CanvasRenderingContext2D,
  box: { left: number; top: number; width: number; height: number },
  ink: string,
  grid: string,
) {
  const d = distribution.value
  if (!d) {
    drawEmptyNote(ctx, box, ink)
    axisTitle(ctx, box, 'Records', ink)
    return
  }

  ctx.strokeStyle = grid
  ctx.lineWidth = 1
  ctx.beginPath()
  for (let i = 1; i < 4; i += 1) {
    const y = Math.round(box.top + (box.height * i) / 4)
    ctx.moveTo(box.left, y + 0.5)
    ctx.lineTo(box.left + box.width, y + 0.5)
  }
  ctx.stroke()

  const span = d.flat ? 1 : d.max - d.min
  const toX = (value: number) => (d.flat ? box.left + box.width / 2 : box.left + (box.width * (value - d.min)) / span)

  ctx.fillStyle = color(BAR_COLOR)
  const barWidth = d.flat ? Math.min(28, box.width / 3) : box.width / d.bins.length
  for (const bin of d.bins) {
    if (bin.count === 0) continue
    const height = Math.max(1, (box.height - 2) * (bin.count / d.tallest))
    const x = d.flat ? box.left + box.width / 2 - barWidth / 2 : toX(bin.from)
    // A pixel of air between bars, which is what makes two clusters read as two rather than as one
    // wide block — and never below one pixel of bar.
    ctx.fillRect(x, box.top + box.height - height, Math.max(1, barWidth - 1), height)
  }

  if (!d.flat) {
    ctx.save()
    ctx.strokeStyle = color(MEAN_COLOR)
    ctx.setLineDash([4, 3])
    ctx.lineWidth = 1
    ctx.beginPath()
    const x = Math.round(toX(d.mean)) + 0.5
    ctx.moveTo(x, box.top)
    ctx.lineTo(x, box.top + box.height)
    ctx.stroke()
    ctx.restore()
  }

  ctx.fillStyle = ink
  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'
  for (let i = 0; i <= 2; i += 1) {
    const count = (d.tallest * (2 - i)) / 2
    ctx.fillText(
      Number.isInteger(count) ? count.toLocaleString() : count.toFixed(1),
      box.left - 6,
      box.top + (box.height * i) / 2,
    )
  }

  ctx.textBaseline = 'top'
  if (d.flat) {
    ctx.textAlign = 'center'
    ctx.fillText(props.format(d.min), box.left + box.width / 2, box.top + box.height + 6)
  } else {
    // The ends against the middle of the range, for the trend axis's reason: a distribution is
    // fitted to the spread, and three labels at four significant figures all read the same.
    const centre = (d.min + d.max) / 2
    ctx.textAlign = 'left'
    ctx.fillText(trendTickLabel(d.min, centre, props.format), box.left, box.top + box.height + 6)
    ctx.textAlign = 'right'
    ctx.fillText(trendTickLabel(d.max, centre, props.format), box.left + box.width, box.top + box.height + 6)
    ctx.textAlign = 'center'
    ctx.fillText(props.format(centre), box.left + box.width / 2, box.top + box.height + 6)
  }

  axisTitle(ctx, box, 'Records', ink)
}

function drawEmptyNote(
  ctx: CanvasRenderingContext2D,
  box: { left: number; top: number; width: number; height: number },
  ink: string,
) {
  ctx.fillStyle = ink
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText('No reading on any record yet', box.left + box.width / 2, box.top + box.height / 2)
}

function axisTitle(
  ctx: CanvasRenderingContext2D,
  box: { left: number; top: number; width: number; height: number },
  text: string,
  ink: string,
) {
  ctx.save()
  ctx.fillStyle = ink
  ctx.translate(13, box.top + box.height / 2)
  ctx.rotate(-Math.PI / 2)
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(text, 0, 0)
  ctx.restore()
}

let observer: ResizeObserver | null = null

onMounted(() => {
  draw()
  observer = new ResizeObserver(() => draw())
  if (container.value) observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

watch(
  () => [props.series, props.view, props.label, isDark.value] as const,
  () => draw(),
)
</script>

<template>
  <div class="flex flex-col gap-1">
    <div ref="container" class="w-full">
      <canvas ref="canvas" class="plot-surface block w-full rounded" />
    </div>
    <p class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">{{ caption }}</p>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import { useResultChartHeight } from '../charts/useResultChartHeight'
import { CHART_EXPORT_HEADER_PX, composeChartPng, type ChartExportOptions } from '../charts/exportChart'
import ExpandToggle from './ExpandToggle.vue'
import { useAppTheme } from '../composables/useAppTheme'
import type { DecayLayout, DecayPick, OrientedSurface, WallRib } from '../measurements/decay'
import { exportGridTheme } from '../charts/palette'
import {
  acrossX,
  advancesTrailingEdge,
  axisFraction,
  decayLayout,
  drawnSliceIndices,
  formatHz,
  furthestReached,
  levelAt,
  levelIndex,
  levelTicks,
  levelY,
  linearTicks,
  logTicks,
  lutColor,
  orientSurface,
  paletteLut,
  pickSurfacePoint,
  sliceOrigin,
} from '../measurements/decay'

/**
 * A time-frequency decay surface — the cumulative spectral decay and the burst decay share this
 * chart, because they differ in what the depth axis counts and in nothing about how it is drawn.
 *
 * <p><b>Canvas rather than uPlot.</b> uPlot draws series against one pair of axes; this is a stack
 * of several dozen curves each with its own origin, drawn back to front so that a nearer one hides
 * what is behind it. There is no uPlot arrangement that produces that, and the hidden-line removal
 * is the thing that makes a waterfall readable rather than a thicket.</p>
 *
 * <p><b>The area under every curve is filled with the palette's floor colour, not with the page.</b>
 * That single choice is what makes the stack read as one solid body rather than as several dozen
 * lines lying on top of one another. The fill was already there, doing the hidden-line removal;
 * painting it in the page colour hid the slices behind exactly as well and threw the solidity away
 * for nothing. On a <em>light</em> page the body is the page instead: the palette's floor is a
 * near-black navy, and a block of it on white reads as a hole punched in the UI rather than as a
 * solid lit from within. The occlusion is unaffected — it only needs the fill to be opaque, not to
 * be dark.</p>
 *
 * <p><b>The projection is oblique, not perspective.</b> Depth runs down and to the left at a fixed
 * rate, so two slices the same distance apart are the same distance apart wherever they sit, and
 * the depth axis can be labelled linearly. A perspective projection would look more like a
 * photograph and could not be read off.</p>
 *
 * <p><b>Two waterfalls, one drawing.</b> Which of frequency and time runs across the curves is a
 * presentation choice — see <c>orientSurface</c>, which is what every part of this file reads the
 * surface through, the hit test included. Nothing here knows which view it is drawing beyond the
 * axis labels.</p>
 *
 * <p>The arithmetic lives in <c>measurements/decay.ts</c> so it can be tested — nothing in a
 * <c>.vue</c> file is testable under the node environment vitest runs in, and the projection is
 * exactly the kind of thing that fails by drawing a convincing picture of the wrong data.</p>
 */
const props = defineProps<{
  surface: DecaySurface | null
  mode: DecayChartMode
  palette: DecayPalette
  /** Colour each curve by level. Off draws them all in one colour, which reads the shape rather than the level. */
  coloredCurves: boolean
  /** How far below 0 dB the chart reaches. 0 dB means whatever this surface normalised to. */
  rangeDb: number
  thickLines: boolean
  computing: boolean
  error: string | null
  /** What to say when there is nothing to draw. */
  emptyMessage: string
}>()

const { isDark } = useAppTheme()

const container = ref<HTMLDivElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)
// Fitted to the view — see charts/useResultChartHeight.ts. This chart has no furniture of its own:
// the container is the root and the canvas fills it, so the fit is the view's height outright.
const chartHeight = useResultChartHeight(container, canvas)
watch(chartHeight, () => draw())
/**
 * The marker under the cursor, on its own canvas over the chart. A mousemove must not redraw the
 * waterfall: a 400-column surface times eighty curves is tens of thousands of segments, and paying
 * that per pointer event is a chart that lags the cursor across the screen.
 */
const overlay = ref<HTMLCanvasElement | null>(null)
const hover = ref<string | null>(null)

interface ChartTheme {
  background: string
  axis: string
  grid: string
  /** Device pixels a grid line is drawn at — wider on an export, which is shown scaled down. */
  gridWidth: number
  plain: string
  text: string
}

// `background` is the plot well, and it has to be a constant here rather than read from
// `--surface-plot`: an export forces the light theme whatever the app is showing, so the two
// grounds must both be available at once. Keep the dark one in step with that token (style.css).
const THEME: { dark: ChartTheme; light: ChartTheme } = {
  dark: { background: '#15181d', axis: '#a3a3a3', grid: '#333333', gridWidth: 1, plain: '#60a5fa', text: '#d4d4d4' },
  light: { background: '#ffffff', axis: '#525252', grid: '#d4d4d4', gridWidth: 1, plain: '#1d6fd0', text: '#404040' },
}

// Which theme the drawing code reads — the screen's, or light for the duration of an export. Safe as
// module state only because the drawing is synchronous; see ResponsePolarChart's `paint`.
let paintDark = isDark.value
let paintExport = false

function theme(): ChartTheme {
  const colors = paintDark ? THEME.dark : THEME.light
  return paintExport ? exportGridTheme(colors) : colors
}

/**
 * Room for the level axis with its colour key and title, the depth labels with their title down the
 * right edge, and the axis under the front slice with its title beneath it.
 */
const MARGIN = { left: 70, right: 92, top: 22, bottom: 44 }

/** The axis titles' font — a bolder cut of the tick font, the way uPlot titles its axes on the other charts. */
const AXIS_TITLE_FONT = '600 11px system-ui, sans-serif'

/** How much of the box the depth axis takes. Half, which is what makes a decay legible as depth rather than as a smear. */
const DEPTH_FRACTION_Y = 0.5
/** Negative: the stack recedes to the left, as ARTA's does. See `decayLayout`. */
const DEPTH_FRACTION_X = -0.12

const isWaterfall = computed(() => props.mode !== 'Sonogram')

/** Waterfall (p): time across the curves, so the stack recedes in frequency. */
const depthIsFrequency = computed(() => props.mode === 'WaterfallPeriods')

/** What the surface's own slice axis counts. Periods for a burst decay, milliseconds for a CSD. */
const timeUnit = computed(() => (props.surface?.sliceAxis === 'Periods' ? 'periods' : 'ms'))

const oriented = computed<OrientedSurface | null>(() =>
  props.surface ? orientSurface(props.surface, depthIsFrequency.value && isWaterfall.value) : null,
)

/*
 * The colour key is drawn on the canvas rather than laid over it, because in the waterfall it is not
 * a key beside a picture — it is the level axis of the box, and it has to sit against the front
 * face's left edge at exactly the height 0 dB is drawn at. An HTML strip positioned over the canvas
 * agreed with that only by coincidence, and stopped agreeing the moment a margin changed. See
 * `drawLevelAxis`.
 */

function draw() {
  const element = canvas.value
  const box = container.value
  if (!element || !box) return

  const cssWidth = Math.max(200, box.getBoundingClientRect().width)
  const cssHeight = chartHeight.value
  const dpr = window.devicePixelRatio || 1

  // Only the backing store is sized here. The CSS width is the `w-full` class's, deliberately: this
  // is drawn from a ResizeObserver on the container, and an element that writes its own width back
  // into the box being observed is how a resize loop starts.
  if (element.width !== Math.round(cssWidth * dpr) || element.height !== Math.round(cssHeight * dpr)) {
    element.width = Math.round(cssWidth * dpr)
    element.height = Math.round(cssHeight * dpr)
  }
  element.style.height = `${cssHeight}px`

  const ctx = element.getContext('2d')
  if (!ctx) return

  // Whatever was under the cursor belongs to the picture that was there before this one.
  hover.value = null
  drawMarker(null)

  renderScene(ctx, cssWidth, cssHeight, dpr, isDark.value)
}

/**
 * The whole picture onto any context: the page, the surface in whichever mode, the box and the
 * three axes. `dpr` is the backing-store scale of `ctx`'s canvas — the sonogram is blitted in
 * backing-store pixels (putImageData ignores the transform), so it has to know.
 */
function renderScene(ctx: CanvasRenderingContext2D, cssWidth: number, cssHeight: number, dpr: number, dark: boolean, forExport = false) {
  paintDark = dark
  paintExport = forExport
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  const colors = theme()
  ctx.fillStyle = colors.background
  ctx.fillRect(0, 0, cssWidth, cssHeight)

  const surface = props.surface
  const view = oriented.value
  if (!surface || !view || view.depth.length === 0 || view.across.length === 0) return

  const plotWidth = cssWidth - MARGIN.left - MARGIN.right
  const plotHeight = cssHeight - MARGIN.top - MARGIN.bottom
  if (plotWidth <= 20 || plotHeight <= 20) return

  const layout = decayLayout(plotWidth, plotHeight, DEPTH_FRACTION_X, DEPTH_FRACTION_Y)
  const topDb = 0
  const bottomDb = -props.rangeDb

  if (!isWaterfall.value) {
    // Not inside a translate, and not in CSS pixels: putImageData works in backing-store pixels and
    // ignores the canvas transform entirely, so an image built at CSS size would land quarter-sized
    // in the corner of the plot on any 2x display.
    drawSonogram(ctx, surface, plotWidth * dpr, plotHeight * dpr, MARGIN.left * dpr, MARGIN.top * dpr, topDb, bottomDb)
  } else {
    // The frame is drawn in two halves, around the data, and that ordering is what makes it read
    // as a box rather than as lines lying on top of a picture. The back wall goes *under* the
    // curves, so their fills occlude it exactly as a wall behind them would be. The floor goes
    // *over* them, and is safe there: every floor line is the locus of the curves' own baselines
    // at that point of the axis, and no curve is ever drawn below its baseline.
    drawBackWall(ctx, view, layout, colors)

    ctx.save()
    ctx.translate(MARGIN.left, MARGIN.top)
    drawWaterfall(ctx, view, layout, topDb, bottomDb, plotHeight, colors)
    ctx.restore()

    drawFloor(ctx, view, layout, colors)
    drawBoxFrame(ctx, layout, colors)
  }

  drawAcrossAxis(ctx, view, layout, cssHeight, plotWidth, colors)
  drawDepthAxis(ctx, view, layout, plotWidth, plotHeight, colors)
  drawLevelAxis(ctx, layout, plotHeight, colors)
}

/**
 * The chart as a PNG of exactly the requested size, light whatever the theme — see
 * ResponsePolarChart.exportPng, which explains the arrangement for a canvas chart. This one paints its
 * own page, so the export's canvas is drawn at a backing-store scale of 1.
 */
async function exportPng({ width, height }: ChartExportOptions): Promise<Blob> {
  const header = CHART_EXPORT_HEADER_PX
  const plotHeight = height - header
  const plot = document.createElement('canvas')
  plot.width = width
  plot.height = plotHeight
  const ctx = plot.getContext('2d')
  if (!ctx) throw new Error('Could not get a 2D context for the chart export.')

  try {
    renderScene(ctx, width, plotHeight, 1, false, true)
  } finally {
    paintDark = isDark.value
    paintExport = false
  }

  const kind = props.surface?.sliceAxis === 'Periods' ? 'Burst decay' : 'Cumulative spectral decay'
  const how = props.mode === 'Sonogram' ? 'sonogram' : props.mode === 'WaterfallPeriods' ? 'waterfall, by period' : 'waterfall'
  return composeChartPng({
    width,
    height,
    plotCanvas: plot,
    headerHeight: header,
    title: `${kind} — ${how}, ${props.rangeDb} dB range`,
  })
}

defineExpose({ exportPng })

/**
 * Stacked curves, back to front. Each one is filled down to the foot of the box before it is
 * stroked, which is the whole of the hidden-line removal: a nearer curve is drawn lower, so its
 * fill covers whatever of the curves behind falls beneath it.
 */
function drawWaterfall(
  ctx: CanvasRenderingContext2D,
  view: OrientedSurface,
  layout: DecayLayout,
  topDb: number,
  bottomDb: number,
  plotHeight: number,
  colors: ChartTheme,
) {
  const lut = paletteLut(props.palette, paintDark)
  const body = bodyColor(lut, colors)
  const count = view.depth.length
  const width = props.thickLines ? 1.6 : 1.0

  /**
   * The trailing wall, collected rib by rib and filled once at the end. It has to be filled after
   * every curve because it stands in front of all of them: at the strip between two neighbouring
   * trailing ends, the wall is nearer the viewer than any surface projecting there. See
   * `advancesTrailingEdge`.
   */
  const recedesLeft = layout.depthX < 0
  const wall: WallRib[] = []
  let reachedX: number | null = null

  for (const index of drawnSliceIndices(count, layout.depthY)) {
    const depth = count > 1 ? index / (count - 1) : 0
    const origin = sliceOrigin(layout, depth)
    const points: { x: number; y: number; level: number }[] = []

    for (let a = 0; a < view.across.length; a++) {
      const level = view.level(index, a)
      if (level === null) continue
      points.push({
        x: acrossX(layout, origin.x, view.acrossFraction[a]),
        y: levelY(layout, origin.y, level, topDb, bottomDb),
        level,
      })
    }

    if (points.length < 2) continue

    // Fill first: everything under this curve belongs to it, not to the curves behind. The fill
    // runs to the foot of the *box*, not to this curve's own baseline — further than its own floor
    // for a curve at the back, but every nearer one paints over the excess and the front one's
    // baseline is the foot of the box. What survives is one baseline stroke per curve lying on the
    // floor, which is the ruled floor a waterfall is read against.
    bodyPath(ctx, points, plotHeight)
    ctx.fillStyle = body
    ctx.fill()

    const trailing = recedesLeft ? points[points.length - 1] : points[0]
    if (advancesTrailingEdge(trailing.x, reachedX, recedesLeft)) {
      wall.push({ x: trailing.x, y: trailing.y, floorY: origin.y + layout.levelHeight, levelDb: trailing.level })
      reachedX = furthestReached(trailing.x, reachedX, recedesLeft)
    }

    ctx.lineWidth = width
    ctx.lineJoin = 'round'

    if (!props.coloredCurves) {
      ctx.beginPath()
      ctx.moveTo(points[0].x, points[0].y)
      for (const point of points) ctx.lineTo(point.x, point.y)
      ctx.strokeStyle = colors.plain
      ctx.stroke()
      continue
    }

    // Per-level colour, but batched into runs of the same quantised colour rather than one stroke
    // per segment — a 400-column surface times 80 curves is 32 000 segments, and a stroke call each
    // is a visibly slow redraw for a picture nobody can see that much colour resolution in.
    let runStart = 0
    let runShade = shade(points[0].level, topDb, bottomDb)
    for (let i = 1; i <= points.length; i++) {
      const current = i < points.length ? shade(points[i].level, topDb, bottomDb) : -1
      if (current === runShade && i < points.length) continue

      ctx.beginPath()
      ctx.moveTo(points[runStart].x, points[runStart].y)
      for (let j = runStart + 1; j < Math.min(i + 1, points.length); j++) ctx.lineTo(points[j].x, points[j].y)
      const lutIndex = Math.round((runShade / (SHADES - 1)) * 255)
      ctx.strokeStyle = `rgb(${lut[lutIndex * 3]},${lut[lutIndex * 3 + 1]},${lut[lutIndex * 3 + 2]})`
      ctx.stroke()

      runStart = i - 1
      runShade = current
    }
  }

  if (props.coloredCurves) paintTrailingWall(ctx, wall, lut, topDb, bottomDb)
}

/** One curve's body: the curve itself, then straight down to the foot of the box and back. */
function bodyPath(ctx: CanvasRenderingContext2D, points: { x: number; y: number }[], plotHeight: number) {
  ctx.beginPath()
  ctx.moveTo(points[0].x, points[0].y)
  for (const point of points) ctx.lineTo(point.x, point.y)
  ctx.lineTo(points[points.length - 1].x, plotHeight)
  ctx.lineTo(points[0].x, plotHeight)
  ctx.closePath()
}

/**
 * The wall at the far end of the surface, coloured by level — ARTA's rainbow down the right-hand
 * edge, and the thing that makes the chart look like a solid rather than like a stack of ribbons
 * with the page showing between their ends.
 *
 * <p>It is the face of the box at the last value of the across axis: every curve ends on it, and
 * because the stack recedes sideways those ends land at different places, leaving a band one whole
 * depth-run wide. Left in the body colour, that band is the white gaps between curve ends. Filled
 * from the palette, it is a wall.</p>
 *
 * <p>Drawn <em>after</em> every curve, which is the ordering the geometry asks for rather than a
 * convenience: at any point inside the band, the wall is nearer the viewer than whatever surface
 * projects there, so it covers the last pixel or two of the curves behind. One quad per rib, with a
 * gradient along the top edge — perpendicular to that direction a linear gradient is constant, so
 * each rib hangs its own level's colour straight down to its own baseline, which is what puts the
 * palette's bands across the wall horizontally.</p>
 */
function paintTrailingWall(
  ctx: CanvasRenderingContext2D,
  wall: WallRib[],
  lut: Uint8Array,
  topDb: number,
  bottomDb: number,
) {
  for (let i = 1; i < wall.length; i++) {
    const back = wall[i - 1]
    const front = wall[i]

    ctx.beginPath()
    ctx.moveTo(back.x, back.y)
    ctx.lineTo(front.x, front.y)
    ctx.lineTo(front.x, front.floorY)
    ctx.lineTo(back.x, back.floorY)
    ctx.closePath()

    const backColor = lutColor(lut, levelIndex(back.levelDb, topDb, bottomDb))
    const frontColor = lutColor(lut, levelIndex(front.levelDb, topDb, bottomDb))

    // A zero-length gradient paints nothing at all rather than painting its last stop, which would
    // punch a hole in the wall wherever two ribs land on the same pixel.
    if (Math.abs(front.x - back.x) < 0.01 && Math.abs(front.y - back.y) < 0.01) {
      ctx.fillStyle = backColor
    } else {
      const gradient = ctx.createLinearGradient(back.x, back.y, front.x, front.y)
      gradient.addColorStop(0, backColor)
      gradient.addColorStop(1, frontColor)
      ctx.fillStyle = gradient
    }

    // Stroked as well as filled: neighbouring quads share an edge exactly, and antialiasing leaves a
    // hairline of whatever is underneath along every one of them — a wall finely striped with the page.
    ctx.fill()
    ctx.strokeStyle = ctx.fillStyle
    ctx.lineWidth = 1
    ctx.stroke()
  }
}

/**
 * What the area under every curve is filled with — the palette's floor on a dark page, the page
 * itself on a light one. See the note at the top of this file: the fill is what does the hidden-line
 * removal either way, and all that asks of it is that it be opaque.
 */
function bodyColor(lut: Uint8Array, colors: ChartTheme): string {
  return paintDark ? lutColor(lut, 0) : colors.background
}

/** Colour resolution of the stroked curves. Enough that the ramp reads as continuous. */
const SHADES = 48

function shade(levelDb: number, topDb: number, bottomDb: number): number {
  return Math.round((levelIndex(levelDb, topDb, bottomDb) / 255) * (SHADES - 1))
}

/**
 * The same surface as a flat colour map — frequency across, depth down. Worth having beside the
 * waterfall rather than instead of it: the waterfall shows how far something decays, the sonogram
 * shows exactly where in frequency it is, and neither answers the other's question well.
 */
function drawSonogram(
  ctx: CanvasRenderingContext2D,
  surface: DecaySurface,
  // All four in backing-store pixels, not CSS ones — putImageData ignores the canvas transform.
  plotWidth: number,
  plotHeight: number,
  offsetX: number,
  offsetY: number,
  topDb: number,
  bottomDb: number,
) {
  const width = Math.round(plotWidth)
  const height = Math.round(plotHeight)
  const image = ctx.createImageData(width, height)
  const lut = paletteLut(props.palette, paintDark)
  const colors = theme()
  const background = hexToRgb(colors.background)

  const columns = surface.freqHz.length
  const rows = surface.slices.length

  // The surface's own columns are already log-spaced across its band, and so is this axis, so the
  // pixel-to-column map is a straight scale rather than a search.
  for (let px = 0; px < width; px++) {
    const column = Math.min(columns - 1, Math.round((px / Math.max(1, width - 1)) * (columns - 1)))
    for (let py = 0; py < height; py++) {
      const row = Math.min(rows - 1, Math.round((py / Math.max(1, height - 1)) * (rows - 1)))
      const level = levelAt(surface, row, column)
      const offset = (py * width + px) * 4
      image.data[offset + 3] = 255
      if (level === null) {
        image.data[offset] = background[0]
        image.data[offset + 1] = background[1]
        image.data[offset + 2] = background[2]
        continue
      }
      const index = levelIndex(level, topDb, bottomDb)
      image.data[offset] = lut[index * 3]
      image.data[offset + 1] = lut[index * 3 + 1]
      image.data[offset + 2] = lut[index * 3 + 2]
    }
  }

  ctx.putImageData(image, Math.round(offsetX), Math.round(offsetY))
}

function hexToRgb(hex: string): [number, number, number] {
  const value = parseInt(hex.slice(1), 16)
  return [(value >> 16) & 255, (value >> 8) & 255, value & 255]
}

/** Ticks along the across axis, as fractions of it, with the labels they carry. */
function acrossTicks(view: OrientedSurface): { fraction: number; label: string }[] {
  const low = view.across[0]
  const high = view.across[view.across.length - 1]
  const values = view.acrossIsFrequency ? logTicks(low, high) : linearTicks(low, high)
  const decimals = view.acrossIsFrequency ? 0 : high - low < 10 ? 1 : 0

  return values.map((value) => ({
    fraction: axisFraction(value, low, high, view.acrossIsFrequency),
    label: view.acrossIsFrequency ? formatHz(value) : value.toFixed(decimals),
  }))
}

/**
 * The back wall — the face at depth zero, behind every curve. Drawn before the data so the fills
 * cover it from the front curves downward, which is most of what makes the stack read as receding
 * rather than as a pile of lines.
 */
function drawBackWall(
  ctx: CanvasRenderingContext2D,
  view: OrientedSurface,
  layout: DecayLayout,
  colors: ChartTheme,
) {
  const left = Math.round(MARGIN.left + layout.left) + 0.5
  const top = Math.round(MARGIN.top) + 0.5
  const width = Math.round(layout.curveWidth)
  const height = Math.round(layout.levelHeight)

  ctx.strokeStyle = colors.grid
  ctx.lineWidth = colors.gridWidth
  ctx.strokeRect(left, top, width, height)

  // Ruled, so that the empty part of the wall — the part above the surface, which is the part still
  // visible — says how far below 0 dB the ridge under it has fallen. Nothing else on this chart
  // answers that: the key gives a level a hue, and nobody reads a hue to a dB.
  ctx.beginPath()
  for (const db of levelTicks(props.rangeDb)) {
    const y = Math.round(top + (-db / (props.rangeDb || 1)) * height) + 0.5
    ctx.moveTo(left, y)
    ctx.lineTo(left + width, y)
  }
  for (const tick of acrossTicks(view)) {
    const x = Math.round(left + tick.fraction * width) + 0.5
    ctx.moveTo(x, top)
    ctx.lineTo(x, top + height)
  }
  ctx.stroke()
}

/**
 * The floor: one receding line per across-axis tick, from the back wall's foot to the front curve's.
 *
 * <p>Drawn <em>after</em> the curves, which is safe rather than sloppy: at any one point of the
 * axis the line passes through every curve's baseline, and a curve is clamped to its own baseline,
 * so the line can never cross data. What it does cover is the background the fills painted, which
 * is the whole point.</p>
 */
function drawFloor(
  ctx: CanvasRenderingContext2D,
  view: OrientedSurface,
  layout: DecayLayout,
  colors: ChartTheme,
) {
  const back = sliceOrigin(layout, 0)
  const front = sliceOrigin(layout, 1)
  const yBack = MARGIN.top + back.y + layout.levelHeight
  const yFront = MARGIN.top + front.y + layout.levelHeight

  const fractions = [0, ...acrossTicks(view).map((tick) => tick.fraction), 1]

  ctx.lineWidth = colors.gridWidth
  for (const u of fractions) {
    const edge = u <= 0 || u >= 1
    ctx.strokeStyle = edge ? colors.axis : colors.grid
    ctx.beginPath()
    ctx.moveTo(MARGIN.left + acrossX(layout, back.x, u), yBack)
    ctx.lineTo(MARGIN.left + acrossX(layout, front.x, u), yFront)
    ctx.stroke()
  }
}

/**
 * The edges of the box the data does not draw for itself: the one receding from the back wall's top
 * corner, and the front face's own left edge, which is the ruler the level axis hangs on. Drawn
 * last, over everything, so the box stays a box where the front curve reaches the edge and paints
 * across it.
 */
function drawBoxFrame(ctx: CanvasRenderingContext2D, layout: DecayLayout, colors: ChartTheme) {
  const back = sliceOrigin(layout, 0)
  const front = sliceOrigin(layout, 1)

  ctx.strokeStyle = colors.axis
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.moveTo(Math.round(MARGIN.left + back.x) + 0.5, Math.round(MARGIN.top + back.y) + 0.5)
  ctx.lineTo(Math.round(MARGIN.left + front.x) + 0.5, Math.round(MARGIN.top + front.y) + 0.5)
  ctx.moveTo(Math.round(MARGIN.left + front.x) + 0.5, Math.round(MARGIN.top + front.y) + 0.5)
  ctx.lineTo(Math.round(MARGIN.left + front.x) + 0.5, Math.round(MARGIN.top + front.y + layout.levelHeight) + 0.5)
  ctx.stroke()
}

/**
 * The level axis, up the <em>front</em> face's left edge, with the colour key welded to it — the
 * ramp drawn at the height each of its levels is plotted at, so a hue on the surface can be carried
 * across to a number by eye. The front face rather than the back one because the stack recedes
 * leftwards: the back wall's left edge is buried inside the picture, and a ruler there would have
 * curves drawn across it. The sonogram has no level axis to hang the key on, its vertical axis
 * being depth, so there it spans the plot and is labelled as a key rather than as a ruler.
 */
function drawLevelAxis(
  ctx: CanvasRenderingContext2D,
  layout: DecayLayout,
  plotHeight: number,
  colors: ChartTheme,
) {
  const waterfall = isWaterfall.value
  const front = sliceOrigin(layout, 1)
  const edgeX = MARGIN.left + (waterfall ? front.x : 0)
  const top = MARGIN.top + (waterfall ? front.y : 0)
  const height = waterfall ? layout.levelHeight : plotHeight
  const keyRight = edgeX - 6
  const keyWidth = 9

  // Row by row out of the same lookup table the surface is coloured from, rather than a canvas
  // gradient rebuilt from the stops: one source of colour means the key cannot drift from the
  // picture it is a key to.
  const lut = paletteLut(props.palette, paintDark)
  const rows = Math.max(1, Math.round(height))
  for (let row = 0; row < rows; row++) {
    ctx.fillStyle = lutColor(lut, Math.round(((rows - 1 - row) / (rows - 1 || 1)) * 255))
    ctx.fillRect(keyRight - keyWidth, top + row, keyWidth, 1)
  }
  ctx.strokeStyle = colors.grid
  ctx.lineWidth = colors.gridWidth
  ctx.strokeRect(Math.round(keyRight - keyWidth) + 0.5, Math.round(top) + 0.5, keyWidth, Math.round(height))

  ctx.font = '11px system-ui, sans-serif'
  ctx.textAlign = 'right'
  ctx.textBaseline = 'middle'

  for (const db of levelTicks(props.rangeDb)) {
    const y = top + (-db / (props.rangeDb || 1)) * height
    if (waterfall) {
      ctx.strokeStyle = colors.axis
      ctx.beginPath()
      ctx.moveTo(edgeX, y)
      ctx.lineTo(edgeX - 4, y)
      ctx.stroke()
    }
    ctx.fillStyle = colors.text
    ctx.fillText(db.toFixed(0), keyRight - keyWidth - 4, y)
  }

  // The title stands up the canvas's own left edge, centred on the axis it names — which in the
  // waterfall is the front face's half of the height, not the whole of it.
  drawRotatedTitle(ctx, 'Level (dB)', 12, top + height / 2, -Math.PI / 2, colors)
}

/** An axis title along a vertical edge, centred on (x, y) and turned by `angle`. */
function drawRotatedTitle(
  ctx: CanvasRenderingContext2D,
  title: string,
  x: number,
  y: number,
  angle: number,
  colors: ChartTheme,
) {
  ctx.save()
  ctx.translate(x, y)
  ctx.rotate(angle)
  ctx.font = AXIS_TITLE_FONT
  ctx.fillStyle = colors.text
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(title, 0, 0)
  ctx.restore()
}

/**
 * The axis along the curves, under the <em>front</em> one — which in the waterfall is offset by the
 * depth axis's run, because that is where the front curve actually is. Labelling it under the back
 * curve would put every tick a fixed distance from the data it names.
 */
function drawAcrossAxis(
  ctx: CanvasRenderingContext2D,
  view: OrientedSurface,
  layout: DecayLayout,
  cssHeight: number,
  plotWidth: number,
  colors: ChartTheme,
) {
  const front = sliceOrigin(layout, 1)
  const offsetX = MARGIN.left + (isWaterfall.value ? front.x : 0)
  const width = isWaterfall.value ? layout.curveWidth : plotWidth
  const y = cssHeight - MARGIN.bottom

  ctx.strokeStyle = colors.axis
  ctx.fillStyle = colors.text
  ctx.font = '11px system-ui, sans-serif'
  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'
  ctx.lineWidth = 1

  ctx.beginPath()
  ctx.moveTo(offsetX, y + 0.5)
  ctx.lineTo(offsetX + width, y + 0.5)
  ctx.stroke()

  for (const tick of acrossTicks(view)) {
    const x = offsetX + tick.fraction * width
    ctx.beginPath()
    ctx.moveTo(x, y)
    ctx.lineTo(x, y + 4)
    ctx.stroke()
    ctx.fillText(tick.label, x, y + 6)
  }

  // The title carries the unit — which matters for the time axes, since a bare "30.0" does not say
  // whether it is milliseconds or periods, and those are the two measurements this chart draws.
  // Centred under the front curve, which is the span the ticks were laid along.
  ctx.font = AXIS_TITLE_FONT
  ctx.fillText(acrossTitle(view), offsetX + width / 2, y + 22)
}

/** What the across axis counts, with its unit. */
function acrossTitle(view: OrientedSurface): string {
  return view.acrossIsFrequency ? 'Frequency (Hz)' : `Time (${timeUnit.value})`
}

/** What the depth axis counts, with its unit — the other of the two. */
function depthTitle(view: OrientedSurface): string {
  return view.depthIsFrequency ? 'Frequency (Hz)' : `Time (${timeUnit.value})`
}

/**
 * The depth axis, running down the far edge along the direction the curves recede in — so the tick
 * for "3 ms" sits beside the curve that is 3 ms, not beside a point on a vertical ruler that has
 * nothing to do with the projection.
 */
function drawDepthAxis(
  ctx: CanvasRenderingContext2D,
  view: OrientedSurface,
  layout: DecayLayout,
  plotWidth: number,
  plotHeight: number,
  colors: ChartTheme,
) {
  const first = view.depth[0]
  const last = view.depth[view.depth.length - 1]

  ctx.strokeStyle = colors.axis
  ctx.font = '11px system-ui, sans-serif'
  ctx.textAlign = 'left'
  ctx.textBaseline = 'middle'
  ctx.lineWidth = 1

  // A frequency depth axis is logarithmic, so its ticks are placed by value; a time one is linear
  // and evenly spaced ticks land on round-enough numbers of their own.
  const decimals = Math.abs(last - first) < 10 ? 2 : 1
  const ticks: { depth: number; label: string }[] = view.depthIsFrequency
    ? logTicks(first, last).map((hz) => ({ depth: axisFraction(hz, first, last, true), label: formatHz(hz) }))
    : Array.from({ length: 7 }, (_, t) => ({
        depth: t / 6,
        label: (first + (last - first) * (t / 6)).toFixed(decimals),
      }))

  for (const tick of ticks) {
    let x: number
    let y: number
    if (isWaterfall.value) {
      // On the floor's far edge, which is the box's own depth axis. Mid-height would put the labels
      // inside the data, where they sit on top of whatever the DUT is doing.
      const origin = sliceOrigin(layout, tick.depth)
      x = MARGIN.left + origin.x + layout.curveWidth
      y = MARGIN.top + origin.y + layout.levelHeight
    } else {
      x = MARGIN.left + plotWidth
      y = MARGIN.top + tick.depth * plotHeight
    }

    ctx.beginPath()
    ctx.moveTo(x, y)
    ctx.lineTo(x + 4, y)
    ctx.stroke()

    const label = tick.label

    // Haloed, because in the waterfall this axis runs *through* the picture rather than beside it:
    // it is the box's own receding edge, and the curves in front of any given depth are drawn
    // across it. Moving the labels out to the margin instead would break the one thing that makes
    // them readable — that each one sits at the end of the curve it names.
    ctx.lineWidth = 3
    ctx.strokeStyle = colors.background
    ctx.strokeText(label, x + 7, y)
    ctx.lineWidth = 1
    ctx.strokeStyle = colors.axis
    ctx.fillStyle = colors.text
    ctx.fillText(label, x + 7, y)
  }

  // The title runs down the canvas's right edge, reading top to bottom, centred on the axis's own
  // vertical span: the lower half of the box in the waterfall, where the floor's far edge runs,
  // and the whole of it in the sonogram.
  const spanTop = MARGIN.top + (isWaterfall.value ? layout.levelHeight : 0)
  const spanBottom = MARGIN.top + plotHeight
  drawRotatedTitle(ctx, depthTitle(view), MARGIN.left + plotWidth + MARGIN.right - 10, (spanTop + spanBottom) / 2, Math.PI / 2, colors)
}

/**
 * The readout, in every view. In the sonogram the cursor is over a cell of a flat map and the row
 * and column are the axes. In a waterfall it is over a stack of receding parallelograms, and which
 * curve owns the pixel is the painter's algorithm's answer — `pickSurfacePoint` replays it, so the
 * whole solid surface reports rather than only the curves.
 */
function onMove(event: MouseEvent) {
  const surface = props.surface
  const view = oriented.value
  const element = canvas.value
  if (!surface || !view || !element) {
    clearHover()
    return
  }

  const rect = element.getBoundingClientRect()
  const x = event.clientX - rect.left - MARGIN.left
  const y = event.clientY - rect.top - MARGIN.top
  const plotWidth = rect.width - MARGIN.left - MARGIN.right
  const plotHeight = rect.height - MARGIN.top - MARGIN.bottom

  if (x < 0 || y < 0 || x > plotWidth || y > plotHeight || plotWidth <= 20 || plotHeight <= 20) {
    clearHover()
    return
  }

  if (!isWaterfall.value) {
    const column = Math.min(surface.freqHz.length - 1, Math.round((x / plotWidth) * (surface.freqHz.length - 1)))
    const row = Math.min(surface.slices.length - 1, Math.round((y / plotHeight) * (surface.slices.length - 1)))
    const level = levelAt(surface, row, column)

    hover.value =
      level === null
        ? `${formatHz(surface.freqHz[column])} Hz — not measured this deep`
        : describe(surface.freqHz[column], surface.slices[row].value, level)
    drawMarker(level === null ? null : { x, y })
    return
  }

  const layout = decayLayout(plotWidth, plotHeight, DEPTH_FRACTION_X, DEPTH_FRACTION_Y)
  const pick: DecayPick | null = pickSurfacePoint(
    view,
    layout,
    drawnSliceIndices(view.depth.length, layout.depthY),
    x,
    y,
    0,
    -props.rangeDb,
  )

  if (!pick) {
    clearHover()
    return
  }

  // Which of the two indices names a frequency depends on the view, and the readout does not: it
  // always says the frequency, then the time, then the level.
  const freqHz = view.depthIsFrequency ? view.depth[pick.depthIndex] : view.across[pick.acrossIndex]
  const time = view.depthIsFrequency ? view.across[pick.acrossIndex] : view.depth[pick.depthIndex]

  hover.value = describe(freqHz, time, pick.levelDb)
  drawMarker({ x: pick.x, y: pick.y })
}

function describe(freqHz: number, time: number, levelDb: number): string {
  return `${formatHz(freqHz)} Hz, ${time.toFixed(2)} ${timeUnit.value} → ${levelDb.toFixed(1)} dB`
}

function clearHover() {
  hover.value = null
  drawMarker(null)
}

/**
 * The marker: a ring on the point being reported, haloed in the page colour so it is legible over a
 * bright curve and over the body alike. Its own canvas, cleared and redrawn per pointer event —
 * cheap, and it leaves the chart underneath untouched.
 *
 * @param at Where, relative to the plot box, or null to clear.
 */
function drawMarker(at: { x: number; y: number } | null) {
  const element = overlay.value
  const main = canvas.value
  if (!element || !main) return

  if (element.width !== main.width || element.height !== main.height) {
    element.width = main.width
    element.height = main.height
  }
  element.style.height = main.style.height

  const ctx = element.getContext('2d')
  if (!ctx) return

  const dpr = window.devicePixelRatio || 1
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  ctx.clearRect(0, 0, element.width / dpr, element.height / dpr)
  if (!at) return

  const colors = theme()
  const x = MARGIN.left + at.x
  const y = MARGIN.top + at.y

  ctx.beginPath()
  ctx.arc(x, y, 4.5, 0, Math.PI * 2)
  ctx.lineWidth = 3
  ctx.strokeStyle = colors.background
  ctx.stroke()
  ctx.lineWidth = 1.5
  ctx.strokeStyle = colors.text
  ctx.stroke()
}

watch(
  () => [props.surface, props.mode, props.palette, props.coloredCurves, props.rangeDb, props.thickLines, isDark.value],
  () => draw(),
)

/**
 * A ResizeObserver on the container, not a window resize listener. The chart is as wide as whatever
 * it is sitting in, and that changes without the window changing — a panel collapsing, a sidebar
 * opening, the settings rail growing a field. Those left the canvas stretched to a stale backing
 * store until something else happened to redraw it.
 */
let observer: ResizeObserver | null = null

onMounted(() => {
  draw()
  if (container.value && typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(() => draw())
    observer.observe(container.value)
  }
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})
</script>

<template>
  <div ref="container" class="relative w-full">
    <ExpandToggle class="absolute right-2 top-2 z-10" :target="container" />
    <canvas
      ref="canvas"
      class="block w-full"
      @mousemove="onMove"
      @mouseleave="clearHover"
    ></canvas>
    <canvas ref="overlay" class="pointer-events-none absolute left-0 top-0 block w-full"></canvas>

    <!-- A box in the middle of the chart while the surface is being computed, over the previous
         one if there is one: the analysis takes seconds, and a note in a corner was missed. -->
    <p
      v-if="computing"
      class="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 rounded border border-neutral-300 bg-white/95 px-4 py-2 text-center text-xs text-neutral-700 shadow dark:border-neutral-700 dark:bg-neutral-900/95 dark:text-neutral-300"
    >
      Analysing…
    </p>
    <p
      v-else-if="error"
      class="absolute inset-x-16 top-1/2 -translate-y-1/2 rounded border border-red-300 bg-white/95 px-3 py-2 text-center text-xs text-red-700 dark:border-red-800 dark:bg-neutral-900/95 dark:text-red-400"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!surface"
      class="absolute inset-x-0 top-1/2 -translate-y-1/2 text-center text-xs text-neutral-500 dark:text-neutral-400"
    >
      {{ emptyMessage }}
    </p>

    <!-- Top left, which is empty in both views (the waterfall's back wall starts one depth run in,
         the sonogram below the top margin); the bottom now holds the across axis's title. -->
    <p v-if="hover" class="absolute left-2 top-1 font-mono text-[11px] text-neutral-500 dark:text-neutral-400">
      {{ hover }}
    </p>
  </div>
</template>

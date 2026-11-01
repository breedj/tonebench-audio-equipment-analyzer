// eslint-disable-next-line @typescript-eslint/ban-ts-comment
// @ts-ignore -- the package's own declarations export `Graph3d` as `any` (see below).
import { Graph3d } from 'vis-graph3d/standalone'
import { surfaceQuadGradient } from './surfaceGradient'
import {
  NO_SURFACE_MESH,
  surfaceMeshIsEmpty,
  surfaceMeshIsWhole,
  surfaceMeshSegments,
  type SurfaceMesh,
} from './surfaceMesh'

/**
 * <p>The typed face of <c>vis-graph3d</c>, which is what <c>DecaySurface3dChart</c> talks to.</p>
 *
 * <p><b>It exists because the package's own declarations are <c>export const Graph3d: any</c></b> —
 * no constructor, no options, no methods — so without a façade every option name and every method
 * call in that component would be unchecked, and a misspelt option is silently ignored by the
 * library rather than refused. Everything this app sets is named once, here, and <c>vue-tsc</c>
 * then holds the component to it.</p>
 *
 * <p>Imported from <c>vis-graph3d/standalone</c>, not the package root: the root entry is the
 * <em>peer</em> build, which expects <c>vis-data</c>, <c>vis-util</c>, <c>uuid</c>,
 * <c>keycharm</c>, <c>@egjs/hammerjs</c> and <c>component-emitter</c> to be resolvable
 * separately. The standalone build carries all six inside it for 45 kB more, which keeps
 * <c>package.json</c> at one dependency — and so the About dialog's list at one row, which is what
 * <c>libraries.test.ts</c> checks against the manifest.</p>
 */

/** One vertex of the surface. A cell with no measurement is left out of the array entirely. */
export interface Graph3dPoint {
  x: number
  y: number
  z: number
}

/** `{ r, g, b }` in 0…255, which is what a `colormap` function has to return. */
export interface Graph3dRgb {
  r: number
  g: number
  b: number
}

export interface Graph3dCameraPosition {
  /** Radians, right round the circle. */
  horizontal: number
  /** Radians, 0 at the horizon to 0.5π looking straight down. */
  vertical: number
  /** The camera's arm, 0.71 to 5.0. The drawn size goes as its reciprocal. */
  distance: number
}

/**
 * Only the options this app sets, so a typo is a build error. The library has many more; add one
 * here when it is actually used rather than mirroring the whole list, which would only rot.
 */
export interface Graph3dOptions {
  style?: 'surface' | 'grid' | 'dot' | 'bar' | 'line'
  width?: string
  height?: string
  /** Where the box's centre sits in the plot, as percentage strings — this app's two Move sliders. */
  xCenter?: string
  yCenter?: string
  /**
   * False normalises each floor axis to unit length independently, which is what draws the floor
   * square. True makes the longer-ranged axis longer, which is meaningless where the two axes are
   * frequency and time.
   */
  keepAspectRatio?: boolean
  /** How tall the box is drawn against its own floor, 0.1 to 1.0. */
  verticalRatio?: number
  showPerspective?: boolean
  showGrid?: boolean
  showShadow?: boolean
  /**
   * <p><b>Pass false. This app rules its own surface</b> — one direction at a time and in its own
   * ink, through {@link applySurfaceMesh} — where the library's grid is all four edges of every
   * cell or none, in <c>axisColor</c>, the same ink as the tick labels.</p>
   *
   * <p>It is passed rather than left out because <b>the library's own default is
   * <c>true</c></b> and the patch below is allowed not to apply: a release that renames one of the
   * three private members it wraps leaves the library painting on its own, and then this option is
   * the only thing between a reader and a near-black grid over every cell. Left out for one
   * afternoon on 2026-11-01 and reported within the hour — a surface ruled in the axis ink looks
   * deliberate, so nothing about it reads as a fault.</p>
   */
  showSurfaceGrid?: boolean
  showGrayBottom?: boolean
  showXAxis?: boolean
  showYAxis?: boolean
  showZAxis?: boolean
  showLegend?: boolean
  legendLabel?: string
  rotateAxisLabels?: boolean
  zoomable?: boolean
  ctrlToZoom?: boolean
  cameraPosition?: Partial<Graph3dCameraPosition>
  xLabel?: string
  yLabel?: string
  zLabel?: string
  xMin?: number
  xMax?: number
  yMin?: number
  yMax?: number
  zMin?: number
  zMax?: number
  xStep?: number
  yStep?: number
  zStep?: number
  xValueLabel?: (value: number) => string
  yValueLabel?: (value: number) => string
  zValueLabel?: (value: number) => string
  /** Receives the height as a 0…1 fraction of the z range and returns the colour for it. */
  colormap?: (fraction: number) => Graph3dRgb
  axisColor?: string
  gridColor?: string
  axisFontSize?: number
  axisFontType?: string
  backgroundColor?: { fill?: string; stroke?: string; strokeWidth?: number }
  dataColor?: { fill?: string; stroke?: string; strokeWidth?: number }
  tooltip?: boolean | ((point: Graph3dPoint) => string)
  tooltipDelay?: number
  tooltipStyle?: Record<string, Record<string, string>>
}

/**
 * <p><b>Every method here redraws, and a redraw is the expensive thing.</b> Measured on this
 * bench against a cumulative spectral decay of 400 columns by 60 slices — 24 000 vertices — at a
 * 719 x 726 canvas: <c>redraw()</c> <b>292 ms</b>, <c>setCameraPosition()</c> <b>362 ms</b>,
 * <c>setData()</c> <b>392 ms</b> and <c>setOptions()</c> <b>243–390 ms</b>. The library is a 2D
 * canvas painter that depth-sorts every vertex and fills a quad per cell, so most of that is the
 * fill and is irreducible; the grid rebuild on top of it is only about 100 ms.</p>
 *
 * <p><b>So the rule is one call per change, never two.</b> <c>setOptions</c> ends by calling
 * <c>setData</c> and <c>setData</c> ends by redrawing, so a caller that follows either with its
 * own <c>redraw()</c> pays twice — which is how a one-degree nudge of the Rotation slider came to
 * cost two seconds before this was measured.</p>
 */
export interface Graph3dInstance {
  /**
   * Rebuilds the grid and redraws. For a change to the *surface*, never to the view.
   * Never pass an empty array — see {@link createGraph3d}.
   */
  setData(data: Graph3dPoint[]): void
  /** Merges options, rebuilds the grid and redraws — it calls `setData` itself. */
  setOptions(options: Graph3dOptions): void
  setSize(width: string, height: string): void
  /** Synchronous — there is no `requestAnimationFrame` anywhere in the library. */
  redraw(): void
  getCameraPosition(): Graph3dCameraPosition
  /** Moves the camera and redraws, without touching the grid. The view's cheapest path. */
  setCameraPosition(position: Partial<Graph3dCameraPosition>): void
  on(event: 'cameraPositionChange' | 'click', handler: (properties: unknown) => void): void
  /** The library's own DOM. `frame.canvas` is what an export reads with `toDataURL`. */
  frame: HTMLElement & { canvas: HTMLCanvasElement }

  /**
   * <p>Three of the library's own fields, written directly because it offers no setter for them
   * and <c>setOptions</c> — the only supported route — rebuilds the grid to deliver a number the
   * renderer reads on its next pass anyway.</p>
   *
   * <p>They are plain properties of the instance and are read by <c>redraw()</c> (the two
   * centres, through <c>_resizeCenter</c>) or by <c>_setScale()</c> (the height ratio), so a
   * write followed by the right call is exactly what <c>setOptions</c> would have arranged.</p>
   */
  xCenter: string
  yCenter: string
  verticalRatio: number
  colormap?: (fraction: number) => Graph3dRgb
  /**
   * Not the library's: the mesh this chart rules its surface with, read by the patch below. Written
   * through {@link applySurfaceMesh}, which is a plain field write — the renderer reads it on its
   * next pass, so a mesh change costs one redraw and no grid rebuild.
   */
  _surfaceMesh?: SurfaceMesh
  /**
   * Private, and called only through {@link applyVerticalRatio}, which checks for it first.
   * It recomputes the three axis scales and the camera's arm location from the current ranges —
   * which is what reads {@link verticalRatio}, and what `redraw()` on its own does not do.
   */
  _setScale?: () => void
}

/**
 * <p>Applies a new box height, by the cheapest route the library allows and falling back to the
 * supported one if a future version renames what this reaches for.</p>
 *
 * <p><b>It does not redraw, and says whether it had to.</b> The caller is the one that knows what
 * else is pending, and the rule above is one redraw per frame — so the cheap path leaves the
 * picture to the caller and the fallback, which cannot help redrawing, reports that it did.</p>
 *
 * @returns whether the chart has already been redrawn as a side effect.
 */
export function applyVerticalRatio(chart: Graph3dInstance, ratio: number): boolean {
  chart.verticalRatio = ratio
  if (typeof chart._setScale === 'function') {
    chart._setScale()
    return false
  }
  chart.setOptions({ verticalRatio: ratio })
  return true
}

/**
 * <p>Sets which lines the surface is ruled with. A plain field write, like the two centres: the
 * renderer reads it on its next pass, so unlike the <c>showSurfaceGrid</c> option it replaced this
 * costs no grid rebuild. The caller redraws when it is ready — see the rule in
 * {@link Graph3dInstance} about never redrawing twice.</p>
 */
export function applySurfaceMesh(chart: Graph3dInstance, mesh: SurfaceMesh): void {
  chart._surfaceMesh = mesh
}

/**
 * <p>Builds a chart into <c>host</c>.</p>
 *
 * <p><b>There is no counterpart to destroy it.</b> The library has no <c>destroy()</c> — its own
 * <c>create()</c> merely empties the container, and a comment in its source says the event
 * listeners "are never cleaned up". So a caller tears one down by dropping its reference and
 * emptying the host, which is what {@link destroyGraph3d} does.</p>
 *
 * <p><b><c>data</c> must not be empty, and the same goes for {@link Graph3dInstance.setData}.</b>
 * <c>DataGroup.initializeData</c> returns on its own first lines for a zero-row table, leaving
 * every range unset, and the <c>_setScale</c> that follows reads <c>xRange.range()</c> off
 * <c>undefined</c> — so an empty surface throws out of this constructor rather than drawing bare
 * axes. A caller with nothing to draw builds no chart.</p>
 */
export function createGraph3d(
  host: HTMLElement,
  data: Graph3dPoint[],
  options: Graph3dOptions,
  mesh: SurfaceMesh = NO_SURFACE_MESH,
): Graph3dInstance {
  patchSurfacePainting()
  // <p><b>The mesh has to be readable during the constructor, which draws before it returns.</b>
  // So it is parked here for the length of that call and moved onto the instance afterwards; the
  // patched painter reads the instance first and falls back to this. Redraws are synchronous and
  // the library never defers one, so nothing else can be painting while this is set — and the
  // alternative, building the chart and then redrawing it to apply the mesh, is the one thing
  // {@link Graph3dInstance} says never to do.</p>
  meshBeingBuilt = mesh
  try {
    const chart = new Graph3d(host, data, options) as Graph3dInstance
    chart._surfaceMesh = mesh
    return chart
  } finally {
    meshBeingBuilt = undefined
  }
}

/** See {@link createGraph3d}: the mesh of the chart whose constructor is running, if one is. */
let meshBeingBuilt: SurfaceMesh | undefined

/** One corner of a surface cell as the library holds it, which is all the patch below reads. */
interface LibraryQuadCorner {
  screen: { x: number; y: number }
  point: { value: number }
}

/** The private members of the library the patch reaches for, named so a rename is a build error. */
interface LibraryInternals {
  valueRange: { min: number }
  scale: { value: number }
  /** Set by the patched colormap to the brightness the library asked for; see below. */
  _surfaceShade?: number | undefined
  /** Whether the polygon being filled is a surface cell rather than anything else. */
  _inSurfaceCell?: boolean
  /** The mesh this chart rules with; see {@link Graph3dInstance._surfaceMesh}. */
  _surfaceMesh?: SurfaceMesh
}

type ColormapFn = (this: LibraryInternals, x: number, v?: number) => string
type DrawCellFn = (this: LibraryInternals, ctx: CanvasRenderingContext2D, point: unknown) => void
type PolygonFn = (
  this: LibraryInternals,
  ctx: CanvasRenderingContext2D,
  points: LibraryQuadCorner[],
  fillStyle?: string | CanvasGradient,
  strokeStyle?: string | CanvasGradient,
) => void

let surfacePaintingPatched = false

/**
 * <p><b>Two changes to how a surface cell is painted: it is filled with a gradient over its own
 * level axis instead of one flat colour, and it is ruled in one direction rather than all or
 * nothing.</b> The reasoning behind the first is in {@link ./surfaceGradient} and behind the
 * second in {@link ./surfaceMesh}; this is the plumbing for both.</p>
 *
 * <p><b>Why it is a patch.</b> The library offers no vertex colours and no hook into the fill: the
 * colour of a cell is decided inside <c>_redrawSurfaceGraphPoint</c> and handed straight to
 * <c>_polygon</c>, both private. Reimplementing the first of those would mean copying its lighting
 * model — the surface normal, the view angle, the gray-bottom test — into this app, where it would
 * drift from the library's on the next release and say nothing about having done so. Wrapping the
 * three instead leaves every one of those decisions where it is and changes only the paint, so what
 * a version bump can break is the three names below, and all three are checked before anything is
 * replaced: a release that renames one leaves the chart drawing exactly as it did before this.</p>
 *
 * <p><b>The shade is read rather than recomputed</b>, which is the whole reason the colormap is
 * wrapped as well. <c>_redrawSurfaceGraphPoint</c> works out a brightness from the cell's angle to
 * the viewer (the <c>showShadow</c> term) and passes it to <c>_colormap</c> as a second argument
 * that nothing else exposes, so the wrapper notes it in passing and the two end colours are then
 * built the same way the mean colour was. Recovering it from the fill colour's digits instead
 * would be a division by whatever the red channel happened to be.</p>
 *
 * <p>Applied once per process and only from {@link createGraph3d}, so a build that never draws a
 * surface never touches the library's prototype. Exported for its own test, which calls the
 * wrapped painter with a stub canvas — the one half of this chart that can be checked without
 * one.</p>
 */
export function patchSurfacePainting(): void {
  if (surfacePaintingPatched) return
  surfacePaintingPatched = true

  const proto = (Graph3d as unknown as { prototype: Record<string, unknown> }).prototype
  const colormap = proto._colormap as ColormapFn | undefined
  const drawCell = proto._redrawSurfaceGraphPoint as DrawCellFn | undefined
  const polygon = proto._polygon as PolygonFn | undefined
  if (typeof colormap !== 'function' || typeof drawCell !== 'function' || typeof polygon !== 'function') {
    return
  }

  proto._colormap = function (this: LibraryInternals, x: number, v?: number): string {
    this._surfaceShade = v === undefined ? 1 : v
    return colormap.call(this, x, v)
  }

  proto._redrawSurfaceGraphPoint = function (
    this: LibraryInternals,
    ctx: CanvasRenderingContext2D,
    point: unknown,
  ): void {
    this._inSurfaceCell = true
    // Cleared rather than left over: a cell the library decides to paint gray never reaches the
    // colormap at all, and a stale shade would be the previous cell's.
    this._surfaceShade = undefined
    try {
      drawCell.call(this, ctx, point)
    } finally {
      this._inSurfaceCell = false
    }
  }

  proto._polygon = function (
    this: LibraryInternals,
    ctx: CanvasRenderingContext2D,
    points: LibraryQuadCorner[],
    fillStyle?: string | CanvasGradient,
    strokeStyle?: string | CanvasGradient,
  ): void {
    const mesh = this._inSurfaceCell ? activeMesh(this) : undefined

    // <p><b>A surface cell's outline is this app's business and not the library's.</b> Whatever
    // <c>showSurfaceGrid</c> decided is discarded here and the mesh below is the only thing that
    // rules a surface, so the two cannot disagree — and the option, which this app pins to false
    // anyway, is left as the fallback for a patch that did not apply rather than as a second
    // opinion that quietly outranks this one. Closing the hairline seams in the fill colour is
    // what an unruled cell wants, and is what the gradient below then follows.</p>
    if (this._inSurfaceCell) strokeStyle = fillStyle

    const shade = this._surfaceShade
    if (this._inSurfaceCell && shade !== undefined && typeof fillStyle === 'string') {
      const ends = surfaceQuadGradient(points.map((corner) => ({ screen: corner.screen, value: corner.point.value })))
      if (ends) {
        const gradient = ctx.createLinearGradient(ends.from.x, ends.from.y, ends.to.x, ends.to.y)
        gradient.addColorStop(0, colormap.call(this, levelFraction(this, ends.fromValue), shade))
        gradient.addColorStop(1, colormap.call(this, levelFraction(this, ends.toValue), shade))
        // The cell is stroked in its own fill colour to close the hairline seams between cells,
        // so that stroke has to follow the gradient too — unless the mesh is about to claim it.
        if (strokeStyle === fillStyle) strokeStyle = gradient
        fillStyle = gradient
      }
    }

    // <p><b>The mesh, in two cases that look like one.</b> Ruling <em>every</em> edge is exactly
    // the outline the library already strokes, so that case is one assignment and costs nothing
    // over the hairline-closing stroke it replaces — which is what `showSurfaceGrid` did, with the
    // axis ink. Ruling <em>one direction</em> cannot be said that way: the quad is one closed path,
    // so its other two edges still have to be stroked in the fill colour or the seams open, and the
    // chosen two are drawn over them in a pass of their own.</p>
    //
    // <p>Each cell therefore ends by drawing its own mesh edges, after its fill. That ordering is
    // what makes the picture right under the painter's algorithm: a shared edge is drawn by both
    // the cells that own it, so whichever is painted last leaves the line on top of its own
    // fill.</p>
    const whole = mesh !== undefined && surfaceMeshIsWhole(mesh)
    if (mesh !== undefined && whole) strokeStyle = mesh.color

    polygon.call(this, ctx, points, fillStyle, strokeStyle)

    if (mesh === undefined || whole || points.length < 4) return
    ctx.strokeStyle = mesh.color
    ctx.beginPath()
    for (const [from, to] of surfaceMeshSegments(mesh)) {
      ctx.moveTo(points[from].screen.x, points[from].screen.y)
      ctx.lineTo(points[to].screen.x, points[to].screen.y)
    }
    ctx.stroke()
  }
}

/**
 * The mesh the cell being painted is ruled with, or undefined where it is ruled with nothing. The
 * instance first and {@link meshBeingBuilt} second, for the constructor case.
 */
function activeMesh(chart: LibraryInternals): SurfaceMesh | undefined {
  const configured = chart._surfaceMesh ?? meshBeingBuilt
  return configured && !surfaceMeshIsEmpty(configured) ? configured : undefined
}

/** Where a level sits on the colour scale, the way the library's own cell fill works it out. */
function levelFraction(chart: LibraryInternals, value: number): number {
  return (value - chart.valueRange.min) * chart.scale.value
}

/**
 * All the teardown available: empty the host so the library's DOM and the listeners bound to it go
 * with it. Listeners it put on `window`/`document` are bound only for the duration of a drag, so
 * what this cannot reach is bounded.
 */
export function destroyGraph3d(host: HTMLElement | null): void {
  if (host) host.replaceChildren()
}

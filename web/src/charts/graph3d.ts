// eslint-disable-next-line @typescript-eslint/ban-ts-comment
// @ts-ignore -- the package's own declarations export `Graph3d` as `any` (see below).
import { Graph3d } from 'vis-graph3d/standalone'

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
): Graph3dInstance {
  return new Graph3d(host, data, options) as Graph3dInstance
}

/**
 * All the teardown available: empty the host so the library's DOM and the listeners bound to it go
 * with it. Listeners it put on `window`/`document` are bound only for the duration of a drag, so
 * what this cannot reach is bounded.
 */
export function destroyGraph3d(host: HTMLElement | null): void {
  if (host) host.replaceChildren()
}

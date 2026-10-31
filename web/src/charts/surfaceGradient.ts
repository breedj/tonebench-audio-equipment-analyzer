/**
 * <p>Where a 3D surface cell's colour gradient runs, which is the arithmetic half of the patch
 * {@link ../charts/graph3d} applies to <c>vis-graph3d</c>.</p>
 *
 * <p><b>Why there is a patch at all.</b> The library fills each cell of a surface with one flat
 * colour, taken from the mean of its four corners (<c>_redrawSurfaceGraphPoint</c>:
 * <c>vAvg = (point + right + top + cross) / 4</c>). That is right only where a cell is small on
 * screen, and a decay surface is exactly where it is not: wherever the decay is steep, one cell
 * spans a long way down the level axis and projects as a tall face. Measured on a cumulative
 * spectral decay of a loudspeaker, 400 columns by 60 slices: down one screen column the picture
 * held a 42-pixel slab of a single <c>rgb(226, 57, 0)</c> and then jumped, in two pixels, to
 * <c>rgb(232, 135, 2)</c> — while over the flat part of the same surface the colour stepped every
 * three to twelve pixels and read as a gradient. Reported 2026-10-31 as the background of the
 * chart not seeming to have a gradient.</p>
 *
 * <p><b>It is the same fault the flat chart was fixed for on 2026-10-25</b>, arriving by another
 * route: <c>DecaySurfaceChart</c> paints each band with a gradient over that pair's own level
 * axis, so the colour at any point is the colour of the level that height stands for, and this
 * does the same thing one cell at a time. Two charts of one measurement disagreeing about what
 * colour a level is drawn in is the thing worth avoiding.</p>
 *
 * <p>Separate from the patch so it can be tested: vitest runs in the node environment here, where
 * there is no canvas and no library to load, but choosing the two ends is arithmetic.</p>
 */

/** One corner of a cell, as the library hands it over: a screen position and the level there. */
export interface SurfaceQuadCorner {
  screen: { x: number; y: number }
  /** The level, in the data's own units — the library's <c>point.value</c>, which is z here. */
  value: number
}

/** The line a cell's gradient runs along, from its lowest corner to its highest. */
export interface SurfaceQuadGradient {
  from: { x: number; y: number }
  to: { x: number; y: number }
  fromValue: number
  toValue: number
}

/**
 * How far apart the two ends must be on screen before a gradient is worth building.
 *
 * <p>Under this the cell is a sliver and a gradient across it cannot be told from the flat fill it
 * replaces, so the flat fill is kept — which is also what bounds the cost, a gradient being the
 * expensive way to fill a shape and there being tens of thousands of them. Two pixels rather than
 * none because a cell exactly one pixel tall is the commonest cell on a dense surface.</p>
 */
export const MIN_GRADIENT_EXTENT_PX = 2

/**
 * The gradient for one cell, or null where a flat fill says the same thing.
 *
 * <p>The ends are the corners with the lowest and the highest level, and the gradient runs between
 * their screen positions rather than straight up the screen: the level axis is near enough
 * vertical under this library's parallel projection, but taking the corners as they are is both
 * simpler and right whatever the camera does. A bilinear cell's colour is not exactly linear along
 * that line — it is a long way closer than one colour for the whole face.</p>
 */
export function surfaceQuadGradient(
  corners: readonly SurfaceQuadCorner[],
  minExtentPx: number = MIN_GRADIENT_EXTENT_PX,
): SurfaceQuadGradient | null {
  if (corners.length === 0) return null
  let lo = corners[0]
  let hi = corners[0]
  for (const corner of corners) {
    if (!Number.isFinite(corner.value)) return null
    if (corner.value < lo.value) lo = corner
    if (corner.value > hi.value) hi = corner
  }
  // A cell lying flat has one colour already, and asking the canvas for a gradient between two
  // equal stops is pure cost. This is most of a quiet surface.
  if (lo.value === hi.value) return null
  const dx = hi.screen.x - lo.screen.x
  const dy = hi.screen.y - lo.screen.y
  if (dx * dx + dy * dy < minExtentPx * minExtentPx) return null
  return { from: lo.screen, to: hi.screen, fromValue: lo.value, toValue: hi.value }
}

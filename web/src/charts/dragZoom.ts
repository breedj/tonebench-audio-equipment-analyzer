import type { PlaneTransform, Viewport } from './planeTransform'

/**
 * The world-space window a drag-box gesture selected, for the hand-drawn plane charts.
 *
 * Only the arithmetic, not the pointer handling: the handlers need the canvas element, the live
 * transform and the redraw callback, so a composable wrapping them would be as much wiring as the
 * fifty lines it replaced. This part is the part that can be quietly wrong and the part nothing in a
 * `.vue` file can test, which is the same split `planeTransform` already follows.
 */

/**
 * The smallest drag, in pixels, that counts as a zoom rather than a click.
 *
 * Both dimensions must clear it. A stray two-pixel drag while clicking would otherwise zoom to a
 * sliver of the plane, which on an equal-aspect chart is indistinguishable from the plot having
 * broken.
 */
export const MIN_DRAG_PX = 8

export function dragZoomViewport(
  start: { x: number; y: number },
  end: { x: number; y: number },
  transform: PlaneTransform,
  minPx = MIN_DRAG_PX,
): Viewport | null {
  if (Math.abs(end.x - start.x) <= minPx || Math.abs(end.y - start.y) <= minPx) return null

  // Sorted, so a drag up-and-left gives the same window as one down-and-right.
  const xs = [transform.toWorldX(start.x), transform.toWorldX(end.x)].sort((a, b) => a - b)
  const ys = [transform.toWorldY(start.y), transform.toWorldY(end.y)].sort((a, b) => a - b)
  return { xMin: xs[0]!, xMax: xs[1]!, yMin: ys[0]!, yMax: ys[1]! }
}

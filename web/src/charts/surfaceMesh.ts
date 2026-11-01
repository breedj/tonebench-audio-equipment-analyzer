/**
 * <p><b>The lines ruled along a 3D surface's cell edges, and which of them are drawn.</b></p>
 *
 * <p>It was one checkbox until 2026-11-01, when Joost asked for two — <em>"one for the frequency
 * mesh lines and one for the time/periods mesh lines. I like to see them separately"</em> — and the
 * two halves really do say different things. The lines running <b>along the frequency axis</b> are
 * one curve per slice: the spectrum at that moment, which is the shape a waterfall is read for. The
 * lines running <b>along the time axis</b> are one rake per frequency column: how that frequency
 * decays. Ruling both at once is a mesh, and on a cumulative spectral decay's twenty-odd thousand
 * cells it is a wash; ruling one is a picture.</p>
 *
 * <p><b>The quad's corners are the library's own order</b> — <c>[point, right, cross, top]</c>,
 * which <c>_redrawSurfaceGraphPoint</c> assembles from the cell at <c>(x, y)</c>, its neighbour at
 * <c>(x+1, y)</c>, the one at <c>(x+1, y+1)</c> and the one at <c>(x, y+1)</c>. So two of its four
 * edges run along x and two along y, and that is the whole of what this file knows about it.
 * Which <em>quantity</em> each axis carries is the view's business and is answered by
 * <c>decayMeshAxes</c> in <c>measurements/decay3d.ts</c>, because either floor axis can be the
 * frequency one.</p>
 */

/** Which directions are ruled, and in what ink. Both false is no mesh at all. */
export interface SurfaceMesh {
  /** Rule along the library's x axis — the edges at constant y. */
  alongX: boolean
  /** Rule along the library's y axis — the edges at constant x. */
  alongY: boolean
  /** The ink those lines take; see {@link MESH_LINE_COLOR}. */
  color: string
}

/**
 * <p><b>White, in both themes.</b> A mesh line is drawn on saturated palette colour rather than on
 * the page, so it does not follow the page the way an axis label does — which is the rule
 * <c>DecaySurfaceChart</c>'s filled stack has carried since 2026-10-25, and this is the same ink
 * for the same reason.</p>
 *
 * <p><b>It took until 2026-11-01 to reach the 3D chart</b>, where the mesh had been nailed to the
 * axis ink: the library strokes each cell with <c>this.axisColor</c> and its own source marks that
 * line "should be customizable", so the two could not be told apart. A dark ink is right for a tick
 * label on a light page and wrong for a line on Jet's dark blue floor, which is how it was reported
 * — <em>"black is hard to see on the dark blue"</em>. Drawing the lines ourselves is what separates
 * them, so the labels keep their own ink in either theme and on a light export.</p>
 *
 * <p>What it costs is the top of the <c>Hot</c> ramp and the light end of <c>Grey</c>, where white
 * on near-white is faint. That is true of the flat chart's curves too, and one ink that is right
 * nearly everywhere beats a mesh whose colour changes with the palette.</p>
 */
export const MESH_LINE_COLOR = '#ffffff'

/** No mesh, which is what a chart is built with until something asks for one. */
export const NO_SURFACE_MESH: SurfaceMesh = { alongX: false, alongY: false, color: MESH_LINE_COLOR }

/**
 * The corner index pairs to stroke for a four-corner cell, in the library's own corner order.
 *
 * <p>Both directions give all four edges, which is exactly the outline the library strokes for
 * <c>showSurfaceGrid</c> — so that case is left to it (see <c>charts/graph3d.ts</c>) and this
 * answers it only for completeness. Neither direction gives nothing to stroke.</p>
 */
export function surfaceMeshSegments(mesh: SurfaceMesh): readonly (readonly [number, number])[] {
  const segments: (readonly [number, number])[] = []
  // 0 = (x, y), 1 = (x+1, y), 2 = (x+1, y+1), 3 = (x, y+1).
  if (mesh.alongX) segments.push([0, 1], [3, 2])
  if (mesh.alongY) segments.push([0, 3], [1, 2])
  return segments
}

/** Whether anything at all is ruled, which is what decides a second stroking pass. */
export function surfaceMeshIsEmpty(mesh: SurfaceMesh): boolean {
  return !mesh.alongX && !mesh.alongY
}

/** Whether every edge is ruled, which the library can do itself and far more cheaply. */
export function surfaceMeshIsWhole(mesh: SurfaceMesh): boolean {
  return mesh.alongX && mesh.alongY
}

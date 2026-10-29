/**
 * Draws the GPU decay surface's mesh lines wider than the engine will.
 *
 * <p><b>Why this exists.</b> ProEssentials draws <c>ShowWireFrame</c> as a one-pixel line and
 * nothing in its API changes that. Reported three times as the mesh being hard to see, and every
 * documented lever was measured against proessentials 11.0.1 on 2026-10-29 before reaching for
 * this: <c>PePlot.SubsetLineTypes</c> at the subset, wireframe and solid-surface indices,
 * <c>LinesOrTubes</c>, <c>TubeThicknessInc</c>, <c>WireFrameIsoLineBorders</c> and
 * <c>PeConfigure.DxMSAA</c> all render byte for byte the same picture. Only the colour
 * (<c>PeColor.BarBorderColor</c>) and the line count (<c>ShowWireframeMaxLines</c>) respond.</p>
 *
 * <p>So the line is widened after the fact, in the composited image: every pixel next to a mesh
 * pixel becomes one. Measured, that takes a one-pixel line to three — <b>11 582 mesh pixels to
 * 41 753</b> on a 60-slice surface — and closes the gaps the engine leaves in a line that runs
 * nearly along the view, which is half of what makes them hard to follow.</p>
 *
 * <p><b>Black is what identifies a mesh pixel, and that is safe by construction rather than by
 * luck.</b> The engine lights the mesh along with the surface, and lighting multiplies: black
 * stays black and nothing else reaches it. The palette's own darkest entry is (0, 0, 131), the
 * ambient floor (<c>DECAY_LIGHT_STRENGTH</c>) is half, so the darkest the surface can draw is
 * about (0, 0, 65); the dark theme's plot well is (21, 24, 29) and its axis ink (212, 212, 212).
 * Every one of them is clear of {@link MESH_INK_MAX}.</p>
 *
 * <p>It is in its own module because nothing in a <c>.vue</c> file is testable here, and because a
 * dilation that ate the surface instead of the line would do it quietly — see
 * {@link thickenMeshLines}' own bound on how close two lines may be before it declines.</p>
 */

/** A channel at or below this is the mesh; see the module's remarks for why nothing else is. */
export const MESH_INK_MAX = 8

/**
 * How far apart the drawn lines have to be before widening them is worth doing, in pixels.
 *
 * <p>Widening takes a line to three pixels, so on a mesh whose lines are already close together it
 * would fill the surface in rather than draw on it. Measured on the engine at 700 x 420 with the
 * mesh's own twenty lines, the median gap runs <b>16 px at the default magnification and 25, 31,
 * 34 and 39 at 255, 340, 510 and 680 %</b> — so every framing the chart opens on or is magnified
 * to has room, and what this bound is for is the other end: a chart at its minimum height with the
 * magnification wound down, where the lines close up and a wider one would be a black blob. Below
 * it the mesh is left exactly as the engine drew it.</p>
 */
export const MESH_MIN_SPACING_PX = 10

/**
 * Widens every mesh line in place by one pixel in each direction.
 *
 * <p>Whether there is room is read off the mesh itself rather than taken from the caller: its own
 * pixels over the area they are spread across give the spacing directly, since a grid of
 * single-pixel lines <c>s</c> apart covers about <c>2/s</c> of what it is drawn on. Nothing else
 * knows that number — it depends on the magnification, the box's shape and the camera together.</p>
 *
 * @returns How many pixels the mesh covers afterwards — equal to the count before it where the
 * lines were too close to widen, which is what makes that case visible to a caller.
 */
export function thickenMeshLines(image: ImageData): number {
  const { data, width, height } = image
  const mesh = new Uint8Array(width * height)
  let found = 0

  for (let i = 0, p = 0; i < data.length; i += 4, p++) {
    if (data[i] <= MESH_INK_MAX && data[i + 1] <= MESH_INK_MAX && data[i + 2] <= MESH_INK_MAX && data[i + 3] > 200) {
      mesh[p] = 1
      found++
    }
  }

  if (found === 0 || !hasRoom(mesh, width, height)) return found

  // Grown into a second mask rather than in place, or the first widened pixel would seed the next
  // and the whole surface would fill from one line outwards.
  const grown = new Uint8Array(mesh)
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (!mesh[y * width + x]) continue
      for (let dy = -1; dy <= 1; dy++) {
        const yy = y + dy
        if (yy < 0 || yy >= height) continue
        for (let dx = -1; dx <= 1; dx++) {
          const xx = x + dx
          if (xx < 0 || xx >= width) continue
          grown[yy * width + xx] = 1
        }
      }
    }
  }

  let total = 0
  for (let p = 0; p < grown.length; p++) {
    if (!grown[p]) continue
    total++
    if (mesh[p]) continue
    const i = p * 4
    // Only over something the engine drew: a transparent pixel is outside the control's own image,
    // and widening into it would put a black fringe on the page.
    if (data[i + 3] <= 200) continue
    data[i] = 0
    data[i + 1] = 0
    data[i + 2] = 0
  }

  return total
}

/**
 * Whether the mesh's lines are far enough apart to widen, read off the mesh alone.
 *
 * <p>The measure is the median gap between neighbouring mesh pixels along a row — which is the
 * horizontal spacing of the lines running down the picture, and the thing that decides whether a
 * three-pixel line still has colour either side of it. A row carrying one line contributes no gap
 * and so cannot argue against widening, which is right: a lone line has all the room there is.</p>
 */
function hasRoom(mesh: Uint8Array, width: number, height: number): boolean {
  const gaps: number[] = []
  for (let y = 0; y < height; y++) {
    let previous = -1
    for (let x = 0; x < width; x++) {
      if (!mesh[y * width + x]) continue
      // Only between separate lines: pixels touching each other are one line, not two.
      if (previous >= 0 && x - previous > 1) gaps.push(x - previous)
      previous = x
    }
  }

  if (gaps.length === 0) return true
  gaps.sort((a, b) => a - b)
  return gaps[gaps.length >> 1] >= MESH_MIN_SPACING_PX
}

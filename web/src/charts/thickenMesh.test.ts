import { describe, expect, it } from 'vitest'
import { MESH_INK_MAX, MESH_MIN_SPACING_PX, thickenMeshLines } from './thickenMesh'

/** An opaque field of one colour, with no ImageData constructor to depend on. */
function field(width: number, height: number, rgb: [number, number, number]): ImageData {
  const data = new Uint8ClampedArray(width * height * 4)
  for (let i = 0; i < data.length; i += 4) {
    data[i] = rgb[0]
    data[i + 1] = rgb[1]
    data[i + 2] = rgb[2]
    data[i + 3] = 255
  }
  return { data, width, height, colorSpace: 'srgb' } as ImageData
}

function at(image: ImageData, x: number, y: number): [number, number, number, number] {
  const i = (y * image.width + x) * 4
  return [image.data[i], image.data[i + 1], image.data[i + 2], image.data[i + 3]]
}

function paint(image: ImageData, x: number, y: number, rgb: [number, number, number]) {
  const i = (y * image.width + x) * 4
  image.data[i] = rgb[0]
  image.data[i + 1] = rgb[1]
  image.data[i + 2] = rgb[2]
}

const BLACK: [number, number, number] = [0, 0, 0]
/** The darkest the palette can draw: its own foot, at the ambient light floor. */
const DARKEST_SURFACE: [number, number, number] = [0, 0, 65]

describe('thickenMeshLines', () => {
  /**
   * The point of it: ProEssentials draws its wireframe one pixel wide and nothing in its API
   * changes that, so the line is widened in the composited image instead. Measured on the engine,
   * this takes a mesh from 11 582 pixels to 41 753.
   */
  it('takes a one-pixel line to two', () => {
    const image = field(21, 21, DARKEST_SURFACE)
    for (let y = 0; y < 21; y++) paint(image, 10, y, BLACK)

    expect(thickenMeshLines(image)).toBe(2 * 21)
    expect(at(image, 10, 5)).toEqual([0, 0, 0, 255])
    expect(at(image, 11, 5)).toEqual([0, 0, 0, 255])
    expect(at(image, 9, 5)).toEqual([...DARKEST_SURFACE, 255])
    expect(at(image, 12, 5)).toEqual([...DARKEST_SURFACE, 255])
  })

  /**
   * <b>A speck is left exactly as it was.</b> The engine leaves a great many single pixels where an
   * iso line crosses a facet turned away from the camera, and a widening that did not know the
   * difference turned every one of them into a blob — which is what the mesh was reported as on
   * 2026-10-29, the same day it was widened at all.
   */
  it('widens a line and leaves a speck alone', () => {
    const image = field(21, 21, DARKEST_SURFACE)
    for (let y = 0; y < 21; y++) paint(image, 4, y, BLACK)
    paint(image, 15, 10, BLACK)

    expect(thickenMeshLines(image)).toBe(2 * 21 + 1)
    expect(at(image, 16, 10)).toEqual([...DARKEST_SURFACE, 255])
    expect(at(image, 15, 11)).toEqual([...DARKEST_SURFACE, 255])
    expect(at(image, 5, 10)).toEqual([0, 0, 0, 255])
  })

  /**
   * <b>The bound that keeps it from eating the surface.</b> Widening costs three pixels, so lines
   * closer than that merge into a black field — and a dilation that swallowed the picture would do
   * it quietly. Below the bound nothing is changed at all.
   */
  it('declines where the lines are too close together to widen', () => {
    const image = field(21, 21, DARKEST_SURFACE)
    // Two lines a gap of two apart, which is far below the bound.
    for (let y = 0; y < 21; y++) {
      paint(image, 10, y, BLACK)
      paint(image, 12, y, BLACK)
    }
    expect(MESH_MIN_SPACING_PX).toBeGreaterThan(2)

    expect(thickenMeshLines(image)).toBe(2 * 21)
    expect(at(image, 11, 5)).toEqual([...DARKEST_SURFACE, 255])
  })

  /**
   * Black is what identifies a mesh pixel, and the surface cannot reach it: the engine lights the
   * mesh too, lighting multiplies, and the palette's darkest entry at the ambient floor is still
   * (0, 0, 65). The dark theme's plot well and its axis ink are further away still.
   */
  it('leaves everything the surface and the furniture can draw alone', () => {
    for (const colour of [DARKEST_SURFACE, [21, 24, 29], [212, 212, 212], [255, 255, 255]] as [number, number, number][]) {
      const image = field(9, 9, colour)
      expect(thickenMeshLines(image), String(colour)).toBe(0)
      expect(at(image, 4, 4), String(colour)).toEqual([...colour, 255])
    }

    expect(MESH_INK_MAX).toBeLessThan(DARKEST_SURFACE[2])
  })

  /** Outside the control's own image there is the page, and a black fringe on it would show. */
  it('does not widen into a transparent pixel', () => {
    const image = field(9, 9, DARKEST_SURFACE)
    for (let i = 3; i < image.data.length; i += 4) image.data[i] = 0
    // A line, since a pixel on its own is left alone whatever is beside it.
    for (const y of [3, 4, 5]) {
      const i = (y * 9 + 4) * 4
      image.data[i] = 0
      image.data[i + 1] = 0
      image.data[i + 2] = 0
      image.data[i + 3] = 255
    }

    thickenMeshLines(image)
    expect(at(image, 5, 4)).toEqual([...DARKEST_SURFACE, 0])
  })

  /**
   * Grown into a second mask, not in place: a widened pixel must not seed the next, or one line
   * fills the whole surface.
   */
  it('widens by one pixel and not by one pixel over and over', () => {
    const image = field(31, 3, DARKEST_SURFACE)
    for (let y = 0; y < 3; y++) paint(image, 15, y, BLACK)

    thickenMeshLines(image)
    expect(at(image, 14, 1)).toEqual([...DARKEST_SURFACE, 255])
    expect(at(image, 17, 1)).toEqual([...DARKEST_SURFACE, 255])
  })
})

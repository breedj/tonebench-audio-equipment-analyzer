/**
 * Joins {@link useAnnotationOverlay} to the three hand-drawn plane charts — the impedance locus, the
 * Nyquist/Nichols/pole-zero projections, and the s-plane heat map.
 *
 * <p>They need <b>less</b> than the uPlot charts, not more, and that is worth knowing before
 * reaching for a plugin pipeline they do not have. All three draw in <b>CSS pixels</b>
 * (`ctx.setTransform(dpr,0,0,dpr,0,0)`) through one `renderScene` that `exportPng` calls at 1:1, so:
 * one adapter serves both the painting and the DOM hit-testing, its `scale` is 1 in both, and
 * `annotationFontPx` gives 11 px on screen and ~25 px on the 1600 px export with nothing further to
 * do. {@link planeTransform.PlaneTransform} already exposes exactly the four mapping functions an
 * {@link AnnotationAxis} wants.</p>
 *
 * <p>What they <i>do</i> need is a box gesture of their own: uPlot's drag-selection is what the
 * uPlot charts commandeer, and there is none here. {@link usePlaneBoxGesture} is that — a press,
 * a rubber band drawn on the chart's own overlay canvas, and a release that places the region.</p>
 */

import { ref } from 'vue'
import { annotationFontPx, type AnnotationPlot } from './annotationLayout'
import { annotationFont } from './annotationPainter'
import type { PlaneTransform } from './planeTransform'
import type { AnnotationController } from './useAnnotationOverlay'

/** The plot area inside a plane chart's canvas, which its own INSETS describe. */
export interface PlaneInsets {
  left: number
  right: number
  top: number
  bottom: number
}

/**
 * An adapter over a plane chart's transform.
 *
 * <p>`left`/`top` are the plot area's own origin inside the canvas, not zero, because these charts
 * draw axes and tick labels in the margins and a pointer position is measured against the canvas.
 * That is the one difference from the uPlot adapters, whose `over` element <i>is</i> the plot
 * area.</p>
 */
export function planeAnnotationPlot(
  transform: PlaneTransform,
  size: { width: number; height: number },
  insets: PlaneInsets,
  quantities: { x: string; y: string },
  measure: CanvasRenderingContext2D | null,
): AnnotationPlot {
  const width = Math.max(1, size.width - insets.left - insets.right)
  const height = Math.max(1, size.height - insets.top - insets.bottom)
  const view = transform.viewport
  return {
    left: insets.left,
    top: insets.top,
    width,
    height,
    x: {
      quantity: quantities.x,
      posOf: (value) => transform.toScreenX(value),
      valOf: (pos) => transform.toWorldX(pos),
      window: { lo: Math.min(view.xMin, view.xMax), hi: Math.max(view.xMin, view.xMax) },
    },
    y: {
      quantity: quantities.y,
      posOf: (value) => transform.toScreenY(value),
      valOf: (pos) => transform.toWorldY(pos),
      window: { lo: Math.min(view.yMin, view.yMax), hi: Math.max(view.yMin, view.yMax) },
    },
    scale: 1,
    measureText: (text, fontPx) => {
      if (!measure) return text.length * fontPx * 0.55
      measure.font = annotationFont(fontPx)
      return measure.measureText(text).width
    },
  }
}

/** The key {@link AnnotationController.noteGeometry} compares, so the DOM handles follow a zoom. */
export function planeGeometryKey(size: { width: number; height: number }, transform: PlaneTransform): string {
  const v = transform.viewport
  return `${size.width},${size.height},${v.xMin},${v.xMax},${v.yMin},${v.yMax}`
}

/** The font a plane chart's labels are drawn at, for a chart that wants to reserve room for them. */
export function planeAnnotationFontPx(plotWidth: number): number {
  return annotationFontPx(plotWidth)
}

/**
 * The box gesture for a chart that has no drag-selection of its own.
 *
 * <p>Returns the rectangle to draw as a rubber band while the button is down, and calls
 * `controller.placeBoxFrom` on release. Coordinates are the canvas's own CSS pixels, which is the
 * space {@link planeAnnotationPlot} works in — so the rectangle can be handed straight to the
 * controller and straight to a `strokeRect`.</p>
 *
 * <p>The release is handled on <b>window</b>, like the uPlot charts', so a drag that ends outside
 * the plot still finishes rather than leaving the chart armed with a band painted on it.</p>
 */
export function usePlaneBoxGesture(controller: AnnotationController, redraw: () => void) {
  /** The band to draw, or null. Read by the chart's own overlay paint. */
  const band = ref<{ x: number; y: number; w: number; h: number } | null>(null)
  let origin: { x: number; y: number } | null = null

  function onPointerDown(pos: { x: number; y: number }) {
    if (!controller.armedForBox.value) return
    origin = pos
    band.value = { x: pos.x, y: pos.y, w: 0, h: 0 }
  }

  function onPointerMove(pos: { x: number; y: number }) {
    if (!origin) return
    band.value = {
      x: Math.min(origin.x, pos.x),
      y: Math.min(origin.y, pos.y),
      w: Math.abs(pos.x - origin.x),
      h: Math.abs(pos.y - origin.y),
    }
    redraw()
  }

  /** True when this release was the end of a box gesture, so the caller skips its own click work. */
  function onWindowUp(): boolean {
    if (!origin) return false
    const rect = band.value
    origin = null
    band.value = null
    redraw()
    if (!rect) return false
    controller.placeBoxFrom({ left: rect.x, top: rect.y, width: rect.w, height: rect.h })
    return true
  }

  function dragging(): boolean {
    return origin !== null
  }

  return { band, onPointerDown, onPointerMove, onWindowUp, dragging }
}

/** Paints the rubber band a box gesture is sweeping, in the chart's own axis colour. */
export function paintPlaneBand(
  ctx: CanvasRenderingContext2D,
  band: { x: number; y: number; w: number; h: number } | null,
  stroke: string,
) {
  if (!band || band.w < 1 || band.h < 1) return
  ctx.save()
  ctx.setLineDash([4, 3])
  ctx.strokeStyle = stroke
  ctx.lineWidth = 1
  ctx.strokeRect(band.x, band.y, band.w, band.h)
  ctx.restore()
}

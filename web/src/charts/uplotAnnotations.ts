/**
 * Everything that joins {@link useAnnotationOverlay} to a uPlot chart: the two pixel adapters, the
 * draw-hook plugin, the drag-mode switch and the listeners.
 *
 * <p>A separate module from the composable so the three hand-drawn plane charts never import uPlot
 * to annotate — they build an adapter from their own {@link PlaneTransform} and call
 * `controller.paint` from their own `renderScene`.</p>
 *
 * <p>Three facts about uPlot 1.6.32 the box gesture rests on, all worth knowing before touching it.
 * It keeps <b>one reference</b> to `cursor.drag` for the instance's life, so mutating fields works
 * and replacing the object does not. It re-reads `drag.y` on every mousemove and `drag.setScale` at
 * mouseup, so both can be flipped per gesture. And `mouseUp` fires the `setSelect` hook <i>before</i>
 * it tests `setScale` to zoom — which is the other way this could have been built, and is the way it
 * is not: a flag that is a pure function of the armed state cannot be left half-restored, whereas
 * cancelling from inside the hook zooms to the half-drawn rectangle if Escape lands mid-drag.</p>
 */

import type uPlot from 'uplot'
import { annotationFont } from './annotationPainter'
import type { AnnotationTheme } from './annotationPainter'
import type { AnnotationAxis, AnnotationPlot } from './annotationLayout'
import type { AnnotationController } from './useAnnotationOverlay'

/**
 * How one of a chart's axes relates to the quantity annotations on it are stored in.
 *
 * <p>`toDisplay`/`fromDisplay` exist for the axes that plot something other than the stored figure:
 * the spectrum's dBV against a dBr or density axis, the distortion sweep's percent against a dB
 * axis, the impulse response's volts against dB re peak. Returning a non-finite number from
 * `toDisplay` is how a chart says "not convertible right now" — the spectrum's density unit with no
 * ENBW — and the layout then draws nothing rather than drawing it in the wrong place.</p>
 */
export interface UplotAxisSpec {
  quantity: string
  /** uPlot's scale key. Defaults to `'x'` for x and `'y'` for y. */
  scale?: string
  toDisplay?: (value: number) => number
  fromDisplay?: (display: number) => number
}

export interface UplotAnnotationAxes {
  x: UplotAxisSpec
  y: UplotAxisSpec
}

const IDENTITY = (value: number) => value
const ZERO_SELECT = { left: 0, top: 0, width: 0, height: 0 }

/** This plot's pixels per CSS pixel — uPlot draws in device pixels, the DOM handles live in CSS ones. */
export function plotPixelScale(u: uPlot): number {
  const css = u.over.clientWidth
  return css > 0 ? u.bbox.width / css : 1
}

function measureWith(u: uPlot): (text: string, fontPx: number) => number {
  return (text, fontPx) => {
    u.ctx.font = annotationFont(fontPx)
    return u.ctx.measureText(text).width
  }
}

function axisOf(u: uPlot, spec: UplotAxisSpec, fallbackScale: 'x' | 'y', canvasPixels: boolean): AnnotationAxis {
  const key = spec.scale ?? fallbackScale
  const toDisplay = spec.toDisplay ?? IDENTITY
  const fromDisplay = spec.fromDisplay ?? IDENTITY
  const scale = u.scales[key]
  return {
    quantity: spec.quantity,
    posOf: (value) => {
      const display = toDisplay(value)
      return Number.isFinite(display) ? u.valToPos(display, key, canvasPixels) : Number.NaN
    },
    valOf: (pos) => fromDisplay(u.posToVal(pos, key, canvasPixels)),
    window: {
      lo: fromDisplay(scale?.min ?? Number.NaN),
      hi: fromDisplay(scale?.max ?? Number.NaN),
    },
  }
}

/** The adapter the canvas draws through: device pixels, positions including the plot's own offset. */
export function canvasAnnotationPlot(u: uPlot, axes: UplotAnnotationAxes): AnnotationPlot {
  return {
    left: u.bbox.left,
    top: u.bbox.top,
    width: u.bbox.width,
    height: u.bbox.height,
    x: axisOf(u, axes.x, 'x', true),
    y: axisOf(u, axes.y, 'y', true),
    scale: plotPixelScale(u),
    measureText: measureWith(u),
  }
}

/**
 * The adapter the DOM handles use: CSS pixels relative to the plotting area's own top-left, which is
 * where the overlay div is positioned and also where `u.over`'s client rect begins — so a handle
 * lands exactly on the label the canvas drew and a pointer position needs nothing added to it.
 */
export function cssAnnotationPlot(u: uPlot, axes: UplotAnnotationAxes): AnnotationPlot {
  const scale = plotPixelScale(u)
  return {
    left: 0,
    top: 0,
    width: u.bbox.width / scale,
    height: u.bbox.height / scale,
    x: axisOf(u, axes.x, 'x', false),
    y: axisOf(u, axes.y, 'y', false),
    scale: 1,
    measureText: measureWith(u),
  }
}

/** Where the overlay div goes: over the plotting area, in CSS pixels relative to the chart's container. */
export function annotationOverlayRect(u: uPlot): { left: number; top: number; width: number; height: number } {
  const scale = plotPixelScale(u)
  return {
    left: u.bbox.left / scale,
    top: u.bbox.top / scale,
    width: u.bbox.width / scale,
    height: u.bbox.height / scale,
  }
}

/** The key {@link AnnotationController.noteGeometry} compares, so the handles follow a zoom or a resize. */
export function annotationGeometryKey(u: uPlot, axes: UplotAnnotationAxes): string {
  const xKey = axes.x.scale ?? 'x'
  const yKey = axes.y.scale ?? 'y'
  const x = u.scales[xKey]
  const y = u.scales[yKey]
  return `${u.bbox.left},${u.bbox.top},${u.bbox.width},${u.bbox.height},${x?.min},${x?.max},${y?.min},${y?.max}`
}

/**
 * The plugin that paints the annotations.
 *
 * <p>Registered in <b>`draw`</b>, i.e. over the series — a label under a dense trace cannot be read —
 * and it must be the <b>last</b> plugin in the list, or whatever draws after it paints over the
 * labels (the spectrogram's own plugin blits an image over the whole plotting box). Unlike its
 * sibling plugins it takes no `interactive` flag, and that is deliberate: it registers no hook an
 * export would not want, because everything a mouse touches is a DOM handle above the canvas. So an
 * offscreen export plot gets exactly this plugin, whole, and the labels land in the report's PNG, the
 * Screenshot dialog and the black-and-white print with no further code.</p>
 */
export function annotationsPlugin(
  controller: AnnotationController,
  axes: UplotAnnotationAxes,
  theme: () => AnnotationTheme,
): uPlot.Plugin {
  return {
    hooks: {
      draw: [
        (u) => {
          controller.noteGeometry(annotationGeometryKey(u, axes))
          controller.paint(u.ctx, canvasAnnotationPlot(u, axes), theme())
        },
      ],
    },
  }
}

/**
 * Turns uPlot's own drag-selection into the region gesture while a Box is armed, by mutating the live
 * cursor rather than adding an input layer of our own. See the module remarks for why it is safe and
 * why it is not done from the `setSelect` hook.
 *
 * <p><b>Must be re-applied after every rebuild</b> — a new instance carries uPlot's defaults, so
 * arming and then switching theme would silently turn the gesture back into a zoom.</p>
 */
export function syncBoxDragMode(u: uPlot | null | undefined, armedForBox: boolean) {
  // uPlot always merges its own defaults, so `drag` is there at runtime; the typings say it may not
  // be, and mutating the object it hands back is the whole mechanism -- see above.
  const drag = u?.cursor.drag
  if (!u || !drag) return
  drag.y = armedForBox
  drag.setScale = !armedForBox
  // With setScale off uPlot never calls its own hideSelect(), so the rubber band stays painted until
  // someone clears it.
  if (!armedForBox) u.setSelect(ZERO_SELECT, false)
}

/**
 * Finishes a box gesture, from a <b>bubble-phase `mouseup` on `window`</b>, and neither half of that
 * is incidental. uPlot's own `drag.click` is registered capture-phase on an ancestor and calls
 * `stopImmediatePropagation()` whenever the pointer moved at all, so nothing drag-shaped can be
 * driven off a `click`; and a window listener is the one place that runs after uPlot's own
 * document-level `mouseUp`, so `u.select` is still readable and the restore cannot race the zoom
 * test. It also fires when the button comes up outside the plot, which a listener on `over` would
 * miss.
 */
export function finishBoxGesture(u: uPlot | null | undefined, controller: AnnotationController) {
  if (!controller.inBoxGesture()) return
  controller.endBoxGesture()
  if (!u) return
  if (controller.armedForBox.value) {
    const sel = u.select
    controller.placeBoxFrom({ left: sel.left, top: sel.top, width: sel.width, height: sel.height })
  }
  u.setSelect(ZERO_SELECT, false)
  syncBoxDragMode(u, controller.armedForBox.value)
}

/**
 * The two listeners a click-to-place needs, on the plot's `over` element. Both are no-ops unless a
 * panel has armed the chart, so an unarmed plot behaves exactly as it did before annotations existed.
 */
export function attachAnnotationListeners(u: uPlot, controller: AnnotationController) {
  u.over.addEventListener('mousedown', controller.notePress)
  u.over.addEventListener('click', controller.onClick)
}

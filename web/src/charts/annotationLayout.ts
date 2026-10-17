/**
 * Where an annotation's marker, leader, label box and ordinal badge go on a plot.
 *
 * <p>Deliberately knows nothing about uPlot: it takes an {@link AnnotationPlot} adapter, so the same
 * arithmetic serves the <b>canvas</b> (device pixels, which is what the report's PNG is made of), the
 * <b>DOM overlay</b> (CSS pixels, which is what a mouse can grab) and the four hand-drawn plane
 * charts, which have no uPlot instance at all. Those have to agree to the pixel or a label would be
 * drawn in one place and dragged from another — one function for all of them is how that is
 * guaranteed. It is also the only way to test any of this, nothing in a `.vue` being testable here.</p>
 *
 * <p>The sizes are all expressed in units of the font, and the font is a fixed fraction of the plot's
 * width ({@link annotationFontPx}). That is what makes the 1600 px export label read the same on the
 * page as the ~700 px screen label does on screen: `composeChartPng` draws the plot canvas scaled
 * onto the requested size, so anything that is a fixed fraction of the plot survives the rescale and
 * anything measured in absolute pixels does not.</p>
 *
 * <p>Nothing here knows what a frequency is. An axis declares which <b>quantity</b> it plots and
 * answers positions for it; an annotation stored in some other quantity is skipped, the same way one
 * outside the plotted window is. All the offset and extent arithmetic goes through that quantity's
 * own {@link AnnotationSpace}, so one set of functions serves a log-frequency axis, a linear time
 * axis that crosses zero, and an impedance axis that switches between the two at runtime.</p>
 */

import type { AnnotationStyle } from '../measurements/chartAnnotations'
import { annotationQuantity, type AnnotationSpace } from './annotationQuantities'

/** One axis of a plot annotations can be laid out on. */
export interface AnnotationAxis {
  /**
   * The annotation quantity this axis can draw — a key of annotationQuantities.ts. An annotation
   * stored in anything else is skipped rather than converted: see the module remarks there.
   */
  quantity: string
  /**
   * A stored value in {@link quantity} → a position in this adapter's pixels, including whatever
   * display transform the axis is applying (the spectrum's unit offset, the sweep's %→dB, the
   * impulse's volts→dB re peak). Non-finite where the value has no position at all, which a log axis
   * answers for anything at or below zero.
   */
  posOf(value: number): number
  /** A position in this adapter's pixels → a value in {@link quantity}. */
  valOf(pos: number): number
  /** The window on screen, in {@link quantity}, `lo < hi`. */
  window: { lo: number; hi: number }
}

/** A plot to lay annotations out on, in whatever pixel space the caller works in. */
export interface AnnotationPlot {
  /** The plotting area, in this adapter's own pixels. */
  left: number
  top: number
  width: number
  height: number
  x: AnnotationAxis
  y: AnnotationAxis
  /**
   * This adapter's pixels per CSS pixel — 1 for the DOM overlay and for the hand-drawn charts (which
   * draw in CSS pixels throughout), the device pixel ratio for a uPlot canvas. The font is chosen
   * from the CSS width and then scaled by this, so a hidpi screen does not get half-size labels.
   */
  scale: number
  measureText(text: string, fontPx: number): number
}

/**
 * One annotation to lay out: its <b>stored</b> values and the quantities they are in. The adapter
 * does the converting, so nothing upstream of here has to know what the axis is currently plotting.
 */
export interface AnnotationLayoutItem {
  id: number
  number: number
  style: AnnotationStyle
  text: string
  x: number
  /** A point's level, or a <b>box</b>'s top edge. Null for a rule. */
  y: number | null
  /** A box's other two edges. Null for every other style. */
  x2: number | null
  y2: number | null
  xQuantity: string
  yQuantity: string
  labelOffsetX: number
  labelOffsetY: number
}

/** A rectangle in plot pixels. */
export interface Rect {
  x: number
  y: number
  w: number
  h: number
}

export interface AnnotationPlacement {
  id: number
  number: number
  style: AnnotationStyle
  text: string
  /**
   * False when there is nothing to draw: outside the plotted window, no level, no room to stack, or
   * stored in a quantity this axis is not plotting.
   */
  visible: boolean
  fontPx: number
  /** The marker dot, for a point. */
  anchor: { x: number; y: number; r: number } | null
  /** The vertical rule, for a rule annotation. */
  rule: { x: number; y1: number; y2: number } | null
  /** The marked region, for a box — already clamped to the plotting area. */
  box: Rect | null
  label: { x: number; y: number; w: number; h: number }
  leader: { x1: number; y1: number; x2: number; y2: number } | null
  /** The ordinal's circle, or null when the numbers are off (see `numbers` below). */
  badge: { x: number; y: number; r: number } | null
  /** Where the note itself is drawn — left-aligned, middle baseline. */
  textX: number
  textY: number
}

/** The reference width the font is proportional to, and the font at it. */
const REFERENCE_WIDTH_PX = 700
const REFERENCE_FONT_PX = 11
const MIN_FONT_PX = 10
const MAX_FONT_PX = 26

/**
 * The label font for a plot this wide, in CSS pixels. Proportional, so the label is the same fraction
 * of the picture on screen and in the 1600 px export; bounded, so a chart squeezed into a narrow
 * column still has readable labels and a very wide one does not get a headline.
 */
export function annotationFontPx(plotCssWidthPx: number): number {
  const proportional = (REFERENCE_FONT_PX * plotCssWidthPx) / REFERENCE_WIDTH_PX
  return Math.min(MAX_FONT_PX, Math.max(MIN_FONT_PX, proportional))
}

/** Every size the labels are built from, in plot pixels, derived from one font size. */
export interface AnnotationMetrics {
  fontPx: number
  padX: number
  padY: number
  radius: number
  badgeR: number
  badgeGap: number
  markerR: number
  gap: number
  boxHeight: number
}

export function annotationMetrics(plot: AnnotationPlot): AnnotationMetrics {
  const fontPx = annotationFontPx(plot.width / plot.scale) * plot.scale
  return {
    fontPx,
    padX: fontPx * 0.5,
    padY: fontPx * 0.3,
    radius: fontPx * 0.35,
    badgeR: fontPx * 0.62,
    badgeGap: fontPx * 0.35,
    markerR: fontPx * 0.3,
    gap: fontPx * 0.35,
    boxHeight: fontPx * 2.0,
  }
}

function clamp(value: number, lo: number, hi: number): number {
  return hi < lo ? (lo + hi) / 2 : Math.min(hi, Math.max(lo, value))
}

function spaceOf(quantity: string): AnnotationSpace {
  return annotationQuantity(quantity).space
}

/**
 * Whether this plot is the one that annotation belongs on: the axes have to be plotting the
 * quantities it was stored in.
 *
 * <p>A rule is judged on x alone — it has no level, so the y axis could be plotting anything. That
 * is what lets one rule survive the frequency-response chart's level/gain switch, which a point at a
 * level cannot.</p>
 */
export function drawsAnnotation(item: AnnotationLayoutItem, plot: AnnotationPlot): boolean {
  if (item.xQuantity !== plot.x.quantity) return false
  return item.style === 'frequency' || item.yQuantity === plot.y.quantity
}

/**
 * Where a label starts on a freshly placed annotation, in the stored quantities' own spaces: a fixed
 * fraction of the plot up and to the right (see DEFAULT_LABEL_OFFSET_FRACTION).
 *
 * <p>Derived from the window rather than being a constant, because the offset's units are the
 * quantity's: 0.45 "of x" is 0.45 of an octave on a frequency axis and 0.45 of a millisecond on a
 * time one, which are not the same gesture at all.</p>
 */
export function defaultLabelOffset(
  plot: AnnotationPlot,
  fraction: { x: number; y: number },
): { labelOffsetX: number; labelOffsetY: number } {
  const sx = spaceOf(plot.x.quantity)
  const sy = spaceOf(plot.y.quantity)
  const xSpan = sx.toSpace(plot.x.window.hi) - sx.toSpace(plot.x.window.lo)
  const ySpan = sy.toSpace(plot.y.window.hi) - sy.toSpace(plot.y.window.lo)
  return {
    labelOffsetX: Number.isFinite(xSpan) ? xSpan * fraction.x : 0,
    labelOffsetY: Number.isFinite(ySpan) ? ySpan * fraction.y : 0,
  }
}

/**
 * A box's region in plot pixels, <b>clamped</b> to the plotting area — or null when it does not
 * overlap the window at all.
 *
 * <p>Clamped, where a point outside the window is skipped, and the difference is the point of both
 * rules: a marker pressed against the axis claims a column that is not there, while a region clipped
 * at the axis claims exactly the columns that <i>are</i> there. Clamping here rather than leaving it
 * to the canvas clip is what puts the stroke <b>on</b> the plot edge, which is the visible statement
 * "this continues past here".</p>
 */
export function boxRect(item: AnnotationLayoutItem, plot: AnnotationPlot): Rect | null {
  if (item.y === null || item.x2 === null || item.y2 === null) return null
  if (!drawsAnnotation(item, plot)) return null

  const x1 = plot.x.posOf(item.x)
  const x2 = plot.x.posOf(item.x2)
  const y1 = plot.y.posOf(item.y)
  const y2 = plot.y.posOf(item.y2)
  if (![x1, x2, y1, y2].every(Number.isFinite)) return null

  const right = plot.left + plot.width
  const bottom = plot.top + plot.height
  const lo = { x: Math.min(x1, x2), y: Math.min(y1, y2) }
  const hi = { x: Math.max(x1, x2), y: Math.max(y1, y2) }
  if (hi.x <= plot.left || lo.x >= right || hi.y <= plot.top || lo.y >= bottom) return null

  const x = clamp(lo.x, plot.left, right)
  const y = clamp(lo.y, plot.top, bottom)
  return { x, y, w: clamp(hi.x, plot.left, right) - x, h: clamp(hi.y, plot.top, bottom) - y }
}

/**
 * The connector between two rectangles: the nearest edges, per axis independently, so it leaves each
 * one square. Null when they overlap on both axes — the label is on the region and there is nothing
 * to join, the same judgement {@link leaderTo} makes when the label swallows its marker.
 */
export function leaderBetween(a: Rect, b: Rect): { x1: number; y1: number; x2: number; y2: number } | null {
  const span = (aLo: number, aLen: number, bLo: number, bLen: number) => {
    const aHi = aLo + aLen
    const bHi = bLo + bLen
    if (bLo >= aHi) return { from: aHi, to: bLo, apart: true }
    if (bHi <= aLo) return { from: aLo, to: bHi, apart: true }
    const mid = (Math.max(aLo, bLo) + Math.min(aHi, bHi)) / 2
    return { from: mid, to: mid, apart: false }
  }

  const x = span(a.x, a.w, b.x, b.w)
  const y = span(a.y, a.h, b.y, b.h)
  if (!x.apart && !y.apart) return null
  return { x1: x.from, y1: y.from, x2: x.to, y2: y.to }
}

/** Which part of a box a grip drags. */
export type BoxHandleKind = 'move' | 'n' | 's' | 'e' | 'w' | 'nw' | 'ne' | 'se' | 'sw'

export interface BoxHandle {
  kind: BoxHandleKind
  rect: Rect
}

/**
 * The grips laid over a box: four corners, four edge bands and one move grip inset at the top left.
 *
 * <p><b>Move is a grip and not the box's interior</b>, which is the whole reason this list looks the
 * way it does: a region can cover the plot, and the interior is where drag-zoom lives — marking a
 * region and then zooming into it is the obvious next thing a user does. The same judgement keeps the
 * label's own handle small.</p>
 *
 * <p>A box too small to carry them keeps only its corners, so the grips can never overlap each other
 * and strand the region.</p>
 */
export function boxHandles(box: Rect, m: AnnotationMetrics): BoxHandle[] {
  const g = Math.max(6, m.fontPx * 0.8)
  const corners: BoxHandle[] = [
    { kind: 'nw', rect: { x: box.x - g / 2, y: box.y - g / 2, w: g, h: g } },
    { kind: 'ne', rect: { x: box.x + box.w - g / 2, y: box.y - g / 2, w: g, h: g } },
    { kind: 'se', rect: { x: box.x + box.w - g / 2, y: box.y + box.h - g / 2, w: g, h: g } },
    { kind: 'sw', rect: { x: box.x - g / 2, y: box.y + box.h - g / 2, w: g, h: g } },
  ]
  if (box.w < 3 * g || box.h < 3 * g) return corners

  const midX = box.x + box.w / 2
  const midY = box.y + box.h / 2
  return [
    ...corners,
    { kind: 'n', rect: { x: midX - g, y: box.y - g / 2, w: 2 * g, h: g } },
    { kind: 's', rect: { x: midX - g, y: box.y + box.h - g / 2, w: 2 * g, h: g } },
    { kind: 'w', rect: { x: box.x - g / 2, y: midY - g, w: g, h: 2 * g } },
    { kind: 'e', rect: { x: box.x + box.w - g / 2, y: midY - g, w: g, h: 2 * g } },
    { kind: 'move', rect: { x: box.x + g * 0.75, y: box.y + g * 0.75, w: g * 1.4, h: g * 1.4 } },
  ]
}

/** A box in stored values — the shape the drag maths works in. */
export interface DataBox {
  x: number
  x2: number
  y: number
  y2: number
}

/** A plotted window in stored values, and the two spaces its axes measure offsets in. */
export interface AnnotationWindow {
  xLo: number
  xHi: number
  yLo: number
  yHi: number
}

/** The spaces a box's own two quantities measure extents in. */
export interface BoxSpaces {
  x: AnnotationSpace
  y: AnnotationSpace
}

export function boxSpaces(xQuantity: string, yQuantity: string): BoxSpaces {
  return { x: spaceOf(xQuantity), y: spaceOf(yQuantity) }
}

/**
 * The box a grip drag proposes, from the box it started on and where the pointer is now (in stored
 * values).
 *
 * <p><b>Move keeps the region's extent in the axes' own spaces, never in pixels</b>: a region dragged
 * along a log axis keeps the octave width it was drawn with rather than stretching as it travels, and
 * one on a linear axis keeps its width in that axis's units. Resize touches only the edges its kind
 * names, and an edge dragged past its opposite simply crosses it — the caller normalises on the way
 * to storage, which is what every resize tool does.</p>
 */
export function boxFromDrag(
  kind: BoxHandleKind,
  start: DataBox,
  pointer: { x: number; y: number },
  spaces: BoxSpaces,
): DataBox {
  if (!Number.isFinite(pointer.x) || !Number.isFinite(pointer.y)) return start

  if (kind === 'move') {
    const width = spaces.x.toSpace(start.x2) - spaces.x.toSpace(start.x)
    const height = spaces.y.toSpace(start.y) - spaces.y.toSpace(start.y2)
    const pointerX = spaces.x.toSpace(pointer.x)
    const pointerY = spaces.y.toSpace(pointer.y)
    if (![width, height, pointerX, pointerY].every(Number.isFinite)) return start
    return {
      x: pointer.x,
      x2: spaces.x.fromSpace(pointerX + width),
      y: pointer.y,
      y2: spaces.y.fromSpace(pointerY - height),
    }
  }

  const next = { ...start }
  if (kind.includes('w')) next.x = pointer.x
  if (kind.includes('e')) next.x2 = pointer.x
  if (kind.includes('n')) next.y = pointer.y
  if (kind.includes('s')) next.y2 = pointer.y
  return next
}

/**
 * A dragged box kept inside the window and no smaller than `minSpan` (in each axis's own space).
 *
 * <p>A box that would leave the window is <b>translated</b> back in rather than squashed, so a move
 * never silently resizes. The size floor is a UI-integrity one — about a grip's width — so the grips
 * can never collapse into each other and strand the region; it is a different number and a different
 * reason from the minimum size of the <i>gesture</i> that draws a box in the first place.</p>
 */
export function clampBoxToWindow(
  box: DataBox,
  window: AnnotationWindow,
  minSpan: { x: number; y: number },
  spaces: BoxSpaces,
): DataBox {
  const sx = spaces.x
  const sy = spaces.y
  const winXLo = sx.toSpace(window.xLo)
  const winXHi = sx.toSpace(window.xHi)
  const winYLo = sy.toSpace(window.yLo)
  const winYHi = sy.toSpace(window.yHi)
  if (![winXLo, winXHi, winYLo, winYHi].every(Number.isFinite)) return box
  if (!(winXHi > winXLo) || !(winYHi > winYLo)) return box

  let lo = sx.toSpace(Math.min(box.x, box.x2))
  let hi = sx.toSpace(Math.max(box.x, box.x2))
  let top = sy.toSpace(Math.max(box.y, box.y2))
  let bottom = sy.toSpace(Math.min(box.y, box.y2))
  if (![lo, hi, top, bottom].every(Number.isFinite)) return box

  // The floor first, so a box squeezed to nothing against an edge still has grips to grab.
  if (hi - lo < minSpan.x) hi = lo + minSpan.x
  if (top - bottom < minSpan.y) top = bottom + minSpan.y

  const width = hi - lo
  const height = top - bottom
  if (lo < winXLo) {
    lo = winXLo
    hi = lo + width
  }
  if (hi > winXHi) {
    hi = winXHi
    lo = Math.max(winXLo, hi - width)
  }
  if (top > winYHi) {
    top = winYHi
    bottom = top - height
  }
  if (bottom < winYLo) {
    bottom = winYLo
    top = Math.min(winYHi, bottom + height)
  }

  return { x: sx.fromSpace(lo), x2: sx.fromSpace(hi), y: sy.fromSpace(top), y2: sy.fromSpace(bottom) }
}

/**
 * The leader line from a marker to the nearest edge of its label box — so a label dragged to the left
 * of its peak gets a line out of the dot's left side rather than one crossing the box.
 */
function leaderTo(
  anchor: { x: number; y: number; r: number },
  label: { x: number; y: number; w: number; h: number },
): { x1: number; y1: number; x2: number; y2: number } | null {
  const targetX = clamp(anchor.x, label.x, label.x + label.w)
  const targetY = clamp(anchor.y, label.y, label.y + label.h)
  const dx = targetX - anchor.x
  const dy = targetY - anchor.y
  const length = Math.hypot(dx, dy)
  if (!(length > anchor.r)) return null
  return {
    x1: anchor.x + (dx / length) * anchor.r,
    y1: anchor.y + (dy / length) * anchor.r,
    x2: targetX,
    y2: targetY,
  }
}

/**
 * Where a label sits for an annotation anchored at `(anchorX, anchorY)`: its stored offset applied in
 * the <b>stored quantity's own space</b>, then projected. Shared by the point and the box so the two
 * cannot drift — a box's anchor is its top-left corner, which is exactly why it needs no second rule.
 *
 * <p>Applying the offset before the projection rather than after is what keeps a label still when the
 * axis changes what it displays: an offset that is a ratio in percent is a constant difference in dB,
 * so a distortion label stays beside its point across the chart's %/dB switch.</p>
 */
function labelBoxAt(
  plot: AnnotationPlot,
  item: AnnotationLayoutItem,
  anchorX: number,
  anchorY: number,
  fallback: { x: number; y: number },
  w: number,
  h: number,
): Rect {
  const right = plot.left + plot.width
  const bottom = plot.top + plot.height
  const sx = spaceOf(item.xQuantity)
  const sy = spaceOf(item.yQuantity)
  const cx = plot.x.posOf(sx.fromSpace(sx.toSpace(anchorX) + item.labelOffsetX))
  const cy = plot.y.posOf(sy.fromSpace(sy.toSpace(anchorY) + item.labelOffsetY))
  return {
    x: clamp((Number.isFinite(cx) ? cx : fallback.x) - w / 2, plot.left, right - w),
    y: clamp((Number.isFinite(cy) ? cy : fallback.y) - h / 2, plot.top, bottom - h),
    w,
    h,
  }
}

/**
 * Every annotation's geometry, in the adapter's own pixels.
 *
 * <p>Three rules worth knowing. An annotation stored in a quantity this plot is not showing is
 * <b>skipped</b> — not converted, not moved, not dropped from the model — so switching a sweep's x
 * axis from hertz to watts hides the hertz notes and switching back brings them out again. An
 * annotation whose x is outside the plotted window is skipped rather than clamped to the edge, the
 * same judgement the response chart's pinned cursor makes, since a label pressed against the axis
 * claims a column that is not there. And rule labels <b>stack</b> into rows at the top of the plot,
 * dropping a row when they would overlap a neighbour and giving up (invisible) past the rows that
 * fit, which is the "labels only where they fit" call the multitone markers already make.</p>
 *
 * @param numbers Whether the ordinal badge is drawn. It changes the box's width, so it belongs here
 *   rather than being painted over the top afterwards — the report turns it off when its Annotations
 *   table is not included, and a box sized for a badge that is not drawn reads as a typo.
 */
export function layoutAnnotations(
  items: readonly AnnotationLayoutItem[],
  plot: AnnotationPlot,
  numbers = true,
): AnnotationPlacement[] {
  const m = annotationMetrics(plot)
  const right = plot.left + plot.width
  const bottom = plot.top + plot.height
  const rowPitch = m.boxHeight + m.fontPx * 0.3
  const maxRows = Math.max(1, Math.floor(plot.height / (3 * m.boxHeight)))
  const rows: { x1: number; x2: number }[][] = []

  const placements: AnnotationPlacement[] = []
  for (const item of items) {
    const base: AnnotationPlacement = {
      id: item.id,
      number: item.number,
      style: item.style,
      text: item.text,
      visible: false,
      fontPx: m.fontPx,
      anchor: null,
      rule: null,
      box: null,
      label: { x: 0, y: 0, w: 0, h: m.boxHeight },
      leader: null,
      badge: null,
      textX: 0,
      textY: 0,
    }

    // This plot is not the one that annotation was drawn against -- a different x quantity, or a y
    // axis showing something else. Nothing is drawn and nothing is changed.
    if (!drawsAnnotation(item, plot)) {
      placements.push(base)
      continue
    }

    const textWidth = item.text ? plot.measureText(item.text, m.fontPx) : 0
    const badgeWidth = numbers ? 2 * m.badgeR + m.badgeGap : 0
    const w = m.padX * 2 + badgeWidth + textWidth
    const h = m.boxHeight

    // The box is laid out *before* the off-window test below, which looks at `item.x` alone: a region
    // whose low edge is left of the window is still mostly on screen, and testing its left edge the
    // way a point's position is tested would make it vanish on the first zoom.
    if (item.style === 'box') {
      const rect = boxRect(item, plot)
      if (rect === null || item.y === null) {
        placements.push(base)
        continue
      }
      const label = labelBoxAt(plot, item, item.x, item.y, { x: rect.x + rect.w / 2, y: rect.y }, w, h)
      placements.push({
        ...base,
        visible: true,
        box: rect,
        label,
        leader: leaderBetween(rect, label),
        ...labelContents(label, m, numbers),
      })
      continue
    }

    const x = plot.x.posOf(item.x)
    if (!Number.isFinite(x) || x < plot.left || x > right) {
      placements.push(base)
      continue
    }

    if (item.style === 'point') {
      if (item.y === null || !Number.isFinite(item.y)) {
        placements.push(base)
        continue
      }
      const y = plot.y.posOf(item.y)
      if (!Number.isFinite(y)) {
        placements.push(base)
        continue
      }
      const anchor = { x, y: clamp(y, plot.top, bottom), r: m.markerR }
      const label = labelBoxAt(plot, item, item.x, item.y, { x, y }, w, h)
      placements.push({
        ...base,
        visible: true,
        anchor,
        label,
        leader: leaderTo(anchor, label),
        ...labelContents(label, m, numbers),
      })
      continue
    }

    // A rule: the label goes to the right of the line, or to its left where there is no room, in the
    // first row at the top of the plot that nothing else has taken.
    let row = -1
    let labelX = x + m.gap
    if (labelX + w > right) labelX = x - m.gap - w
    labelX = clamp(labelX, plot.left, right - w)
    for (let candidate = 0; candidate < maxRows; candidate++) {
      const taken = rows[candidate] ?? []
      if (taken.every((box) => labelX > box.x2 + m.gap || labelX + w < box.x1 - m.gap)) {
        row = candidate
        break
      }
    }
    if (row < 0) {
      placements.push(base)
      continue
    }
    rows[row] = [...(rows[row] ?? []), { x1: labelX, x2: labelX + w }]

    const label = { x: labelX, y: plot.top + m.padY + row * rowPitch, w, h }
    placements.push({
      ...base,
      visible: true,
      rule: { x, y1: plot.top, y2: bottom },
      label,
      ...labelContents(label, m, numbers),
    })
  }

  return placements
}

function labelContents(
  label: { x: number; y: number; w: number; h: number },
  m: AnnotationMetrics,
  numbers: boolean,
): Pick<AnnotationPlacement, 'badge' | 'textX' | 'textY'> {
  const middle = label.y + label.h / 2
  const badge = numbers ? { x: label.x + m.padX + m.badgeR, y: middle, r: m.badgeR } : null
  return {
    badge,
    textX: label.x + m.padX + (numbers ? 2 * m.badgeR + m.badgeGap : 0),
    textY: middle,
  }
}

/**
 * A dragged label's offset, clamped so the box stays on the plot — in the <b>stored quantities' own
 * spaces</b>, which is the form the offset is stored in (see ChartAnnotation.labelOffsetX).
 *
 * <p>Done in that space rather than by clamping pixels and converting back, because the stored value
 * has to be sane in its own terms: the same annotation is drawn on a 1600 px export and re-laid-out
 * after a zoom, and an offset that was only ever valid for the pixels it was dragged on would wander.
 * Clamping in the stored quantity rather than in the plotted one is also what keeps a unit switch
 * from moving a label that was already near an edge.</p>
 */
export function clampLabelOffset(args: {
  x: number
  y: number
  offsetX: number
  offsetY: number
  window: AnnotationWindow
  spaces: BoxSpaces
  /** The label box, in plot pixels. */
  label: { w: number; h: number }
  plot: { width: number; height: number }
}): { offsetX: number; offsetY: number } {
  const { x, y, window: win, spaces, label, plot } = args
  const fallback = { offsetX: args.offsetX, offsetY: args.offsetY }
  if (!(plot.width > 0) || !(plot.height > 0)) return fallback

  const anchorX = spaces.x.toSpace(x)
  const anchorY = spaces.y.toSpace(y)
  const xLo = spaces.x.toSpace(win.xLo)
  const xHi = spaces.x.toSpace(win.xHi)
  const yLo = spaces.y.toSpace(win.yLo)
  const yHi = spaces.y.toSpace(win.yHi)
  if (![anchorX, anchorY, xLo, xHi, yLo, yHi].every(Number.isFinite)) return fallback
  if (!(xHi > xLo) || !(yHi > yLo)) return fallback

  const marginX = (label.w / 2 / plot.width) * (xHi - xLo)
  const marginY = (label.h / 2 / plot.height) * (yHi - yLo)

  const targetX = clamp(anchorX + args.offsetX, xLo + marginX, xHi - marginX)
  const targetY = clamp(anchorY + args.offsetY, yLo + marginY, yHi - marginY)

  return { offsetX: targetX - anchorX, offsetY: targetY - anchorY }
}

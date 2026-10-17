/**
 * Where an annotation's marker, leader, label box and ordinal badge go on a plot.
 *
 * <p>Deliberately knows nothing about uPlot: it takes an {@link AnnotationPlot} adapter, so the same
 * arithmetic serves the <b>canvas</b> (device pixels, `valToPos(..., true)`, which is what the
 * report's PNG is made of) and the <b>DOM overlay</b> (CSS pixels, `valToPos(..., false)`, which is
 * what a mouse can grab). Those two have to agree to the pixel or a label would be drawn in one
 * place and dragged from another — one function for both is how that is guaranteed. It is also the
 * only way to test any of this, nothing in a `.vue` being testable here.</p>
 *
 * <p>The sizes are all expressed in units of the font, and the font is a fixed fraction of the
 * plot's width ({@link annotationFontPx}). That is what makes the 1600 px export label read the
 * same on the page as the ~700 px screen label does on screen: `composeChartPng` draws the plot
 * canvas scaled onto the requested size, so anything that is a fixed fraction of the plot survives
 * the rescale and anything measured in absolute pixels does not.</p>
 */

import type { AnnotationStyle } from '../measurements/spectrumAnnotations'

/** A plot to lay annotations out on, in whatever pixel space the caller works in. */
export interface AnnotationPlot {
  /** The plotting area, in this adapter's own pixels. */
  left: number
  top: number
  width: number
  height: number
  xToPos(freqHz: number): number
  yToPos(levelDb: number): number
  /**
   * This adapter's pixels per CSS pixel — 1 for the DOM overlay, the device pixel ratio for the
   * canvas (uPlot draws in device pixels). The font is chosen from the CSS width and then scaled by
   * this, so a hidpi screen does not get half-size labels.
   */
  scale: number
  measureText(text: string, fontPx: number): number
}

/** One annotation, with its level already converted to whatever the chart is plotting. */
export interface AnnotationLayoutItem {
  id: number
  number: number
  style: AnnotationStyle
  text: string
  freqHz: number
  /**
   * The plotted level: a point's marker, or a <b>box</b>'s top edge. Null for a frequency rule, or
   * when the unit cannot be converted.
   */
  levelDb: number | null
  /** A box's other two edges, already in the plotted unit. Null for every other style. */
  freqHz2: number | null
  levelDb2: number | null
  labelOffsetOctaves: number
  labelOffsetDb: number
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
  /** False when there is nothing to draw: off the frequency window, no level, or no room to stack. */
  visible: boolean
  fontPx: number
  /** The marker dot, for a point. */
  anchor: { x: number; y: number; r: number } | null
  /** The vertical rule, for a frequency annotation. */
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
 * The label font for a plot this wide, in CSS pixels. Proportional, so the label is the same
 * fraction of the picture on screen and in the 1600 px export; bounded, so a chart squeezed into a
 * narrow column still has readable labels and a very wide one does not get a headline.
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

/**
 * A box's region in plot pixels, <b>clamped</b> to the plotting area — or null when it does not
 * overlap the window at all.
 *
 * <p>Clamped, where a point outside the window is skipped, and the difference is the point of both
 * rules: a marker pressed against the axis claims a column that is not there, while a region
 * clipped at the axis claims exactly the columns that <i>are</i> there. Clamping here rather than
 * leaving it to the canvas clip is what puts the stroke <b>on</b> the plot edge, which is the
 * visible statement "this continues past here".</p>
 */
export function boxRect(item: AnnotationLayoutItem, plot: AnnotationPlot): Rect | null {
  if (item.levelDb === null || item.freqHz2 === null || item.levelDb2 === null) return null
  if (!(item.freqHz > 0) || !(item.freqHz2 > 0)) return null

  const x1 = plot.xToPos(item.freqHz)
  const x2 = plot.xToPos(item.freqHz2)
  const y1 = plot.yToPos(item.levelDb)
  const y2 = plot.yToPos(item.levelDb2)
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
 * region and then zooming into it is the obvious next thing a user does. The same judgement keeps
 * the label's own handle small.</p>
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

/** A box in data units — the shape the drag maths works in. */
export interface DataBox {
  freqHz: number
  freqHz2: number
  levelDb: number
  levelDb2: number
}

/**
 * The box a grip drag proposes, from the box it started on and where the pointer is now (in data
 * units).
 *
 * <p><b>Move keeps the region's extent in octaves and dB, never in pixels</b>: a region dragged
 * along a log axis keeps the octave width it was drawn with rather than stretching as it travels.
 * Resize touches only the edges its kind names, and an edge dragged past its opposite simply
 * crosses it — the caller normalises on the way to storage, which is what every resize tool does.</p>
 */
export function boxFromDrag(kind: BoxHandleKind, start: DataBox, pointer: { freqHz: number; levelDb: number }): DataBox {
  if (!(pointer.freqHz > 0) || !Number.isFinite(pointer.levelDb)) return start

  if (kind === 'move') {
    const octaves = Math.log2(start.freqHz2 / start.freqHz)
    const height = start.levelDb - start.levelDb2
    return {
      freqHz: pointer.freqHz,
      freqHz2: pointer.freqHz * Math.pow(2, octaves),
      levelDb: pointer.levelDb,
      levelDb2: pointer.levelDb - height,
    }
  }

  const next = { ...start }
  if (kind.includes('w')) next.freqHz = pointer.freqHz
  if (kind.includes('e')) next.freqHz2 = pointer.freqHz
  if (kind.includes('n')) next.levelDb = pointer.levelDb
  if (kind.includes('s')) next.levelDb2 = pointer.levelDb
  return next
}

/**
 * A dragged box kept inside the window and no smaller than `minSpan`.
 *
 * <p>A box that would leave the window is <b>translated</b> back in rather than squashed, so a move
 * never silently resizes. The size floor is a UI-integrity one — about a grip's width — so the grips
 * can never collapse into each other and strand the region; it is a different number and a different
 * reason from the minimum size of the <i>gesture</i> that draws a box in the first place.</p>
 */
export function clampBoxToWindow(
  box: DataBox,
  window: { loHz: number; hiHz: number; bottomDb: number; topDb: number },
  minSpan: { octaves: number; db: number },
): DataBox {
  if (!(box.freqHz > 0) || !(box.freqHz2 > 0) || !(window.loHz > 0) || !(window.hiHz > window.loHz)) return box
  if (!Number.isFinite(box.levelDb) || !Number.isFinite(box.levelDb2)) return box

  let loHz = Math.min(box.freqHz, box.freqHz2)
  let hiHz = Math.max(box.freqHz, box.freqHz2)
  let top = Math.max(box.levelDb, box.levelDb2)
  let bottom = Math.min(box.levelDb, box.levelDb2)

  // The floor first, so a box squeezed to nothing against an edge still has grips to grab.
  if (Math.log2(hiHz / loHz) < minSpan.octaves) hiHz = loHz * Math.pow(2, minSpan.octaves)
  if (top - bottom < minSpan.db) top = bottom + minSpan.db

  const widthOctaves = Math.log2(hiHz / loHz)
  const heightDb = top - bottom
  if (loHz < window.loHz) {
    loHz = window.loHz
    hiHz = loHz * Math.pow(2, widthOctaves)
  }
  if (hiHz > window.hiHz) {
    hiHz = window.hiHz
    loHz = Math.max(window.loHz, hiHz * Math.pow(2, -widthOctaves))
  }
  if (top > window.topDb) {
    top = window.topDb
    bottom = top - heightDb
  }
  if (bottom < window.bottomDb) {
    bottom = window.bottomDb
    top = Math.min(window.topDb, bottom + heightDb)
  }

  return { freqHz: loHz, freqHz2: hiHz, levelDb: top, levelDb2: bottom }
}

/**
 * The leader line from a marker to the nearest edge of its label box — so a label dragged to the
 * left of its peak gets a line out of the dot's left side rather than one crossing the box.
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
 * Where a label sits for an annotation anchored at `(anchorHz, anchorDb)`: its stored offset applied
 * in data units, then clamped inside the plot. Shared by the point and the box so the two cannot
 * drift — a box's anchor is its top-left corner, which is exactly why it needs no second rule.
 */
function labelBoxAt(
  plot: AnnotationPlot,
  anchorHz: number,
  anchorDb: number,
  offsetOctaves: number,
  offsetDb: number,
  fallback: { x: number; y: number },
  w: number,
  h: number,
): Rect {
  const right = plot.left + plot.width
  const bottom = plot.top + plot.height
  const cx = plot.xToPos(anchorHz * Math.pow(2, offsetOctaves))
  const cy = plot.yToPos(anchorDb + offsetDb)
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
 * <p>Two rules worth knowing. An annotation whose frequency is outside the plotted window is
 * <b>skipped, not clamped to the edge</b> — the same judgement the response chart's pinned cursor
 * makes, since a label pressed against the axis claims a column that is not there. And frequency
 * labels <b>stack</b> into rows at the top of the plot, dropping a row when they would overlap a
 * neighbour and giving up (invisible) past the rows that fit, which is the "labels only where they
 * fit" call the multitone markers already make.</p>
 *
 * @param numbers Whether the ordinal badge is drawn. It changes the box's width, so it belongs here
 *   rather than being painted over the top afterwards — the report turns it off when its
 *   Annotations table is not included, and a box sized for a badge that is not drawn reads as a
 *   typo.
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

    const textWidth = item.text ? plot.measureText(item.text, m.fontPx) : 0
    const badgeWidth = numbers ? 2 * m.badgeR + m.badgeGap : 0
    const w = m.padX * 2 + badgeWidth + textWidth
    const h = m.boxHeight

    // The box is laid out *before* the off-window test below, which looks at `item.freqHz` alone: a
    // region whose low edge is left of the window is still mostly on screen, and testing its left
    // edge the way a point's position is tested would make it vanish on the first zoom.
    if (item.style === 'box') {
      const rect = boxRect(item, plot)
      if (rect === null || item.levelDb === null) {
        placements.push(base)
        continue
      }
      const label = labelBoxAt(
        plot,
        item.freqHz,
        item.levelDb,
        item.labelOffsetOctaves,
        item.labelOffsetDb,
        { x: rect.x + rect.w / 2, y: rect.y },
        w,
        h,
      )
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

    const x = plot.xToPos(item.freqHz)
    if (!Number.isFinite(x) || x < plot.left || x > right) {
      placements.push(base)
      continue
    }

    if (item.style === 'point') {
      if (item.levelDb === null || !Number.isFinite(item.levelDb)) {
        placements.push(base)
        continue
      }
      const y = plot.yToPos(item.levelDb)
      if (!Number.isFinite(y)) {
        placements.push(base)
        continue
      }
      const anchor = { x, y: clamp(y, plot.top, bottom), r: m.markerR }
      const label = labelBoxAt(
        plot,
        item.freqHz,
        item.levelDb,
        item.labelOffsetOctaves,
        item.labelOffsetDb,
        { x, y },
        w,
        h,
      )
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

    // A frequency rule: the label goes to the right of the line, or to its left where there is no
    // room, in the first row at the top of the plot that nothing else has taken.
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
 * A dragged label's offset, clamped so the box stays on the plot — in <b>data units</b>, which is
 * the form the offset is stored in (see SpectrumAnnotation.labelOffsetOctaves).
 *
 * <p>Done in data space rather than by clamping pixels and converting back, because the stored
 * value has to be sane in its own terms: the same annotation is drawn on a 1600 px export and
 * re-laid-out after a zoom, and an offset that was only ever valid for the pixels it was dragged on
 * would wander. `levelDb` and the window are in whatever unit the chart is plotting; the returned
 * dB offset is a difference, so it is unit-free either way.</p>
 */
export function clampLabelOffset(args: {
  freqHz: number
  levelDb: number
  octaves: number
  db: number
  window: { loHz: number; hiHz: number; bottomDb: number; topDb: number }
  /** The label box, in plot pixels. */
  label: { w: number; h: number }
  plot: { width: number; height: number }
}): { octaves: number; db: number } {
  const { freqHz, levelDb, window: win, label, plot } = args
  if (!(freqHz > 0) || !(win.loHz > 0) || !(win.hiHz > win.loHz) || !(plot.width > 0) || !(plot.height > 0)) {
    return { octaves: args.octaves, db: args.db }
  }

  const spanOctaves = Math.log2(win.hiHz / win.loHz)
  const spanDb = win.topDb - win.bottomDb
  const marginOctaves = ((label.w / 2) / plot.width) * spanOctaves
  const marginDb = ((label.h / 2) / plot.height) * spanDb

  const targetHz = clamp(
    freqHz * Math.pow(2, args.octaves),
    win.loHz * Math.pow(2, marginOctaves),
    win.hiHz * Math.pow(2, -marginOctaves),
  )
  const targetDb = clamp(levelDb + args.db, win.bottomDb + marginDb, win.topDb - marginDb)

  return { octaves: Math.log2(targetHz / freqHz), db: targetDb - levelDb }
}

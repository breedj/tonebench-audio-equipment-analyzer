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
  /** The plotted level for a point; null for a frequency rule, or when the unit cannot be converted. */
  levelDb: number | null
  labelOffsetOctaves: number
  labelOffsetDb: number
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
      label: { x: 0, y: 0, w: 0, h: m.boxHeight },
      leader: null,
      badge: null,
      textX: 0,
      textY: 0,
    }

    const x = plot.xToPos(item.freqHz)
    if (!Number.isFinite(x) || x < plot.left || x > right) {
      placements.push(base)
      continue
    }

    const textWidth = item.text ? plot.measureText(item.text, m.fontPx) : 0
    const badgeWidth = numbers ? 2 * m.badgeR + m.badgeGap : 0
    const w = m.padX * 2 + badgeWidth + textWidth
    const h = m.boxHeight

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
      const cx = plot.xToPos(item.freqHz * Math.pow(2, item.labelOffsetOctaves))
      const cy = plot.yToPos(item.levelDb + item.labelOffsetDb)
      const label = {
        x: clamp((Number.isFinite(cx) ? cx : x) - w / 2, plot.left, right - w),
        y: clamp((Number.isFinite(cy) ? cy : y) - h / 2, plot.top, bottom - h),
        w,
        h,
      }
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

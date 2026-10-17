/**
 * Draws the annotations a {@link layoutAnnotations} call has placed, onto any 2-D context.
 *
 * <p>In a `.ts` module rather than inside a chart's uPlot plugin for two reasons. It is shared: nine
 * uPlot charts reach it through charts/uplotAnnotations.ts and three hand-drawn plane charts call it
 * directly from their own `renderScene`, and twelve copies of this would drift within a week. And it
 * is the one part of the feature whose <b>order</b> is load-bearing — regions before labels, the
 * dash set explicitly, the shadow cleared before the border — which a fake context can pin here and
 * could pin nowhere inside a `.vue`.</p>
 *
 * <p>Nothing here registers a hook, reads a ref or touches the DOM, which is what lets the report's
 * offscreen render, the Screenshot dialog and the black-and-white print get the labels with no
 * further code: they all end in a canvas, and this paints a canvas.</p>
 */

import { annotationColor } from '../measurements/chartAnnotations'
import type { AnnotationPlacement } from './annotationLayout'

/** The five theme colours the labels are drawn in. */
export interface AnnotationTheme {
  /** Leaders, anchors and a rule's dashed line. */
  annotationLine: string
  annotationText: string
  annotationFill: string
  annotationBorder: string
  annotationShadow: string
}

export function annotationFont(px: number): string {
  return `${px}px ui-sans-serif, system-ui, sans-serif`
}

export function roundedRectPath(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
) {
  ctx.beginPath()
  // roundRect is Chromium 99+; the plain rect is the same fallback bandwidthPlugin carries.
  if (typeof ctx.roundRect === 'function') ctx.roundRect(x, y, w, h, r)
  else ctx.rect(x, y, w, h)
}

/**
 * Paints every placement, clipped to `area`.
 *
 * @param scale This context's pixels per CSS pixel, so a hidpi canvas gets proportional line widths.
 */
export function paintAnnotations(
  ctx: CanvasRenderingContext2D,
  placements: readonly AnnotationPlacement[],
  theme: AnnotationTheme,
  scale: number,
  area: { left: number; top: number; width: number; height: number },
) {
  if (placements.length === 0) return

  ctx.save()
  ctx.beginPath()
  ctx.rect(area.left, area.top, area.width, area.height)
  ctx.clip()

  // Pass one: every region. Drawn before any label so one annotation's translucent fill can never
  // bury another's note, and never as uPlot's own `.u-select` div -- the report, the Screenshot
  // dialog and the black-and-white print only ever see a chart's exportPng canvas.
  for (const p of placements) {
    if (!p.visible || !p.box) continue
    const colour = annotationColor(p.number)
    roundedRectPath(ctx, p.box.x, p.box.y, p.box.w, p.box.h, p.fontPx * 0.25)
    ctx.fillStyle = colour + '1a'
    ctx.fill()
    // Solid, not dashed: the dash belongs to the rule style, which still exists on measurements
    // saved before the picker stopped offering it. Set explicitly rather than assumed -- this is the
    // first stroke made here, so it inherits whatever dash the plugin drawn before it left on the
    // context, and it came out dotted until it said so.
    ctx.setLineDash([])
    ctx.strokeStyle = colour
    ctx.lineWidth = 1.5 * scale
    ctx.stroke()
  }

  for (const p of placements) {
    if (!p.visible) continue

    // A rule's line, in the response chart's pinned-cursor dash so the two read as one family of
    // "someone marked this".
    if (p.rule) {
      ctx.setLineDash([5, 4])
      ctx.strokeStyle = theme.annotationLine
      ctx.lineWidth = 1.5 * scale
      ctx.beginPath()
      ctx.moveTo(p.rule.x, p.rule.y1)
      ctx.lineTo(p.rule.x, p.rule.y2)
      ctx.stroke()
      ctx.setLineDash([])
    }

    if (p.leader) {
      ctx.strokeStyle = theme.annotationLine
      ctx.lineWidth = scale
      ctx.beginPath()
      ctx.moveTo(p.leader.x1, p.leader.y1)
      ctx.lineTo(p.leader.x2, p.leader.y2)
      ctx.stroke()
    }

    if (p.anchor) {
      ctx.fillStyle = theme.annotationLine
      ctx.beginPath()
      ctx.arc(p.anchor.x, p.anchor.y, p.anchor.r, 0, Math.PI * 2)
      ctx.fill()
    }

    // The box carries a small drop shadow, so a label over a dense trace reads as sitting above the
    // picture rather than being part of it. Only the fill: the shadow is cleared before the border,
    // or every stroke after it would trail one too.
    roundedRectPath(ctx, p.label.x, p.label.y, p.label.w, p.label.h, p.fontPx * 0.35)
    ctx.shadowColor = theme.annotationShadow
    ctx.shadowBlur = 4 * scale
    ctx.shadowOffsetY = scale
    ctx.fillStyle = theme.annotationFill
    ctx.fill()
    ctx.shadowColor = 'transparent'
    ctx.shadowBlur = 0
    ctx.shadowOffsetY = 0
    ctx.strokeStyle = theme.annotationBorder
    ctx.lineWidth = scale
    ctx.stroke()

    if (p.badge) {
      // The ordinal's own colour, matching its row in the Annotations panel -- see annotationColor.
      // A tinted disc rather than a ring alone: at this size a 1 px coloured outline is nearly
      // invisible over a busy trace.
      const colour = annotationColor(p.number)
      ctx.fillStyle = colour + '26'
      ctx.beginPath()
      ctx.arc(p.badge.x, p.badge.y, p.badge.r, 0, Math.PI * 2)
      ctx.fill()
      ctx.strokeStyle = colour
      ctx.stroke()
      ctx.fillStyle = colour
      ctx.font = annotationFont(p.fontPx * 0.78)
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      ctx.fillText(`${p.number}`, p.badge.x, p.badge.y)
    }

    if (p.text) {
      ctx.fillStyle = theme.annotationText
      ctx.font = annotationFont(p.fontPx)
      ctx.textAlign = 'left'
      ctx.textBaseline = 'middle'
      ctx.fillText(p.text, p.textX, p.textY)
    }
  }

  ctx.restore()
}

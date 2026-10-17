import { describe, expect, it } from 'vitest'
import { paintAnnotations, type AnnotationTheme } from './annotationPainter'
import type { AnnotationPlacement } from './annotationLayout'

const THEME: AnnotationTheme = {
  annotationLine: '#line',
  annotationText: '#text',
  annotationFill: '#fill',
  annotationBorder: '#border',
  annotationShadow: '#shadow',
}

const AREA = { left: 0, top: 0, width: 700, height: 400 }

/** Records the calls in order, which is the only thing about this painting that can be wrong. */
function fakeContext() {
  const calls: string[] = []
  const ctx = {
    calls,
    save: () => calls.push('save'),
    restore: () => calls.push('restore'),
    beginPath: () => calls.push('beginPath'),
    rect: () => calls.push('rect'),
    roundRect: () => calls.push('roundRect'),
    clip: () => calls.push('clip'),
    fill: () => calls.push('fill'),
    stroke: () => calls.push('stroke'),
    arc: () => calls.push('arc'),
    moveTo: () => calls.push('moveTo'),
    lineTo: () => calls.push('lineTo'),
    fillText: (text: string) => calls.push(`fillText:${text}`),
    setLineDash: (pattern: number[]) => calls.push(`setLineDash:[${pattern.join(',')}]`),
    measureText: () => ({ width: 10 }),
    font: '',
    fillStyle: '',
    strokeStyle: '',
    lineWidth: 0,
    textAlign: '',
    textBaseline: '',
    shadowColor: '',
    shadowBlur: 0,
    shadowOffsetY: 0,
  }
  return ctx as unknown as CanvasRenderingContext2D & { calls: string[] }
}

function placement(partial: Partial<AnnotationPlacement> = {}): AnnotationPlacement {
  return {
    id: 1,
    number: 1,
    style: 'point',
    text: 'Mains hum',
    visible: true,
    fontPx: 11,
    anchor: { x: 100, y: 200, r: 3 },
    rule: null,
    box: null,
    label: { x: 120, y: 160, w: 90, h: 22 },
    leader: { x1: 103, y1: 200, x2: 120, y2: 180 },
    badge: { x: 130, y: 171, r: 7 },
    textX: 145,
    textY: 171,
    ...partial,
  }
}

const boxPlacement = (id: number, number: number) =>
  placement({ id, number, style: 'box', box: { x: 50, y: 50, w: 200, h: 100 }, anchor: null })

describe('paintAnnotations', () => {
  it('draws every region before any label, so one annotation’s fill cannot bury another’s note', () => {
    // Two boxes and the first one's label: both regions have to be down before any text goes on.
    const ctx = fakeContext()
    paintAnnotations(ctx, [boxPlacement(1, 1), boxPlacement(2, 2)], THEME, 1, AREA)

    const firstText = ctx.calls.findIndex((c) => c.startsWith('fillText'))
    const lastRegionStroke = ctx.calls.lastIndexOf('setLineDash:[]')
    expect(firstText).toBeGreaterThan(-1)
    expect(lastRegionStroke).toBeLessThan(firstText)
  })

  it('sets the dash explicitly before the first region stroke', () => {
    // The bug this pins: the first stroke inherits whatever dash the plugin drawn before it left on
    // the context, and the box outline came out dotted until it said so.
    const ctx = fakeContext()
    paintAnnotations(ctx, [boxPlacement(1, 1)], THEME, 1, AREA)
    const dash = ctx.calls.indexOf('setLineDash:[]')
    const stroke = ctx.calls.indexOf('stroke')
    expect(dash).toBeGreaterThan(-1)
    expect(dash).toBeLessThan(stroke)
  })

  it('clips to the plotting area and puts the context back as it found it', () => {
    const ctx = fakeContext()
    paintAnnotations(ctx, [placement()], THEME, 1, AREA)
    expect(ctx.calls[0]).toBe('save')
    expect(ctx.calls).toContain('clip')
    expect(ctx.calls[ctx.calls.length - 1]).toBe('restore')
  })

  it('leaves the dash cleared after a rule, so nothing drawn later trails one', () => {
    const ctx = fakeContext()
    paintAnnotations(ctx, [placement({ style: 'frequency', rule: { x: 300, y1: 0, y2: 400 }, anchor: null })], THEME, 1, AREA)
    const dashes = ctx.calls.filter((c) => c.startsWith('setLineDash'))
    expect(dashes[0]).toBe('setLineDash:[5,4]')
    expect(dashes[dashes.length - 1]).toBe('setLineDash:[]')
  })

  it('clears the shadow before the border, or every stroke after it would trail one', () => {
    const ctx = fakeContext()
    paintAnnotations(ctx, [placement()], THEME, 1, AREA)
    // The label's fill carries the shadow; by the time the border is stroked it is off again.
    expect(ctx.shadowBlur).toBe(0)
    expect(ctx.shadowColor).toBe('transparent')
  })

  it('draws the ordinal and the note, and the ordinal only when there is a badge', () => {
    const withBadge = fakeContext()
    paintAnnotations(withBadge, [placement()], THEME, 1, AREA)
    expect(withBadge.calls).toContain('fillText:1')
    expect(withBadge.calls).toContain('fillText:Mains hum')

    const bare = fakeContext()
    paintAnnotations(bare, [placement({ badge: null })], THEME, 1, AREA)
    expect(bare.calls).not.toContain('fillText:1')
    expect(bare.calls).toContain('fillText:Mains hum')
  })

  it('draws nothing at all for a placement that is not visible', () => {
    const ctx = fakeContext()
    paintAnnotations(ctx, [placement({ visible: false })], THEME, 1, AREA)
    expect(ctx.calls.some((c) => c.startsWith('fillText'))).toBe(false)
  })

  it('touches the context not at all when there is nothing to draw', () => {
    const ctx = fakeContext()
    paintAnnotations(ctx, [], THEME, 1, AREA)
    expect(ctx.calls).toEqual([])
  })

  it('scales its line widths with the canvas, so a hidpi plot is not hairlines', () => {
    const ctx = fakeContext()
    paintAnnotations(ctx, [boxPlacement(1, 1)], THEME, 2, AREA)
    expect(ctx.lineWidth).toBe(2)
  })
})

import { describe, expect, it } from 'vitest'
import { nextTick, ref } from 'vue'
import { useAnnotationOverlay, MIN_BOX_DRAG_PX, type AnnotationReadAt } from './useAnnotationOverlay'
import type { AnnotationPlot } from './annotationLayout'
import type { AnnotationStyle, ChartAnnotation } from '../measurements/chartAnnotations'

const VIEW = 'Spectrum'
const WINDOW = { loHz: 20, hiHz: 20000, bottomDb: -150, topDb: 20 }
const WIDTH = 700
const HEIGHT = 400

/** The spectrum chart's geometry — log frequency against dBV — in the CSS pixels a mouse works in. */
function plotOf(): AnnotationPlot {
  const spanOctaves = Math.log2(WINDOW.hiHz / WINDOW.loHz)
  return {
    left: 0,
    top: 0,
    width: WIDTH,
    height: HEIGHT,
    x: {
      quantity: 'hz',
      posOf: (hz) => (Math.log2(hz / WINDOW.loHz) / spanOctaves) * WIDTH,
      valOf: (pos) => WINDOW.loHz * Math.pow(2, (pos / WIDTH) * spanOctaves),
      window: { lo: WINDOW.loHz, hi: WINDOW.hiHz },
    },
    y: {
      quantity: 'dbv',
      posOf: (db) => ((WINDOW.topDb - db) / (WINDOW.topDb - WINDOW.bottomDb)) * HEIGHT,
      valOf: (pos) => WINDOW.topDb - (pos / HEIGHT) * (WINDOW.topDb - WINDOW.bottomDb),
      window: { lo: WINDOW.bottomDb, hi: WINDOW.topDb },
    },
    scale: 1,
    measureText: (text, fontPx) => text.length * 0.6 * fontPx,
  }
}

/** The surface pointer coordinates are measured against: only its bounding rect is ever read. */
function fakeSurface() {
  return { getBoundingClientRect: () => ({ left: 0, top: 0 }) } as unknown as HTMLElement
}

function setup(options: { readAt?: (pos: { x: number; y: number }) => AnnotationReadAt | null; plot?: AnnotationPlot } = {}) {
  const list = ref<ChartAnnotation[]>([])
  const armed = ref<AnnotationStyle | null>(null)
  let redraws = 0
  const controller = useAnnotationOverlay({
    view: () => VIEW,
    list,
    armed,
    numbers: () => true,
    cssPlot: () => options.plot ?? plotOf(),
    surface: fakeSurface,
    overlayRect: () => ({ left: 0, top: 0, width: WIDTH, height: HEIGHT }),
    redraw: () => {
      redraws++
    },
    readAt: options.readAt,
  })
  return { list, armed, controller, redraws: () => redraws }
}

/** A pointer event carrying only what the composable reads of one. */
function pointerAt(x: number, y: number) {
  return {
    clientX: x,
    clientY: y,
    pointerId: 1,
    currentTarget: { setPointerCapture: () => {} },
    preventDefault: () => {},
  } as unknown as PointerEvent
}

function clickAt(x: number, y: number, detail = 1) {
  return { clientX: x, clientY: y, detail } as unknown as MouseEvent
}

describe('placeAt', () => {
  it('stores the value the chart’s own readout was showing, in the axis’s own quantity', () => {
    // The snap and the trace reading are the chart's job; what matters here is that the figure the
    // readout answered is the figure stored, not the raw pointer position.
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -62.5, channel: 1 }) })
    expect(controller.placeAt({ x: 350, y: 120 }, 'point')).toBe(true)
    expect(list.value[0]).toMatchObject({
      x: 1000,
      y: -62.5,
      channel: 1,
      xQuantity: 'hz',
      yQuantity: 'dbv',
      view: VIEW,
      style: 'point',
      text: '',
    })
  })

  it('falls back to the pointer where the chart has nothing to read there', () => {
    const plot = plotOf()
    const { list, controller } = setup({ readAt: () => null })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    expect(list.value[0].x).toBeCloseTo(plot.x.valOf(350), 6)
    expect(list.value[0].y!).toBeCloseTo(plot.y.valOf(120), 6)
    expect(list.value[0].channel).toBeNull()
  })

  it('keeps a snapped x but takes the pointer’s level where the chart has no trace under it', () => {
    const plot = plotOf()
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: null, channel: null }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    expect(list.value[0].x).toBe(1000)
    expect(list.value[0].y!).toBeCloseTo(plot.y.valOf(120), 6)
  })

  it('stores nothing and stays armed when the axis cannot convert what was clicked', () => {
    // The spectrum under a density unit with no ENBW: its y axis answers a non-finite value, and a
    // wrong level would be worse than making the user click again.
    const plot = plotOf()
    const unconvertible: AnnotationPlot = { ...plot, y: { ...plot.y, valOf: () => Number.NaN } }
    const { list, armed, controller } = setup({ plot: unconvertible })
    armed.value = 'point'
    expect(controller.placeAt({ x: 350, y: 120 }, 'point')).toBe(false)
    expect(list.value).toEqual([])
    expect(armed.value).toBe('point')
  })

  it('places a rule although the y axis can say nothing, since a rule names no level', () => {
    const plot = plotOf()
    const unconvertible: AnnotationPlot = { ...plot, y: { ...plot.y, valOf: () => Number.NaN } }
    const { list, controller } = setup({ plot: unconvertible, readAt: () => ({ x: 1000, y: null, channel: null }) })
    expect(controller.placeAt({ x: 350, y: 120 }, 'frequency')).toBe(true)
    expect(list.value[0]).toMatchObject({ style: 'frequency', x: 1000, y: null })
    expect(list.value[0].labelOffsetX).toBe(0)
  })

  it('disarms the chart and opens the text box on what it just placed', () => {
    const { armed, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    armed.value = 'point'
    controller.placeAt({ x: 350, y: 120 }, 'point')
    expect(armed.value).toBeNull()
    expect(controller.editing.value).toBe(1)
  })

  it('offers the new label an offset up and to the right of what it names', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    expect(list.value[0].labelOffsetX).toBeGreaterThan(0)
    expect(list.value[0].labelOffsetY).toBeGreaterThan(0)
  })
})

describe('placeBoxFrom', () => {
  it('stores the region the drag swept, top-left corner first', () => {
    const plot = plotOf()
    const { list, controller } = setup()
    expect(controller.placeBoxFrom({ left: 100, top: 80, width: 200, height: 120 })).toBe(true)
    expect(list.value[0]).toMatchObject({ style: 'box', channel: null, view: VIEW })
    expect(list.value[0].x).toBeCloseTo(plot.x.valOf(100), 6)
    expect(list.value[0].x2!).toBeCloseTo(plot.x.valOf(300), 6)
    expect(list.value[0].y!).toBeCloseTo(plot.y.valOf(80), 6)
    expect(list.value[0].y2!).toBeCloseTo(plot.y.valOf(200), 6)
  })

  it('ignores a drag smaller than the minimum on either axis and stays armed', () => {
    // A 300 x 2 px pass clears uPlot's own `dist` because `uni` promotes one axis to both, so the
    // floor has to be ours and it has to apply to both.
    const { list, armed, controller } = setup()
    armed.value = 'box'
    expect(controller.placeBoxFrom({ left: 10, top: 10, width: 300, height: MIN_BOX_DRAG_PX - 1 })).toBe(false)
    expect(controller.placeBoxFrom({ left: 10, top: 10, width: MIN_BOX_DRAG_PX - 1, height: 300 })).toBe(false)
    expect(list.value).toEqual([])
    expect(armed.value).toBe('box')
  })

  it('puts the label above the region and centred on it', () => {
    const { list, controller } = setup()
    controller.placeBoxFrom({ left: 100, top: 180, width: 200, height: 120 })
    const a = list.value[0]
    // Half the region's width in octaves, so the label sits over its middle; up, so it clears it.
    expect(a.labelOffsetX).toBeCloseTo(Math.log2(a.x2! / a.x) / 2, 6)
    expect(a.labelOffsetY).toBeGreaterThan(0)
  })
})

describe('onClick', () => {
  it('does nothing at all while the chart is not armed', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.notePress(clickAt(350, 120))
    controller.onClick(clickAt(350, 120))
    expect(list.value).toEqual([])
  })

  it('places on a still click while armed for a point', () => {
    const { list, armed, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    armed.value = 'point'
    controller.notePress(clickAt(350, 120))
    controller.onClick(clickAt(350, 120))
    expect(list.value).toHaveLength(1)
  })

  it('ignores a click that ended a drag, so a zoom while armed does not also drop a label', () => {
    const { list, armed, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    armed.value = 'point'
    controller.notePress(clickAt(100, 120))
    controller.onClick(clickAt(350, 120))
    expect(list.value).toEqual([])
  })

  it('ignores the second click of a double-click, which is the zoom reset', () => {
    const { list, armed, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    armed.value = 'point'
    controller.notePress(clickAt(350, 120))
    controller.onClick(clickAt(350, 120, 2))
    expect(list.value).toEqual([])
  })

  it('places nothing on a still press while armed for a box, which is drawn by dragging', () => {
    const { list, armed, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    armed.value = 'box'
    controller.notePress(clickAt(350, 120))
    controller.onClick(clickAt(350, 120))
    expect(list.value).toEqual([])
  })

  it('marks a box gesture in flight from the press, and only while armed for one', () => {
    const { armed, controller } = setup()
    controller.notePress(clickAt(350, 120))
    expect(controller.inBoxGesture()).toBe(false)
    armed.value = 'box'
    controller.notePress(clickAt(350, 120))
    expect(controller.inBoxGesture()).toBe(true)
    controller.endBoxGesture()
    expect(controller.inBoxGesture()).toBe(false)
  })
})

describe('onWindowKeyDown', () => {
  it('disarms the chart on Escape', () => {
    const { armed, controller } = setup()
    armed.value = 'box'
    controller.onWindowKeyDown({ key: 'Escape' } as KeyboardEvent)
    expect(armed.value).toBeNull()
  })

  it('leaves the arming alone while a text box is open, so Escape does not fight the editor', () => {
    const { armed, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    armed.value = 'box'
    controller.onWindowKeyDown({ key: 'Escape' } as KeyboardEvent)
    expect(armed.value).toBe('box')
  })

  it('ignores every other key', () => {
    const { armed, controller } = setup()
    armed.value = 'point'
    controller.onWindowKeyDown({ key: 'a' } as KeyboardEvent)
    expect(armed.value).toBe('point')
  })
})

describe('dragging a label', () => {
  function placed() {
    const s = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    s.controller.placeAt({ x: 350, y: 120 }, 'point')
    s.controller.commitEdit()
    return s
  }

  it('writes an offset that follows the pointer', () => {
    const { list, controller } = placed()
    const before = list.value[0].labelOffsetX
    const handle = controller.labelHandles.value[0]
    controller.onHandleDown(handle.id, pointerAt(handle.style.left ? 0 : 0, 0))
    // Grab at the label's own centre, then move well to the right of it.
    const plot = plotOf()
    const centreX = controller.placements.value[0].label.x + controller.placements.value[0].label.w / 2
    const centreY = controller.placements.value[0].label.y + controller.placements.value[0].label.h / 2
    controller.onHandleDown(handle.id, pointerAt(centreX, centreY))
    controller.onHandleMove(pointerAt(centreX + 100, centreY))
    expect(list.value[0].labelOffsetX).toBeGreaterThan(before)
    expect(list.value[0].labelOffsetX).toBeCloseTo(
      Math.log2(plot.x.valOf(plot.x.posOf(1000 * Math.pow(2, before)) + 100) / 1000),
      3,
    )
  })

  it('clamps the offset so the label stays on the plot', () => {
    const { list, controller } = placed()
    const handle = controller.labelHandles.value[0]
    const p = controller.placements.value[0]
    controller.onHandleDown(handle.id, pointerAt(p.label.x + p.label.w / 2, p.label.y + p.label.h / 2))
    controller.onHandleMove(pointerAt(5000, -5000))
    const a = list.value[0]
    expect(a.x * Math.pow(2, a.labelOffsetX)).toBeLessThanOrEqual(WINDOW.hiHz)
    expect(a.y! + a.labelOffsetY).toBeLessThanOrEqual(WINDOW.topDb)
  })

  it('does nothing once the drag has been let go', () => {
    const { list, controller } = placed()
    const handle = controller.labelHandles.value[0]
    const p = controller.placements.value[0]
    controller.onHandleDown(handle.id, pointerAt(p.label.x, p.label.y))
    controller.onHandleUp()
    const before = { ...list.value[0] }
    controller.onHandleMove(pointerAt(600, 50))
    expect(list.value[0]).toEqual(before)
  })

  it('offers a handle for a point and a box, and none for a rule, whose label is stacked', () => {
    const { controller } = placed()
    expect(controller.labelHandles.value).toHaveLength(1)

    const rule = setup({ readAt: () => ({ x: 1000, y: null, channel: null }) })
    rule.controller.placeAt({ x: 350, y: 120 }, 'frequency')
    expect(rule.controller.labelHandles.value).toHaveLength(0)
  })
})

describe('moving and resizing a box', () => {
  function boxed() {
    const s = setup()
    s.controller.placeBoxFrom({ left: 100, top: 80, width: 200, height: 120 })
    s.controller.commitEdit()
    return s
  }

  it('offers nine grips on a region big enough to carry them', () => {
    const { controller } = boxed()
    expect(new Set(controller.boxGrips.value.map((g) => g.kind))).toEqual(
      new Set(['nw', 'ne', 'se', 'sw', 'n', 's', 'e', 'w', 'move']),
    )
  })

  it('resizes the one edge its grip names', () => {
    const { list, controller } = boxed()
    const before = { ...list.value[0] }
    const plot = plotOf()
    controller.onBoxGripDown(1, 'e', pointerAt(300, 140))
    controller.onBoxGripMove(pointerAt(400, 140))
    expect(list.value[0].x).toBeCloseTo(before.x, 6)
    expect(list.value[0].x2!).toBeCloseTo(plot.x.valOf(400), 4)
    expect(list.value[0].y).toBeCloseTo(before.y!, 6)
  })

  it('resizes from the box the gesture started on, so an edge crossing its opposite keeps tracking', () => {
    const { list, controller } = boxed()
    const plot = plotOf()
    controller.onBoxGripDown(1, 'w', pointerAt(100, 140))
    // Drag the left edge well past the right one: it crosses, and normalising on write must not make
    // the next move fight its own result.
    controller.onBoxGripMove(pointerAt(500, 140))
    controller.onBoxGripMove(pointerAt(450, 140))
    expect(list.value[0].x2!).toBeCloseTo(plot.x.valOf(450), 3)
  })

  it('moves the whole region, keeping its extent in the axes’ own spaces', () => {
    const { list, controller } = boxed()
    const before = list.value[0]
    const widthOctaves = Math.log2(before.x2! / before.x)
    const heightDb = before.y! - before.y2!

    controller.onBoxGripDown(1, 'move', pointerAt(110, 90))
    controller.onBoxGripMove(pointerAt(210, 150))

    const after = list.value[0]
    expect(Math.log2(after.x2! / after.x)).toBeCloseTo(widthOctaves, 6)
    expect(after.y! - after.y2!).toBeCloseTo(heightDb, 6)
    expect(after.x).toBeGreaterThan(before.x)
    expect(after.y!).toBeLessThan(before.y!)
  })

  it('does not jump the region under the cursor on the first move', () => {
    const { list, controller } = boxed()
    const before = { ...list.value[0] }
    controller.onBoxGripDown(1, 'move', pointerAt(150, 120))
    controller.onBoxGripMove(pointerAt(150, 120))
    expect(list.value[0].x).toBeCloseTo(before.x, 4)
    expect(list.value[0].y!).toBeCloseTo(before.y!, 4)
  })

  it('does nothing once the grip has been let go', () => {
    const { list, controller } = boxed()
    controller.onBoxGripDown(1, 'e', pointerAt(300, 140))
    controller.onBoxGripUp()
    const before = { ...list.value[0] }
    controller.onBoxGripMove(pointerAt(500, 140))
    expect(list.value[0]).toEqual(before)
  })
})

describe('the inline editor', () => {
  it('commits the text that was typed', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    controller.editingText.value = '  Mains hum  '
    controller.commitEdit()
    expect(list.value[0].text).toBe('Mains hum')
    expect(controller.editing.value).toBeNull()
  })

  it('removes a marker that was only just placed and still says nothing', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    controller.cancelEdit()
    expect(list.value).toEqual([])
  })

  it('keeps a region with no note, which took a deliberate drag to make', () => {
    const { list, controller } = setup()
    controller.placeBoxFrom({ left: 100, top: 80, width: 200, height: 120 })
    controller.cancelEdit()
    expect(list.value).toHaveLength(1)
  })

  it('keeps a marker that already had a note, and leaves that note as it was', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    controller.editingText.value = 'kept'
    controller.commitEdit()
    controller.beginEdit(1)
    controller.editingText.value = 'discarded'
    controller.cancelEdit()
    expect(list.value[0].text).toBe('kept')
  })

  it('commits on blur, which is what a text box on a chart has to mean', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    controller.editingText.value = 'typed'
    controller.onEditorBlur()
    expect(list.value[0].text).toBe('typed')
  })

  it('does not commit on the blur that Escape itself caused', () => {
    const { list, controller } = setup({ readAt: () => ({ x: 1000, y: -60, channel: 0 }) })
    controller.placeAt({ x: 350, y: 120 }, 'point')
    controller.editingText.value = 'kept'
    controller.commitEdit()
    controller.beginEdit(1)
    controller.editingText.value = 'discarded'
    controller.cancelEdit()
    controller.onEditorBlur()
    expect(list.value[0].text).toBe('kept')
  })
})

describe('the placements', () => {
  it('draws only its own view’s annotations, numbered from one', () => {
    const { list, controller } = setup()
    list.value = [
      { ...annotation(1, 1000), view: 'Nyquist' },
      annotation(2, 5000),
      annotation(3, 500),
    ]
    expect(controller.placements.value.map((p) => [p.number, p.id])).toEqual([
      [1, 3],
      [2, 2],
    ])
  })

  it('answers nothing at all when the chart has no plot yet', () => {
    const list = ref<ChartAnnotation[]>([annotation(1, 1000)])
    const controller = useAnnotationOverlay({
      view: () => VIEW,
      list,
      armed: ref<AnnotationStyle | null>(null),
      numbers: () => true,
      cssPlot: () => null,
      surface: () => null,
      overlayRect: () => null,
      redraw: () => {},
    })
    expect(controller.placements.value).toEqual([])
    expect(controller.overlayStyle.value.display).toBe('none')
  })

  it('redraws the chart when the arming changes, so the crosshair and the labels keep up', async () => {
    const { armed, redraws } = setup()
    const before = redraws()
    armed.value = 'box'
    await nextTick()
    expect(redraws()).toBeGreaterThan(before)
  })

  it('hands the chart its own arming hook, which is how a uPlot drag becomes the region gesture', async () => {
    // Not optional for a uPlot chart: without it, arming for a Box leaves `drag.setScale` on and the
    // drag zooms instead of drawing -- a silent failure, which is why the hook is on the composable
    // rather than in each chart's own watcher.
    const list = ref<ChartAnnotation[]>([])
    const armed = ref<AnnotationStyle | null>(null)
    let armedChanges = 0
    useAnnotationOverlay({
      view: () => VIEW,
      list,
      armed,
      numbers: () => true,
      cssPlot: () => plotOf(),
      surface: fakeSurface,
      overlayRect: () => ({ left: 0, top: 0, width: WIDTH, height: HEIGHT }),
      redraw: () => {},
      onArmedChange: () => {
        armedChanges++
      },
    })
    armed.value = 'box'
    await nextTick()
    expect(armedChanges).toBe(1)
    armed.value = null
    await nextTick()
    expect(armedChanges).toBe(2)
  })
})

function annotation(id: number, x: number): ChartAnnotation {
  return {
    id,
    style: 'point',
    text: `note ${id}`,
    view: VIEW,
    x,
    y: -60,
    x2: null,
    y2: null,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: 0,
    labelOffsetX: 0.45,
    labelOffsetY: 14,
  }
}

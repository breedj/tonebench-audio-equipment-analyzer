/**
 * The whole annotation apparatus for one chart: placing by clicking or dragging, the drag handles
 * and resize grips, the inline text editor, and the painting.
 *
 * <p>This lived inside SpectrumChart.vue while annotations were an FFT feature. It is a composable
 * now because twelve charts need it, and twelve copies of a gesture that mutates uPlot's live cursor
 * would be twelve chances to get one of its documented traps wrong. A chart supplies two
 * {@link AnnotationPlot} adapters — one in its canvas's pixels, one in the CSS pixels a mouse works
 * in — and gets back everything else.</p>
 *
 * <p>Deliberately free of uPlot: charts/uplotAnnotations.ts builds the adapters and the draw-hook
 * plugin for the nine uPlot charts, and the three hand-drawn plane charts call {@link paint} from
 * their own `renderScene` and feed the gesture from their own pointer handlers. That split is what
 * lets the plane charts annotate at all — they have no plugin pipeline, no `valToPos` and no
 * monotonic x.</p>
 *
 * <p>It is also what finally makes any of this testable. Click placement, box placement from a swept
 * rectangle, the label-drag clamp, the grip resize, the gesture's minimum size and the
 * Escape-removes-an-unnamed-marker rule were all inside a `.vue` and pinned by nothing.</p>
 */

import { computed, nextTick, ref, watch, type ComputedRef, type Ref } from 'vue'
import {
  DEFAULT_LABEL_OFFSET_FRACTION,
  addAnnotation,
  annotationReading,
  nextAnnotationId,
  numberedAnnotations,
  removeAnnotation,
  updateAnnotation,
  type AnnotationStyle,
  type ChartAnnotation,
} from '../measurements/chartAnnotations'
import {
  annotationMetrics,
  boxFromDrag,
  boxHandles,
  boxSpaces,
  clampBoxToWindow,
  clampLabelOffset,
  defaultLabelOffset,
  layoutAnnotations,
  type AnnotationLayoutItem,
  type AnnotationPlacement,
  type AnnotationPlot,
  type AnnotationWindow,
  type BoxHandleKind,
  type DataBox,
} from './annotationLayout'
import { paintAnnotations, type AnnotationTheme } from './annotationPainter'

/**
 * Where the button went down, so a drag can be told from a click: a drag-zoom ends with a `click` on
 * the same element, and without this every zoom while armed would also drop a label. The threshold is
 * in CSS pixels and deliberately small — a hand is never perfectly still on a press, and anything
 * larger would swallow a very short drag. Same figure and reasoning as FrequencyResponseChart's,
 * which pins its cursor the same way.
 */
const CLICK_SLOP_PX = 4

/**
 * How big a drag has to be, in CSS pixels, to mean a region — on <b>both</b> axes.
 *
 * <p>It cannot be uPlot's own `drag.dist`: with `uni == null` uPlot promotes a pass on one axis to
 * both, so a 300 x 2 px drag clears `dist` and yields a hairline box. Twelve is the figure `uni`
 * already uses on the two-axis charts here. Under it nothing is placed and <b>the chart stays
 * armed</b> — a slip of the hand should not cost a trip back to the panel.</p>
 */
export const MIN_BOX_DRAG_PX = 12

/**
 * How small a box may be dragged, in CSS pixels, before the floor in clampBoxToWindow stops it.
 *
 * <p>A <i>UI-integrity</i> floor rather than a measurement one: at about a grip's width the nine
 * grips can no longer collapse into each other and strand the region. Deliberately a different
 * number, and a different reason, from {@link MIN_BOX_DRAG_PX}, which is how big a *gesture* has to
 * be to mean a region at all.</p>
 */
const MIN_BOX_SPAN_PX = 10

const GRIP_CURSOR: Record<BoxHandleKind, string> = {
  move: 'cursor-move',
  n: 'cursor-ns-resize',
  s: 'cursor-ns-resize',
  e: 'cursor-ew-resize',
  w: 'cursor-ew-resize',
  nw: 'cursor-nwse-resize',
  se: 'cursor-nwse-resize',
  ne: 'cursor-nesw-resize',
  sw: 'cursor-nesw-resize',
}

/** A point on a trace, as the chart itself reads it — the figure its cursor readout would show. */
export interface AnnotationReadAt {
  x: number
  y: number | null
  channel: number | null
}

export interface AnnotationOverlayOptions {
  /** Which chart of the measurement this is, so only its own annotations are drawn and numbered. */
  view: () => string
  /** The measurement's whole list, across every view. Replaced, never mutated. */
  list: Ref<ChartAnnotation[]>
  /** The style the panel has armed, or null. Cleared by this composable once something is placed. */
  armed: Ref<AnnotationStyle | null>
  numbers: () => boolean
  /**
   * The CSS-pixel adapter, whose coordinate origin is {@link surface}'s own top-left — so a pointer
   * position is `clientX - surfaceRect.left` with nothing further to add.
   */
  cssPlot: () => AnnotationPlot | null
  /** The element pointer coordinates are measured against: uPlot's `over`, or a chart's canvas. */
  surface: () => HTMLElement | null
  /** Where the overlay div goes, in CSS pixels relative to the chart's positioned container. */
  overlayRect: () => { left: number; top: number; width: number; height: number } | null
  /** Repaint the chart. Must be a *redraw*, never a rebuild — see the uplotAnnotations remarks. */
  redraw: () => void
  /**
   * Called whenever the armed style changes, before the redraw.
   *
   * <p>Every uPlot chart passes `syncBoxDragMode` here, and it is not optional for one of those: it
   * is what turns uPlot's own drag-selection into the region gesture and back. It lives on the
   * composable rather than in each chart's own watcher because a chart that forgot it would arm
   * perfectly and then zoom instead of drawing, which is a silent failure. The plane charts, whose
   * rubber band is their own, pass nothing.</p>
   */
  onArmedChange?: () => void
  /**
   * What a click should record, where the chart can do better than the raw pointer position: the
   * peak it was obviously aimed at, and the level of the trace there. Falling back to the pointer is
   * always correct, so this is optional — and it is what the spectrogram, which runs with no cursor
   * at all, relies on.
   */
  readAt?: (pos: { x: number; y: number }) => AnnotationReadAt | null
}

export interface AnnotationGrip {
  key: string
  id: number
  kind: BoxHandleKind
  cursor: string
  title: string
  style: Record<string, string>
}

export interface AnnotationHandle {
  id: number
  title: string
  style: Record<string, string>
}

export interface AnnotationController {
  placements: ComputedRef<AnnotationPlacement[]>
  overlayStyle: ComputedRef<Record<string, string>>
  labelHandles: ComputedRef<AnnotationHandle[]>
  boxGrips: ComputedRef<AnnotationGrip[]>
  editorStyle: ComputedRef<Record<string, string>>
  editorRef: Ref<HTMLInputElement | null>
  editingText: Ref<string>
  editing: Ref<number | null>
  armed: Ref<AnnotationStyle | null>
  armedForBox: ComputedRef<boolean>
  geometryTick: Ref<number>
  noteGeometry: (key: string) => void
  paint: (ctx: CanvasRenderingContext2D, plot: AnnotationPlot, theme: AnnotationTheme) => void
  placeAt: (pos: { x: number; y: number }, style: AnnotationStyle) => boolean
  placeBoxFrom: (rect: { left: number; top: number; width: number; height: number }) => boolean
  notePress: (event: MouseEvent) => void
  onClick: (event: MouseEvent) => void
  endBoxGesture: () => void
  inBoxGesture: () => boolean
  onWindowKeyDown: (event: KeyboardEvent) => void
  onHandleDown: (id: number, event: PointerEvent) => void
  onHandleMove: (event: PointerEvent) => void
  onHandleUp: () => void
  onBoxGripDown: (id: number, kind: BoxHandleKind, event: PointerEvent) => void
  onBoxGripMove: (event: PointerEvent) => void
  onBoxGripUp: () => void
  beginEdit: (id: number) => void
  commitEdit: () => void
  cancelEdit: () => void
  onEditorBlur: () => void
}

export function useAnnotationOverlay(options: AnnotationOverlayOptions): AnnotationController {
  const { list, armed, redraw } = options
  const editing = ref<number | null>(null)
  const editingText = ref('')
  const editorRef = ref<HTMLInputElement | null>(null)
  const geometryTick = ref(0)
  let lastGeometry = ''
  let pressOrigin: { x: number; y: number } | null = null
  let boxGesture = false

  const armedForBox = computed(() => armed.value === 'box')

  /** This view's annotations, as the layout wants them: stored values and their quantities. */
  function items(): AnnotationLayoutItem[] {
    return numberedAnnotations(list.value, options.view()).map((a) => ({
      id: a.id,
      number: a.number,
      style: a.style,
      text: a.text,
      x: a.x,
      y: a.y,
      x2: a.x2,
      y2: a.y2,
      xQuantity: a.xQuantity,
      yQuantity: a.yQuantity,
      labelOffsetX: a.labelOffsetX,
      labelOffsetY: a.labelOffsetY,
    }))
  }

  /**
   * Bumped whenever the plot's geometry or its window changes, so the DOM handles follow a zoom, a
   * pan and a resize. Called from whatever runs on every commit — the draw hook, or a canvas chart's
   * render — and gated on an actual change, so a streaming tick does not re-render the handles twenty
   * times a second. Writing a counter from a draw and reading it in a render schedules a re-render,
   * not another draw, so there is no loop here however much it looks like one.
   */
  function noteGeometry(key: string) {
    if (key === lastGeometry) return
    lastGeometry = key
    geometryTick.value++
  }

  function paint(ctx: CanvasRenderingContext2D, plot: AnnotationPlot, theme: AnnotationTheme) {
    const list = items()
    if (list.length === 0) return
    const placements = layoutAnnotations(list, plot, options.numbers())
    paintAnnotations(ctx, placements, theme, plot.scale, plot)
  }

  /**
   * Every annotation's geometry in CSS pixels, from the same layout the canvas drew with — which is
   * what makes a handle land exactly on its label. Re-runs when the list changes and when the plot's
   * geometry or window does (see {@link noteGeometry}).
   */
  const placements = computed<AnnotationPlacement[]>(() => {
    void geometryTick.value
    const plot = options.cssPlot()
    if (!plot) return []
    const laid = items()
    if (laid.length === 0) return []
    return layoutAnnotations(laid, plot, options.numbers())
  })

  const overlayStyle = computed<Record<string, string>>(() => {
    void geometryTick.value
    const rect = options.overlayRect()
    return {
      display: rect ? 'block' : 'none',
      left: `${rect?.left ?? 0}px`,
      top: `${rect?.top ?? 0}px`,
      width: `${rect?.width ?? 0}px`,
      height: `${rect?.height ?? 0}px`,
    }
  })

  /**
   * One transparent handle per *point* and *box* label. A rule's label is stacked at the top of the
   * plot out of its neighbours' way, which a manual nudge would fight, so it has nothing to drag.
   */
  const labelHandles = computed<AnnotationHandle[]>(() =>
    placements.value
      // A box's label drags exactly like a point's, because a box's stored anchor is its top-left
      // corner -- onHandleMove's maths needs no branch.
      .filter((p) => p.visible && (p.style === 'point' || p.style === 'box'))
      .map((p) => {
        const annotation = list.value.find((a) => a.id === p.id)
        return {
          id: p.id,
          title: annotation
            ? `${annotation.text || 'Annotation'} — ${annotationReading(annotation)}. Drag to move the label, double-click to edit.`
            : '',
          style: {
            left: `${p.label.x}px`,
            top: `${p.label.y}px`,
            width: `${p.label.w}px`,
            height: `${p.label.h}px`,
          },
        }
      }),
  )

  /**
   * The move and resize grips of every drawn box, in the same CSS pixels the label handles use.
   *
   * <p>Move is a <b>grip</b> and not the box's interior, which is the one thing to understand here: a
   * region can cover the plot, and its interior is where drag-zoom lives — marking a region and then
   * zooming into it is the obvious next thing a user does. The same judgement keeps the label's own
   * handle small. The cost is that a drag-zoom begun exactly on a box's border resizes it instead;
   * that is a few pixels per edge and the cursor says so on hover.</p>
   */
  const boxGrips = computed<AnnotationGrip[]>(() => {
    void geometryTick.value
    const plot = options.cssPlot()
    if (!plot) return []
    const m = annotationMetrics(plot)
    return placements.value
      .filter((p) => p.visible && p.box)
      .flatMap((p) =>
        boxHandles(p.box as { x: number; y: number; w: number; h: number }, m).map((handle) => ({
          key: `${p.id}:${handle.kind}`,
          id: p.id,
          kind: handle.kind,
          cursor: GRIP_CURSOR[handle.kind],
          title: handle.kind === 'move' ? 'Drag to move this region.' : 'Drag to resize this region.',
          style: {
            left: `${handle.rect.x}px`,
            top: `${handle.rect.y}px`,
            width: `${handle.rect.w}px`,
            height: `${handle.rect.h}px`,
          },
        })),
      )
  })

  // --- placing ------------------------------------------------------------------------------

  function windowOf(plot: AnnotationPlot): AnnotationWindow {
    return {
      xLo: plot.x.window.lo,
      xHi: plot.x.window.hi,
      yLo: plot.y.window.lo,
      yHi: plot.y.window.hi,
    }
  }

  /** The size floor in the spaces clampBoxToWindow works in, for the window now on screen. */
  function minBoxSpan(plot: AnnotationPlot): { x: number; y: number } {
    const spaces = boxSpaces(plot.x.quantity, plot.y.quantity)
    const xSpan = spaces.x.toSpace(plot.x.window.hi) - spaces.x.toSpace(plot.x.window.lo)
    const ySpan = spaces.y.toSpace(plot.y.window.hi) - spaces.y.toSpace(plot.y.window.lo)
    return {
      x: plot.width > 0 && Number.isFinite(xSpan) ? (MIN_BOX_SPAN_PX / plot.width) * xSpan : 0,
      y: plot.height > 0 && Number.isFinite(ySpan) ? (MIN_BOX_SPAN_PX / plot.height) * ySpan : 0,
    }
  }

  function store(draft: Omit<ChartAnnotation, 'id'>): boolean {
    const id = nextAnnotationId(list.value)
    const next = addAnnotation(list.value, draft)
    // The measurement is full -- addAnnotation hands the list straight back. Say nothing here: the
    // panel's Add button is already disabled with the reason on it.
    if (next.length === list.value.length) return false
    list.value = next
    armed.value = null
    editing.value = id
    return true
  }

  /**
   * Places an annotation at a pointer position: on whatever the chart's own {@link readAt} says is
   * there — the peak a click was obviously aimed at, and the level of the trace under it — or simply
   * where the pointer landed, so a note can be dropped on an empty chart or beside a hidden channel.
   *
   * <p>Returns false and leaves the chart <b>armed</b> where the axes cannot say what was clicked:
   * the spectrum under a density unit with no ENBW to convert by has no honest dBV to record, and
   * storing a wrong level would be worse than making the user click again.</p>
   */
  function placeAt(pos: { x: number; y: number }, style: AnnotationStyle): boolean {
    const plot = options.cssPlot()
    if (!plot) return false

    const read = options.readAt?.(pos) ?? null
    const x = read?.x ?? plot.x.valOf(pos.x)
    if (!Number.isFinite(x)) return false

    let y: number | null = null
    let channel: number | null = null
    if (style !== 'frequency') {
      y = read && read.y !== null ? read.y : plot.y.valOf(pos.y)
      if (y === null || !Number.isFinite(y)) return false
      channel = read?.channel ?? null
    }

    const offset =
      style === 'frequency'
        ? { labelOffsetX: 0, labelOffsetY: 0 }
        : defaultLabelOffset(plot, DEFAULT_LABEL_OFFSET_FRACTION)

    return store({
      style,
      text: '',
      view: options.view(),
      x,
      y,
      x2: null,
      y2: null,
      xQuantity: plot.x.quantity,
      yQuantity: plot.y.quantity,
      channel,
      ...offset,
    })
  }

  /**
   * The region a drag just swept, as an annotation. The rectangle is in the CSS pixels of
   * {@link AnnotationOverlayOptions.cssPlot} — the same space the label handles work in.
   */
  function placeBoxFrom(rect: { left: number; top: number; width: number; height: number }): boolean {
    const plot = options.cssPlot()
    if (!plot) return false
    if (rect.width < MIN_BOX_DRAG_PX || rect.height < MIN_BOX_DRAG_PX) return false

    // No snapping, deliberately, where a point and a rule both snap: a region's edges are where the
    // hand put them, and jumping one to the nearest bin could move it by an arbitrary amount.
    const lo = plot.x.valOf(rect.left)
    const hi = plot.x.valOf(rect.left + rect.width)
    const top = plot.y.valOf(rect.top)
    const bottom = plot.y.valOf(rect.top + rect.height)
    if (![lo, hi, top, bottom].every(Number.isFinite)) return false

    const spaces = boxSpaces(plot.x.quantity, plot.y.quantity)
    const width = spaces.x.toSpace(Math.max(lo, hi)) - spaces.x.toSpace(Math.min(lo, hi))
    const height = spaces.y.toSpace(Math.max(top, bottom)) - spaces.y.toSpace(Math.min(top, bottom))

    return store({
      style: 'box',
      text: '',
      view: options.view(),
      x: lo,
      y: top,
      x2: hi,
      y2: bottom,
      xQuantity: plot.x.quantity,
      yQuantity: plot.y.quantity,
      channel: null,
      // Above the region and centred on it, which works precisely because the stored anchor is the
      // box's top-left corner.
      labelOffsetX: Number.isFinite(width) ? width / 2 : 0,
      labelOffsetY: Number.isFinite(height) ? height * 0.35 : 0,
    })
  }

  // --- the click and the box gesture ---------------------------------------------------------

  function notePress(event: MouseEvent) {
    pressOrigin = { x: event.clientX, y: event.clientY }
    if (armed.value === 'box') boxGesture = true
  }

  function pointerIn(event: MouseEvent): { x: number; y: number } | null {
    const surface = options.surface()
    if (!surface) return null
    const rect = surface.getBoundingClientRect()
    return { x: event.clientX - rect.left, y: event.clientY - rect.top }
  }

  /**
   * A click places an annotation — but only while a panel has armed the chart. Unarmed, this returns
   * at once and the plot behaves exactly as it always has: the cursor drag still zooms and the
   * double-click still resets.
   */
  function onClick(event: MouseEvent) {
    const origin = pressOrigin
    pressOrigin = null

    const style = armed.value
    if (style === null) return
    // A box is drawn by dragging, and finished by endBoxGesture. A still press would otherwise reach
    // placeAt and store a region of no size.
    if (style === 'box') return
    // A double-click fires two `click` events, so without this the reset gesture would place two
    // labels on top of one another while armed.
    if (event.detail !== 1) return
    if (
      origin &&
      (Math.abs(event.clientX - origin.x) > CLICK_SLOP_PX || Math.abs(event.clientY - origin.y) > CLICK_SLOP_PX)
    ) {
      return
    }
    const pos = pointerIn(event)
    if (pos) placeAt(pos, style)
  }

  function inBoxGesture(): boolean {
    return boxGesture
  }

  /** Clears the in-flight flag; the caller places the region, because only it knows the rectangle. */
  function endBoxGesture() {
    boxGesture = false
  }

  function onWindowKeyDown(event: KeyboardEvent) {
    if (event.key !== 'Escape' || armed.value === null || editing.value !== null) return
    armed.value = null
  }

  // --- dragging a label ----------------------------------------------------------------------

  let dragging: { id: number; grabDx: number; grabDy: number } | null = null

  function onHandleDown(id: number, event: PointerEvent) {
    const placement = placements.value.find((p) => p.id === id)
    const pos = pointerIn(event)
    if (!placement || !pos) return
    dragging = {
      id,
      grabDx: pos.x - (placement.label.x + placement.label.w / 2),
      grabDy: pos.y - (placement.label.y + placement.label.h / 2),
    }
    ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
    event.preventDefault()
  }

  function onHandleMove(event: PointerEvent) {
    const drag = dragging
    const plot = options.cssPlot()
    if (!drag || !plot) return
    const annotation = list.value.find((a) => a.id === drag.id)
    const placement = placements.value.find((p) => p.id === drag.id)
    const pos = pointerIn(event)
    if (!annotation || !placement || !pos || annotation.y === null) return

    const targetX = plot.x.valOf(pos.x - drag.grabDx)
    const targetY = plot.y.valOf(pos.y - drag.grabDy)
    if (!Number.isFinite(targetX) || !Number.isFinite(targetY)) return

    const spaces = boxSpaces(annotation.xQuantity, annotation.yQuantity)
    const offsetX = spaces.x.toSpace(targetX) - spaces.x.toSpace(annotation.x)
    const offsetY = spaces.y.toSpace(targetY) - spaces.y.toSpace(annotation.y)
    if (!Number.isFinite(offsetX) || !Number.isFinite(offsetY)) return

    const clamped = clampLabelOffset({
      x: annotation.x,
      y: annotation.y,
      offsetX,
      offsetY,
      window: windowOf(plot),
      spaces,
      label: placement.label,
      plot: { width: plot.width, height: plot.height },
    })
    list.value = updateAnnotation(list.value, drag.id, {
      labelOffsetX: clamped.offsetX,
      labelOffsetY: clamped.offsetY,
    })
  }

  function onHandleUp() {
    dragging = null
  }

  // --- moving and resizing a box -------------------------------------------------------------

  let boxDrag: { id: number; kind: BoxHandleKind; grabX: number; grabY: number; start: DataBox } | null = null

  /** The box an annotation describes, or null when it is not one. */
  function storedBox(id: number): DataBox | null {
    const a = list.value.find((x) => x.id === id)
    if (!a || a.style !== 'box') return null
    if (a.x2 === null || a.y === null || a.y2 === null) return null
    return { x: a.x, x2: a.x2, y: a.y, y2: a.y2 }
  }

  function onBoxGripDown(id: number, kind: BoxHandleKind, event: PointerEvent) {
    const plot = options.cssPlot()
    const start = storedBox(id)
    const pos = pointerIn(event)
    const annotation = list.value.find((a) => a.id === id)
    if (!plot || !start || !pos || !annotation) return

    const pointerX = plot.x.valOf(pos.x)
    const pointerY = plot.y.valOf(pos.y)
    if (!Number.isFinite(pointerX) || !Number.isFinite(pointerY)) return

    // Where the pointer sits relative to the thing it grabbed, in the axes' own spaces, so the edge
    // (or the corner, or the whole region for a move) does not jump under the cursor on the first
    // move.
    const spaces = boxSpaces(annotation.xQuantity, annotation.yQuantity)
    const anchorX = kind === 'move' || kind.includes('w') ? start.x : kind.includes('e') ? start.x2 : pointerX
    const anchorY = kind === 'move' || kind.includes('n') ? start.y : kind.includes('s') ? start.y2 : pointerY
    boxDrag = {
      id,
      kind,
      grabX: spaces.x.toSpace(pointerX) - spaces.x.toSpace(anchorX),
      grabY: spaces.y.toSpace(pointerY) - spaces.y.toSpace(anchorY),
      start,
    }
    ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
    event.preventDefault()
  }

  function onBoxGripMove(event: PointerEvent) {
    const drag = boxDrag
    const plot = options.cssPlot()
    const pos = pointerIn(event)
    if (!drag || !plot || !pos) return
    const annotation = list.value.find((a) => a.id === drag.id)
    if (!annotation) return

    const spaces = boxSpaces(annotation.xQuantity, annotation.yQuantity)
    const x = spaces.x.fromSpace(spaces.x.toSpace(plot.x.valOf(pos.x)) - drag.grabX)
    const y = spaces.y.fromSpace(spaces.y.toSpace(plot.y.valOf(pos.y)) - drag.grabY)
    if (!Number.isFinite(x) || !Number.isFinite(y)) return

    // Always from the box the gesture *started* on, so a resize that crosses its opposite edge keeps
    // tracking the pointer rather than fighting its own normalised result.
    const proposed = boxFromDrag(drag.kind, drag.start, { x, y }, spaces)
    const clamped = clampBoxToWindow(proposed, windowOf(plot), minBoxSpan(plot), spaces)
    list.value = updateAnnotation(list.value, drag.id, {
      x: clamped.x,
      x2: clamped.x2,
      y: clamped.y,
      y2: clamped.y2,
    })
  }

  function onBoxGripUp() {
    boxDrag = null
  }

  // --- the inline editor ---------------------------------------------------------------------

  const editorStyle = computed<Record<string, string>>(() => {
    const placement = placements.value.find((p) => p.id === editing.value && p.visible) ?? null
    return {
      display: placement ? 'block' : 'none',
      left: `${placement?.label.x ?? 0}px`,
      top: `${placement?.label.y ?? 0}px`,
      width: `${Math.max(160, placement?.label.w ?? 0)}px`,
    }
  })

  let cancelling = false

  watch(editing, async (id) => {
    editingText.value = id === null ? '' : (list.value.find((a) => a.id === id)?.text ?? '')
    if (id === null) return
    await nextTick()
    editorRef.value?.focus()
    editorRef.value?.select()
  })

  function beginEdit(id: number) {
    editing.value = id
  }

  function commitEdit() {
    const id = editing.value
    if (id === null) return
    list.value = updateAnnotation(list.value, id, { text: editingText.value.trim() })
    editing.value = null
  }

  /**
   * Escape leaves the text as it was — and removes the annotation outright when it was only just
   * placed and still says nothing, so a stray click leaves nothing behind.
   */
  function cancelEdit() {
    const id = editing.value
    if (id === null) return
    cancelling = true
    // A box is exempt: it took a deliberate drag of at least MIN_BOX_DRAG_PX to make, and a region
    // with no note still says "look here". Only a click-placed marker is thrown away unnamed.
    const existing = list.value.find((a) => a.id === id)
    if (existing && existing.style !== 'box' && existing.text.length === 0) {
      list.value = removeAnnotation(list.value, id)
    }
    editing.value = null
  }

  /** Clicking away commits, which is what a text box on a chart has to mean — unless Escape did it. */
  function onEditorBlur() {
    if (cancelling) {
      cancelling = false
      return
    }
    commitEdit()
  }

  watch(armed, () => {
    options.onArmedChange?.()
    redraw()
  })

  return {
    placements,
    overlayStyle,
    labelHandles,
    boxGrips,
    editorStyle,
    editorRef,
    editingText,
    editing,
    armed,
    armedForBox,
    geometryTick,
    noteGeometry,
    paint,
    placeAt,
    placeBoxFrom,
    notePress,
    onClick,
    endBoxGesture,
    inBoxGesture,
    onWindowKeyDown,
    onHandleDown,
    onHandleMove,
    onHandleUp,
    onBoxGripDown,
    onBoxGripMove,
    onBoxGripUp,
    beginEdit,
    commitEdit,
    cancelEdit,
    onEditorBlur,
  }
}

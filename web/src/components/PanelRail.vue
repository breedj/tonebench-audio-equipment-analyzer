<script setup lang="ts">
/**
 * One of a measurement screen's two rails, with its panels draggable into whatever order the
 * reader wants (asked for 2026-11-21). The order is remembered per measurement <em>kind</em>, so
 * opening another measurement of that kind shows the same rail — see usePanelOrder.ts.
 *
 * <p><b>It reorders by CSS `order`, and never by moving a node.</b> The rails are template markup
 * rather than a list — the settings rail is a run of per-screen components, the display rail is one
 * multi-root component whose sections are the rail's own children — so there is no array to sort
 * from outside without taking every screen apart. There is also a stronger reason not to: moving a
 * DOM node remounts everything under it, which on these rails means a chart losing its uPlot
 * instance, an open panel closing, and a half-typed number box being re-stamped. Writing
 * <code>style.order</code> changes the picture and nothing else.</p>
 *
 * <p><b>The children are read from the DOM rather than from the slot's vnodes</b>, and flattened
 * through any <code>display: contents</code> wrapper (ChartDisplayPanel and GeneratorPanel are
 * one), because what has to be ordered is the <em>flex items</em> — which is what the box tree
 * says, not what the component tree does. The same walk is what lets a panel appear and disappear
 * behind a <code>v-if</code> without any of this knowing which ones exist.</p>
 *
 * <p>The arithmetic — which slot each panel lands in, where a drop puts it, and what is stored —
 * is in panels/panelOrder.ts, because nothing in a <code>.vue</code> is testable here.</p>
 */
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { usePanelOrder } from '../composables/usePanelOrder'
import {
  panelOrderAfterDrop,
  panelOrders,
  railOrderAfterDrag,
  type PanelRailId,
} from '../panels/panelOrder'

const props = defineProps<{
  /** Which rail this is, for the stored document. */
  rail: PanelRailId
  /** The open measurement's kind — the order is remembered against it, not against the measurement. */
  kind?: string | null
}>()

const host = ref<HTMLDivElement | null>(null)
const { kinds, load, orderFor, setOrder } = usePanelOrder()
void load()

/** The id the pointer is dragging, while it is. */
let dragging: string | null = null
/** What the drop indicator is drawn on, so it can be taken off again. */
let marked: HTMLElement | null = null

/**
 * The rail's flex items, in DOM order, flattened through `display: contents`.
 *
 * <p>Read live rather than cached: a panel behind a <code>v-if</code> comes and goes, and the
 * observer below re-runs this rather than trying to keep a list in step.</p>
 */
function items(): HTMLElement[] {
  const el = host.value
  if (!el) return []
  const out: HTMLElement[] = []
  const walk = (parent: Element) => {
    for (const child of Array.from(parent.children)) {
      if (!(child instanceof HTMLElement)) continue
      // A `contents` wrapper is not a box of its own, so its children are the rail's items.
      if (getComputedStyle(child).display === 'contents') walk(child)
      else out.push(child)
    }
  }
  walk(el)
  return out
}

function idsOf(elements: readonly HTMLElement[]): (string | null)[] {
  return elements.map((el) => el.dataset.panelId ?? null)
}

/** Writes the stored order onto the rail. Cheap enough to run on every DOM change. */
function apply() {
  const elements = items()
  if (elements.length === 0) return
  const orders = panelOrders(idsOf(elements), orderFor(props.kind, props.rail))
  elements.forEach((el, index) => {
    const order = String(orders[index] ?? index)
    if (el.style.order !== order) el.style.order = order
  })
}

/** The panels as they are drawn, top first — the list a drop is computed against and stored. */
function drawnIds(): string[] {
  const elements = items()
  const orders = elements.map((el, index) => Number(el.style.order || index))
  return railOrderAfterDrag(idsOf(elements), orders)
}

function unmark() {
  marked?.classList.remove('panel-drop-before', 'panel-drop-after')
  marked = null
}

function panelUnder(event: DragEvent): HTMLElement | null {
  const target = event.target
  if (!(target instanceof Element)) return null
  const panel = target.closest('[data-panel-id]')
  return panel instanceof HTMLElement && host.value?.contains(panel) ? panel : null
}

function onDragStart(event: DragEvent) {
  const panel = panelUnder(event)
  if (!panel) return
  dragging = panel.dataset.panelId ?? null
  if (!dragging) return
  // Set so Firefox starts the drag at all; the payload is never read — the id is held here, which
  // is what keeps a drag from another window or another rail from meaning anything.
  event.dataTransfer?.setData('text/plain', dragging)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
  panel.classList.add('panel-dragging')
}

function onDragOver(event: DragEvent) {
  if (!dragging) return
  const panel = panelUnder(event)
  if (!panel || panel.dataset.panelId === dragging) {
    unmark()
    return
  }
  // Without this the drop never fires.
  event.preventDefault()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'

  // Split at the midpoint, which is the only way the last position is reachable: a panel that can
  // only ever mean "above me" leaves nothing below the final one (reorder.ts's own rule).
  const box = panel.getBoundingClientRect()
  const after = event.clientY > box.top + box.height / 2
  if (marked !== panel) unmark()
  marked = panel
  panel.classList.toggle('panel-drop-before', !after)
  panel.classList.toggle('panel-drop-after', after)
}

function onDrop(event: DragEvent) {
  if (!dragging) return
  const panel = panelUnder(event)
  const over = panel?.dataset.panelId
  unmark()
  if (!over || over === dragging) return
  event.preventDefault()

  const box = panel!.getBoundingClientRect()
  const after = event.clientY > box.top + box.height / 2
  const next = panelOrderAfterDrop(drawnIds(), dragging, over, after)
  void setOrder(props.kind, props.rail, next)
}

function onDragEnd() {
  host.value?.querySelector('.panel-dragging')?.classList.remove('panel-dragging')
  unmark()
  dragging = null
}

let observer: MutationObserver | null = null

onMounted(() => {
  apply()
  // Panels come and go behind `v-if` — a delay range that appears with a curve, a section a
  // checkbox summons — so the rail re-applies rather than assuming the list it saw at mount.
  observer = new MutationObserver(() => apply())
  if (host.value) observer.observe(host.value, { childList: true, subtree: true })
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

// The stored document arriving, or the screen changing to another kind.
watch([kinds, () => props.kind], apply)
</script>

<template>
  <div
    ref="host"
    :data-panel-rail="rail"
    @dragstart="onDragStart"
    @dragover="onDragOver"
    @drop="onDrop"
    @dragend="onDragEnd"
  >
    <slot />
  </div>
</template>

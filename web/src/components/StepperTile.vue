<script setup lang="ts">
/**
 * A knob, as a tile: <code>−</code> at one end, <code>+</code> at the other, the setting between
 * them — Joost's design of 2026-11-20.
 *
 * <p><b>Why not a dropdown.</b> These are the two controls a scope is actually driven by, and the
 * gesture is "one step coarser", repeated, while watching the trace. A `select` cannot do that at
 * all: every change is open-pick-close, with the chart hidden behind the popup for two of the
 * three. The tile puts the step under one press and keeps the whole ladder one press away — which
 * is the grid (`panels/stepperGrid.ts`), opened from the body and anchored under the tile.</p>
 *
 * <p><b>The tone is the panel's own.</b> Time/div is the Acquisition panel's orange and V/div the
 * Vertical panel's violet, so a tile reads as part of the section it sits in rather than as a
 * third thing — and the grid's own decade dots step back from that same hue.</p>
 *
 * <p><b>The widths are a budget, not a taste.</b> At the rail's 226 px the frame holds two step
 * buttons, the body and — where the caller has one — an Autoscale, and the body is the only one of
 * them that has to hold a figure: <code>1.000 ms/div</code> wants 80 px at 15 px semibold. The
 * buttons were 36 px before the Autoscale existed and are 28 now, which with the body's own padding
 * buys the 16 px it costs. Narrow one further and the value truncates, which is the one thing a
 * readout may not do.</p>
 *
 * <p><b>Two shapes, one chrome.</b> With a <code>ladder</code> the body is a button that opens the
 * grid; without one the caller puts its own control in the default slot (the Centre box, which is
 * a continuous voltage and has to stay typable) and only the two buttons are ours. Either way the
 * caller owns the value: this emits <code>step</code> and <code>pick</code> and never writes.</p>
 */
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { buildStepperGrid, ladderNeighbours, stepLadder } from '../panels/stepperGrid'
import type { MeasurementKindTone } from '../measurements/kinds'

const props = withDefaults(
  defineProps<{
    /** The caption inside the tile — `Time/div`, `Centre (V)`. */
    label: string
    /** The panel's own hue. */
    tone: MeasurementKindTone
    /** What the tile reads now. Ignored when the default slot is filled. */
    display?: string
    /** Drawn small after the value, for the controls that carry one. */
    suffix?: string
    /** The rungs. Absent means a plain stepper with no grid. */
    ladder?: readonly number[]
    /** Where the control stands, for the step buttons and the grid's own highlight. */
    value?: number
    /** How the grid and the hint write a rung. Required with a ladder. */
    format?: (value: number) => string
    title?: string
    /**
     * What an <b>Autoscale</b> press would do, in a sentence. Its presence is what puts the
     * button on the tile at all (asked for 2026-11-21).
     *
     * <p>On the tile rather than in the panel, because what it scales is <em>this</em> control:
     * a button in the section header would read as "scale the section", and the Vertical
     * section holds two tiles of which only one has an automatic answer. After the
     * <code>+</code> rather than inside the body, which is forced — with a ladder the body is
     * itself a button, and a button inside a button is not markup.</p>
     */
    autoTitle?: string
  }>(),
  {
    display: '',
    suffix: '',
    ladder: undefined,
    value: Number.NaN,
    format: undefined,
    title: undefined,
    autoTitle: undefined,
  },
)

const emit = defineEmits<{ step: [direction: 1 | -1]; pick: [value: number]; auto: [] }>()

const open = ref(false)
const tile = ref<HTMLElement | null>(null)
const popover = ref<HTMLElement | null>(null)

const hasGrid = computed(() => props.ladder !== undefined && props.format !== undefined)

const grid = computed(() =>
  hasGrid.value ? buildStepperGrid(props.ladder!, props.format!, props.tone) : null,
)

/**
 * The two rungs either side, for the step buttons' own tooltips.
 *
 * <p>They were drawn in the tile until 2026-11-21, when Joost had them taken out: at the rail's
 * 226 px they fought the value for the one line there is, and a value that does not fit is worse
 * than a neighbour that has to be hovered for. On the button they still answer the one question a
 * stepper cannot ask on its own — what this press is about to do.</p>
 */
const neighbours = computed(() =>
  hasGrid.value && Number.isFinite(props.value)
    ? ladderNeighbours(props.ladder!, props.value, props.format!)
    : { down: null, up: null },
)

/** Whether a press would move anything, so an end of the ladder reads as one. */
const canStepDown = computed(
  () => !hasGrid.value || stepLadder(props.ladder!, props.value, -1) !== props.value,
)
const canStepUp = computed(
  () => !hasGrid.value || stepLadder(props.ladder!, props.value, 1) !== props.value,
)

/** Which cell is lit: by ratio, since a stored value need not be exactly a rung. */
function isCurrent(cellValue: number | null): boolean {
  return (
    cellValue !== null &&
    Number.isFinite(props.value) &&
    props.value > 0 &&
    Math.abs(cellValue / props.value - 1) < 1e-9
  )
}

function step(direction: 1 | -1) {
  emit('step', direction)
}

function pick(cellValue: number | null) {
  if (cellValue === null) return
  emit('pick', cellValue)
  open.value = false
}

// ---------------------------------------------------------------------------
// Where the popover goes
// ---------------------------------------------------------------------------

/**
 * Teleported to the body and positioned by hand, which is forced rather than chosen:
 * `CollapsibleSection`'s own root is `overflow-hidden`, so a popover living inside the panel is
 * cut off at its bottom edge — and the grid is seven rows taller than the panel it opens from.
 * Fixed coordinates read off the tile are what escape that; the cost is this bookkeeping.
 */
const placement = ref({ left: 0, top: 0, width: 0, above: false })

/** Roughly what the grid needs, used only to decide which side of the tile it opens on. */
const ESTIMATED_HEIGHT_PX = 320
const GAP_PX = 6

/** No narrower than this, or the three columns and the row labels have nowhere to go. */
const MIN_WIDTH_PX = 260
/** Kept this far off the window's own edges. */
const MARGIN_PX = 8

function place() {
  const el = tile.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const below = window.innerHeight - rect.bottom
  const above = below < ESTIMATED_HEIGHT_PX && rect.top > below
  // Anchored to the tile, then pulled back inside the window: these rails sit against the right
  // edge, and a grid that is wider than its tile would otherwise hang off it.
  const width = Math.max(rect.width, MIN_WIDTH_PX)
  const left = Math.max(MARGIN_PX, Math.min(rect.left, window.innerWidth - width - MARGIN_PX))
  placement.value = {
    left,
    top: above ? rect.top - GAP_PX : rect.bottom + GAP_PX,
    width,
    above,
  }
}

function onDocumentPointerDown(event: PointerEvent) {
  const target = event.target as Node | null
  if (!target) return
  if (tile.value?.contains(target) || popover.value?.contains(target)) return
  open.value = false
}

function onKeydown(event: KeyboardEvent) {
  if (!open.value) return
  if (event.key === 'Escape') {
    event.preventDefault()
    open.value = false
    return
  }
  // The footer promises these, so they are handled while the popover is open whatever has focus:
  // the gesture is to open the grid and walk the ladder with the trace in view.
  if (event.key === 'ArrowRight' || event.key === 'ArrowUp') {
    event.preventDefault()
    step(1)
  } else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') {
    event.preventDefault()
    step(-1)
  }
}

function onWheel(event: WheelEvent) {
  if (event.deltaY === 0) return
  event.preventDefault()
  step(event.deltaY < 0 ? 1 : -1)
}

watch(open, (isOpen) => {
  if (isOpen) {
    place()
    void nextTick(place)
    // Capture, because what scrolls is an ancestor rather than the window: `<main>` is the
    // scroller here, and a bubble-phase listener on window never hears it.
    window.addEventListener('scroll', place, true)
    window.addEventListener('resize', place)
    document.addEventListener('pointerdown', onDocumentPointerDown, true)
    window.addEventListener('keydown', onKeydown)
  } else {
    window.removeEventListener('scroll', place, true)
    window.removeEventListener('resize', place)
    document.removeEventListener('pointerdown', onDocumentPointerDown, true)
    window.removeEventListener('keydown', onKeydown)
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('scroll', place, true)
  window.removeEventListener('resize', place)
  document.removeEventListener('pointerdown', onDocumentPointerDown, true)
  window.removeEventListener('keydown', onKeydown)
})

const tileStyle = computed(() => ({
  backgroundColor: `var(--kind-${props.tone}-badge-bg)`,
  borderColor: `var(--kind-${props.tone}-badge-border)`,
}))
const inkStyle = computed(() => ({ color: `var(--kind-${props.tone}-chip-fg)` }))

/**
 * The middle sits on the panel's own ground rather than on the tint — Joost's design of 2026-11-21.
 *
 * <p>It is what separates the <em>setting</em> from the two buttons that change it: the tint is the
 * frame and the controls, and the value is read off a plain surface, the way every other figure on
 * the rail is. It also stops a long value sharing a ground with the <code>+</code> beside it.</p>
 */
const bodyStyle = computed(() => ({ backgroundColor: 'var(--surface-panel)' }))
</script>

<template>
  <div
    ref="tile"
    class="flex h-[52px] items-stretch gap-1 rounded-xl border p-1 transition"
    :style="{
      ...tileStyle,
      ...(open ? { boxShadow: `0 0 0 3px var(--kind-${tone}-badge-border)` } : {}),
    }"
    :title="props.title"
  >
    <button
      type="button"
      class="flex w-7 shrink-0 items-center justify-center rounded-lg text-lg font-semibold transition hover:bg-black/5 disabled:cursor-not-allowed disabled:opacity-35 dark:hover:bg-white/10"
      :style="inkStyle"
      :disabled="!canStepDown"
      :title="neighbours.down ? `${label}: ${neighbours.down}` : undefined"
      :aria-label="`${label}: one step down`"
      @click="step(-1)"
    >
      −
    </button>

    <!-- The body. With a ladder it opens the grid; without one it is whatever the caller put in
         the slot, which for the Centre box is a number input that still has to be typable. -->
    <component
      :is="hasGrid ? 'button' : 'div'"
      :type="hasGrid ? 'button' : undefined"
      class="flex min-w-0 flex-1 items-center gap-1.5 rounded-lg px-2 text-left"
      :class="hasGrid ? 'transition hover:brightness-95 dark:hover:brightness-110' : ''"
      :style="bodyStyle"
      :aria-haspopup="hasGrid ? 'dialog' : undefined"
      :aria-expanded="hasGrid ? open : undefined"
      @click="hasGrid ? (open = !open) : undefined"
    >
      <span class="flex min-w-0 flex-1 flex-col justify-center leading-tight">
        <span class="truncate text-[10.5px] font-medium text-neutral-500 dark:text-neutral-400">{{ label }}</span>
        <slot>
          <span class="truncate text-[15px] font-semibold tabular-nums" :style="inkStyle">
            {{ display }}<span v-if="suffix" class="text-[11px] font-medium">{{ suffix }}</span>
          </span>
        </slot>
      </span>
      <svg
        v-if="hasGrid"
        width="12"
        height="12"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2.5"
        stroke-linecap="round"
        stroke-linejoin="round"
        class="shrink-0 text-neutral-400 transition"
        :class="open ? 'rotate-180' : ''"
        aria-hidden="true"
      >
        <path d="M6 9l6 6 6-6" />
      </svg>
    </component>

    <button
      type="button"
      class="flex w-7 shrink-0 items-center justify-center rounded-lg text-lg font-semibold transition hover:bg-black/5 disabled:cursor-not-allowed disabled:opacity-35 dark:hover:bg-white/10"
      :style="inkStyle"
      :disabled="!canStepUp"
      :title="neighbours.up ? `${label}: ${neighbours.up}` : undefined"
      :aria-label="`${label}: one step up`"
      @click="step(1)"
    >
      +
    </button>

    <!-- Autoscale, only where the caller has an automatic answer for this control. The figure is
         what every instrument draws for "fit this to the signal": a bracket either side with the
         arrows pushing out to the edges it is about to find. -->
    <button
      v-if="autoTitle"
      type="button"
      class="flex w-6 shrink-0 items-center justify-center rounded-lg transition hover:bg-black/5 dark:hover:bg-white/10"
      :style="inkStyle"
      :title="autoTitle"
      :aria-label="`${label}: autoscale`"
      @click="emit('auto')"
    >
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M3 5v14" />
        <path d="M21 5v14" />
        <path d="M7 12h10" />
        <path d="M10 9l-3 3 3 3" />
        <path d="M14 9l3 3-3 3" />
      </svg>
    </button>
  </div>

  <Teleport v-if="hasGrid" to="body">
    <div
      v-if="open"
      ref="popover"
      class="fixed z-50 rounded-xl border border-neutral-200 p-3 shadow-xl dark:border-neutral-700"
      :style="{
        left: `${placement.left}px`,
        top: `${placement.top}px`,
        width: `${placement.width}px`,
        transform: placement.above ? 'translateY(-100%)' : undefined,
        backgroundColor: 'var(--surface-panel)',
      }"
      role="dialog"
      :aria-label="label"
      @wheel="onWheel"
    >
      <div class="mb-2 flex items-baseline gap-2">
        <span class="text-[13px] font-semibold">{{ label }}</span>
        <span class="text-[11px] text-neutral-500 dark:text-neutral-400">{{ grid!.range }}</span>
      </div>

      <div class="grid items-center gap-1" style="grid-template-columns: auto repeat(3, minmax(0, 1fr))">
        <span></span>
        <span
          v-for="column in grid!.columns"
          :key="column"
          class="pb-0.5 text-center text-[11px] text-neutral-400 dark:text-neutral-500"
          >{{ column }}</span
        >

        <template v-for="row in grid!.rows" :key="row.label">
          <span class="flex items-center justify-end gap-1.5 pr-1 text-[11.5px] font-medium tabular-nums">
            <span
              class="size-1.5 shrink-0 rounded-full"
              :style="{ backgroundColor: `var(--kind-${row.tone}-chip-fg)` }"
              aria-hidden="true"
            ></span>
            {{ row.label }}
          </span>
          <button
            v-for="(cell, column) in row.cells"
            :key="column"
            type="button"
            class="h-8 rounded-lg border text-[12.5px] font-semibold tabular-nums transition"
            :class="
              cell.value === null
                ? 'cursor-not-allowed border-transparent text-neutral-300 dark:text-neutral-700'
                : isCurrent(cell.value)
                  ? 'border-transparent text-white'
                  : 'border-neutral-200 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800'
            "
            :style="
              isCurrent(cell.value) ? { backgroundColor: `var(--kind-${tone}-chip-fg)` } : undefined
            "
            :disabled="cell.value === null"
            :aria-current="isCurrent(cell.value) ? 'true' : undefined"
            @click="pick(cell.value)"
          >
            {{ cell.text }}
          </button>
        </template>
      </div>

      <div
        class="mt-2.5 flex items-center justify-between border-t border-neutral-200 pt-2 text-[10.5px] text-neutral-400 dark:border-neutral-800 dark:text-neutral-500"
      >
        <span>Wheel or ← → steps one value</span>
        <span>Esc closes</span>
      </div>
    </div>
  </Teleport>
</template>

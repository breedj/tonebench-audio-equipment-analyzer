<script setup lang="ts">
import { computed } from 'vue'
import { ICON_HEIGHT, ICON_WIDTH, scopeMeasurementIcon } from '../scope/measurementIcons'

/**
 * One measurement's figure — a waveform with the quantity marked on it.
 *
 * <p>The shapes are data (<code>scope/measurementIcons.ts</code>); this only draws them. The
 * backdrop trace is faint and the mark is in the current colour, so a figure reads the same way
 * against a panel, inside a chosen card and in either theme without a palette of its own.</p>
 *
 * <p><b><code>mark</code> is the two-colour reading of the same shapes</b>, which everything that
 * stands for a <em>chosen</em> measurement asks for — the picker's tiles and, since 2026-11-20, the
 * readings table's own rows, so a row is drawn exactly as the tile it was picked off. Given one,
 * the trace stops being faint and takes
 * the current colour outright — its well is already tinted, so it has contrast of its own — and
 * everything drawn <em>on</em> it takes the mark colour instead. Nothing about the data changes:
 * <code>faint</code> already means "this is the trace, the rest is the reading", which is exactly
 * the split the two colours want. Without it the component draws precisely what it always did.</p>
 */

const props = withDefaults(
  defineProps<{
    name: string
    /**
     * How big to draw it.
     *
     * <p><b>At the call site rather than in here</b>, because the two places this is used want
     * different answers and the component has no way to tell them apart: the picker's tiles are
     * read while choosing and want a figure big enough to recognise, while the readings table puts
     * one in a 24 px column beside each row. Growing the component's own class would grow the table
     * rows with it.</p>
     *
     * <p>The size is <b>CSS only</b>. <code>ICON_WIDTH</code>/<code>ICON_HEIGHT</code> are the
     * viewBox, and all 31 shape tables plus their test are written in that 24x16 space.</p>
     */
    size?: 'sm' | 'lg'
    /**
     * A CSS colour for the marks on the trace — a level line, a dimension line, a dot, a band.
     *
     * <p>Absent means the one-colour figure the readings table has always drawn.</p>
     */
    mark?: string
  }>(),
  { size: 'sm', mark: undefined },
)

const shapes = computed(() => scopeMeasurementIcon(props.name))

/** Whether this is being drawn in two colours, which is what decides every stroke below. */
const twoTone = computed(() => props.mark !== undefined)

/** What a shape is stroked in: the trace keeps the current colour, the marks take theirs. */
function ink(faint?: boolean): string | undefined {
  if (!twoTone.value) return undefined
  return faint ? undefined : props.mark
}

/** How solid a shape is. A two-colour figure has no need to whisper its trace. */
function opacity(faint?: boolean): number {
  return faint && !twoTone.value ? 0.4 : 1
}

/**
 * How heavy a stroke is.
 *
 * <p>In two colours the trace is drawn a little heavier than what is marked on it, which is the
 * hierarchy the faintness used to carry: the waveform is the thing, and the level lines and
 * dimension arrows are an annotation of it. Undefined everywhere else, so the one-colour figure
 * keeps the single width the <code>svg</code> sets.</p>
 */
function weight(faint?: boolean): number | undefined {
  if (!twoTone.value) return undefined
  return faint ? 1 : 0.8
}

/** Half the arm of a dimension line's end tick, in the icon's own units. */
const TICK = 2
</script>

<template>
  <svg
    :viewBox="`0 0 ${ICON_WIDTH} ${ICON_HEIGHT}`"
    class="shrink-0 overflow-visible"
    :class="props.size === 'lg' ? 'h-6 w-9' : 'h-4 w-6'"
    fill="none"
    stroke="currentColor"
    :stroke-width="props.size === 'lg' ? 0.75 : 1"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <template v-for="(shape, i) in shapes" :key="i">
      <path
        v-if="shape.t === 'path'"
        :d="shape.d"
        :stroke="ink(shape.faint)"
        :stroke-width="weight(shape.faint)"
        :stroke-opacity="opacity(shape.faint)"
        :stroke-dasharray="shape.dash ? '2 1.5' : undefined"
      />
      <line
        v-else-if="shape.t === 'line'"
        :x1="shape.x1"
        :y1="shape.y1"
        :x2="shape.x2"
        :y2="shape.y2"
        :stroke="ink(shape.faint)"
        :stroke-width="weight(shape.faint)"
        :stroke-opacity="opacity(shape.faint)"
        :stroke-dasharray="shape.dash ? '2 1.5' : undefined"
      />
      <rect
        v-else-if="shape.t === 'fill'"
        :x="shape.x"
        :y="shape.y"
        :width="shape.w"
        :height="shape.h"
        :fill="props.mark ?? 'currentColor'"
        :fill-opacity="twoTone ? 0.28 : 0.22"
        stroke="none"
      />
      <!-- A dimension line: the span itself and a tick at each end, which is what makes it read as a
           measurement rather than as one more trace. -->
      <template v-else-if="shape.t === 'spanH'">
        <line :x1="shape.x1" :y1="shape.y" :x2="shape.x2" :y2="shape.y" :stroke="ink(false)" :stroke-width="weight(false)" />
        <line :x1="shape.x1" :y1="shape.y - TICK" :x2="shape.x1" :y2="shape.y + TICK" :stroke="ink(false)" :stroke-width="weight(false)" />
        <line :x1="shape.x2" :y1="shape.y - TICK" :x2="shape.x2" :y2="shape.y + TICK" :stroke="ink(false)" :stroke-width="weight(false)" />
      </template>
      <template v-else-if="shape.t === 'spanV'">
        <line :x1="shape.x" :y1="shape.y1" :x2="shape.x" :y2="shape.y2" :stroke="ink(false)" :stroke-width="weight(false)" />
        <line :x1="shape.x - TICK" :y1="shape.y1" :x2="shape.x + TICK" :y2="shape.y1" :stroke="ink(false)" :stroke-width="weight(false)" />
        <line :x1="shape.x - TICK" :y1="shape.y2" :x2="shape.x + TICK" :y2="shape.y2" :stroke="ink(false)" :stroke-width="weight(false)" />
      </template>
    </template>
  </svg>
</template>

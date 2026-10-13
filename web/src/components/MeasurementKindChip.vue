<script setup lang="ts">
/**
 * A measurement kind's own glyph inside the round tinted circle of its accent — the sidebar's rows
 * and anywhere else a kind is listed, from the 2026-10-12 mockups. The glyph is
 * MeasurementKindIcon's, so a new kind needs one entry there and one in `MEASUREMENT_KINDS`, and
 * every list in the app picks it up; the circle is PanelIcon's geometry, spelled again here because
 * that component draws from the *panel* glyph table rather than the kind one.
 */
import { computed } from 'vue'
import MeasurementKindIcon from './MeasurementKindIcon.vue'
import { measurementKindTone } from '../measurements/kinds'

const props = withDefaults(defineProps<{ kind: string; size?: number }>(), { size: 30 })

const tone = computed(() => measurementKindTone(props.kind))
/** 16-in-30 in the mockup's sidebar, which is the same 4/7 the panel headers use. */
const glyphSize = computed(() => Math.round((props.size * 8) / 15))
</script>

<template>
  <span
    class="flex shrink-0 items-center justify-center rounded-full"
    :style="{
      width: `${props.size}px`,
      height: `${props.size}px`,
      backgroundColor: `var(--kind-${tone}-chip-bg)`,
      color: `var(--kind-${tone}-chip-fg)`,
    }"
    aria-hidden="true"
  >
    <MeasurementKindIcon :kind="kind" :size="glyphSize" />
  </span>
</template>

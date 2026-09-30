<script setup lang="ts">
import { GENERATOR_LEVEL_UNITS } from '../generatorLevel'
import { useGeneratorStimulus } from '../composables/useGeneratorStimulus'

/**
 * The unit dropdown that sits beside every level box in the generator panel — the tone rows, the
 * IMD preset's sine-equivalent level, the multitone comb's total RMS, the noise level and the
 * chirp's amplitude.
 *
 * One component rather than a copy of the markup per box, because it is one setting: `levelUnit` is a
 * single value on the shared stimulus store (see useGeneratorStimulus.ts), so changing it on any
 * box changes them all. Repeated per box on purpose even so — it reads as that field's unit rather
 * than as a preference parked somewhere else on the panel.
 *
 * Display only: the generator is always told dBV, so this can never disagree with what is playing.
 */
const { levelUnit } = useGeneratorStimulus()

const emit = defineEmits<{ change: [] }>()
</script>

<template>
  <select
    v-model="levelUnit"
    class="rounded border border-neutral-300 py-1 pl-1 pr-0.5 text-xs dark:border-neutral-700"
    title="The unit every level box on this panel is typed in. The generator is always told dBV; this is a display choice."
    @change="emit('change')"
  >
    <option v-for="u in GENERATOR_LEVEL_UNITS" :key="u.value" :value="u.value">{{ u.label }}</option>
  </select>
</template>

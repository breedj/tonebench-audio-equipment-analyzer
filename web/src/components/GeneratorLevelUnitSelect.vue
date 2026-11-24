<script setup lang="ts">
import { GENERATOR_LEVEL_UNITS } from '../generatorLevel'
import type { GeneratorStimulusStore } from '../composables/useGeneratorStimulus'

/**
 * The unit dropdown that sits beside every level box in the generator panel — the tone rows, the
 * IMD preset's sine-equivalent level, the multitone comb's total RMS, the noise level and the
 * chirp's amplitude.
 *
 * One component rather than a copy of the markup per box, because it is one setting: `levelUnit` is a
 * single value on one stimulus store (see useGeneratorStimulus.ts), so changing it on any box
 * changes them all. Repeated per box on purpose even so — it reads as that field's unit rather
 * than as a preference parked somewhere else on the panel.
 *
 * Display only: the generator is always told dBV, so this can never disagree with what is playing.
 *
 * <p><b>The store is a required prop and must stay one.</b> It read the FFT screen's module
 * singleton directly, which was right while that screen was the only one composing a stimulus and
 * silently wrong the day the oscilloscope got a store of its own: the box's conversion reads
 * <code>props.stimulus.levelUnit</code> (GeneratorChannelPanel) while this dropdown wrote the
 * singleton's, so on the scope screen picking <b>mV</b> left the box converting in <b>dBV</b> —
 * and a level typed as 5 mV was played as 5 dBV, which is 1.778 V, <b>51 dB</b> out. Nothing on
 * screen contradicted it: the number stood beside the unit the user had just chosen. Reported from
 * a bench 2026-11-24 as the scope "not outputting what it says", with every other screen correct.
 * A default back to the singleton is exactly how that returns, so there is none — the one thing
 * this component must not be able to do is pick its own store.</p>
 */
const props = defineProps<{ stimulus: GeneratorStimulusStore }>()

const levelUnit = props.stimulus.levelUnit

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

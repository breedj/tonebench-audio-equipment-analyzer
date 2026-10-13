<script setup lang="ts">
// What the ±1 dB and −3 dB corners are measured down from, at the top of the Measurements panel
// next to the corners it defines. The three values are the shared useBandwidthReference store —
// the config panels save them with the measurement and send them with every run; what this block
// adds is that a change is applied to the traces already on screen. The server re-finds the
// corners on the full-resolution estimates it kept, and the rebuilt traces arrive over the stream
// like a pass's. The reference is a reading of the response, not a parameter of the stimulus,
// which is why it can move after the fact when the smoothing, say, cannot.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { api, type BandwidthReferenceRequest, type ResponseReferenceModeName } from '../api/client'
import { useBandwidthReference } from '../composables/useBandwidthReference'

const props = defineProps<{
  /** Which measurement's traces a change is applied to. */
  stepped: boolean
}>()

const {
  bandwidthReference: reference,
  bandwidthReferenceLowHz: lowHz,
  bandwidthReferenceHighHz: highHz,
} = useBandwidthReference()

/** See useBandwidthReference for why the passband median is the default. */
const BANDWIDTH_REFERENCES: { value: ResponseReferenceModeName; label: string }[] = [
  { value: 'BandMedian', label: 'Passband median' },
  { value: 'Peak', label: 'Peak' },
]

/** The band boxes are only read — and so only validated and only shown — by the median reference. */
const usesReferenceBand = computed(() => reference.value === 'BandMedian')

/** Mirrors BandwidthReferenceSpec.Validate on the server, so a bad band is caught before it is sent. */
const invalid = computed<string | null>(() => {
  if (!usesReferenceBand.value) return null
  if (!Number.isFinite(lowHz.value) || !Number.isFinite(highHz.value)) return 'Reference band must be numbers.'
  if (lowHz.value < 1 || highHz.value > 96000) return 'Reference band must lie between 1 Hz and 96 kHz.'
  if (highHz.value <= lowHz.value) return 'Reference band must end above where it starts.'
  return null
})

const error = ref<string | null>(null)

function request(): BandwidthReferenceRequest {
  return {
    bandwidthReference: reference.value,
    bandwidthReferenceLowHz: lowHz.value,
    bandwidthReferenceHighHz: highHz.value,
  }
}

async function apply() {
  if (invalid.value) return
  error.value = null
  try {
    if (props.stepped) await api.setSteppedResponseBandwidthReference(request())
    else await api.setFrequencyResponseBandwidthReference(request())
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

// The dropdown applies at once; the band boxes wait for typing to stop, since "2000" passes
// through "2", "20" and "200" on its way in and each of those is a legal band on its own.
let timer: ReturnType<typeof setTimeout> | null = null
watch(reference, () => {
  if (timer) clearTimeout(timer)
  timer = null
  void apply()
})
watch([lowHz, highHz], () => {
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => {
    timer = null
    void apply()
  }, 400)
})
onBeforeUnmount(() => {
  if (timer) clearTimeout(timer)
})

defineExpose({ invalid })
</script>

<template>
  <!-- One row of three, as the 2026-10-12 mockup lays it out: the reference and the band it is
       taken over are read together. `auto-fit` is what makes that safe in both places this panel
       lands — side by side in the chart column, stacked in the two-wide flow under it — and the cap
       keeps the row from stretching to 600 px in the wide case, where three short fields spread
       that far read as a form that has lost its layout. -->
  <div class="mb-2 flex max-w-[620px] flex-col gap-2">
    <div class="grid gap-2.5" style="grid-template-columns: repeat(auto-fit, minmax(170px, 1fr))">
      <label
        class="tb-field"
        title="What the bandwidth corners are measured down from. Passband median is right for an amplifier: its output transformer's HF resonance is often taller than the passband, and referencing the peak then measures the resonance instead. Pick Peak for a DUT whose response is itself a peak. A change is applied to the curves on screen at once."
      >
        Bandwidth reference
        <select v-model="reference" class="tb-input">
          <option v-for="option in BANDWIDTH_REFERENCES" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>

      <!-- Three cells of the row above, not a nested pair: all three are read together, and the
           auto-fit columns are what let them stack where the panel is narrow. -->
      <template v-if="usesReferenceBand">
        <label class="tb-field" title="Bottom of the band the reference level is the median of.">
          Reference from (Hz)
          <input v-model.number="lowHz" type="number" min="1" max="96000" class="tb-input" />
        </label>
        <label class="tb-field" title="Top of the band the reference level is the median of.">
          Reference to (Hz)
          <input v-model.number="highHz" type="number" min="1" max="96000" class="tb-input" />
        </label>
      </template>
    </div>

    <p v-if="invalid" class="text-[12px] text-red-600 dark:text-red-400">{{ invalid }}</p>
    <p v-else-if="error" class="text-[12px] text-red-600 dark:text-red-400">{{ error }}</p>
  </div>
</template>

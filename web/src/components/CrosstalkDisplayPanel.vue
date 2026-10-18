<script setup lang="ts">
import { computed, watch } from 'vue'
import { useFrequencyResponse, type ResponseTrace } from '../composables/useFrequencyResponse'
import { useMeasurements } from '../composables/useMeasurements'
import { RESPONSE_X_FROM_OPTIONS, RESPONSE_X_TO_OPTIONS, snapResponseXWindow } from '../response/display'
import AnnotationsPanel from './AnnotationsPanel.vue'
import { CROSSTALK_ANNOTATION_VIEW } from '../charts/annotationViews'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The crosstalk chart's display rail: a dB window, a frequency window, one Autofit for both, and the
 * two style toggles. Its own small panel rather than a `crosstalk` flag on
 * FrequencyResponseDisplayPanel — that panel is a projection/view/phase/coherence/bandwidth rail
 * for a Bode plot, and every one of those controls would be hidden here.
 */
const yTopDb = defineModel<number>('yTopDb', { required: true })
const yBottomDb = defineModel<number>('yBottomDb', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

const { traces, running } = useFrequencyResponse()
const { overlays } = useMeasurements()

const DB_MIN = -200
const DB_MAX = 40
const DB_STEP = 5
const DB_MIN_SPAN = 10

// The dropdowns' own values and the outward snap both live in response/display.ts: this panel and
// the crosstalk one offer the same window, and "Set axis" has to land on a value a select can show.
const X_FROM_OPTIONS = RESPONSE_X_FROM_OPTIONS
const X_TO_OPTIONS = RESPONSE_X_TO_OPTIONS

/** See FrequencyResponseDisplayPanel.commitBoundedNumber for why `:value` + `@change`. */
function commitBoundedNumber(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current
  el.value = String(next)
  return next
}

function onTopChange(event: Event) {
  yTopDb.value = commitBoundedNumber(event, yTopDb.value, yBottomDb.value + DB_MIN_SPAN, DB_MAX)
}

function onBottomChange(event: Event) {
  yBottomDb.value = commitBoundedNumber(event, yBottomDb.value, DB_MIN, yTopDb.value - DB_MIN_SPAN)
}

function isCrosstalk(t: ResponseTrace): boolean {
  return t.kind === 'CrosstalkLeftToRight' || t.kind === 'CrosstalkRightToLeft'
}

/** Every crosstalk curve on the chart: live plus checked-on history. */
const fitTraces = computed<ResponseTrace[]>(() => [
  ...traces.value.filter(isCrosstalk),
  ...overlays.value.filter((o) => o.visible).flatMap((o) => (o.responseTraces ?? []).filter(isCrosstalk)),
])

function autofitDb() {
  let lo = Infinity
  let hi = -Infinity
  for (const trace of fitTraces.value) {
    for (let i = 0; i < trace.magnitudeMaxDb.length; i++) {
      const f = trace.freqHz[i]
      if (f < xFromHz.value || f > xToHz.value) continue
      lo = Math.min(lo, trace.magnitudeMinDb[i])
      hi = Math.max(hi, trace.magnitudeMaxDb[i])
    }
  }
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  const top = Math.ceil((hi + 2) / DB_STEP) * DB_STEP
  const bottom = Math.floor((lo - 2) / DB_STEP) * DB_STEP
  yTopDb.value = Math.min(DB_MAX, Math.max(top, bottom + DB_MIN_SPAN))
  yBottomDb.value = Math.max(DB_MIN, Math.min(bottom, yTopDb.value - DB_MIN_SPAN))
}

function autofitX() {
  const list = fitTraces.value.filter((t) => t.freqHz.length > 0)
  if (list.length === 0) return
  const from = Math.min(...list.map((t) => t.startFreqHz))
  const to = Math.max(...list.map((t) => t.endFreqHz))
  const snapped = snapResponseXWindow(from, to)
  xFromHz.value = snapped.fromHz
  xToHz.value = snapped.toHz
}

/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30). The frequency window is fitted
 * first: every level fit below is scoped to it, so fitting the other way round sizes the axis to
 * whatever the old window happened to hold.
 */
function autofitAll() {
  autofitX()
  autofitDb()
}

watch(running, (isRunning, wasRunning) => {
  if (wasRunning && !isRunning && autofitOnDone.value) autofitAll()
})
</script>

<template>
  <!-- The chart's one Autofit, at the top of the rail and in no section at all. It used to sit
       inside the first axis section, which read as if it belonged to that one axis (Joost,
       2026-10-18) — it has always fitted every axis the chart is showing, and now it looks like a
       chart action rather than a range control. Above the sections rather than between them so it
       is in the same place on every screen. -->
  <button
    type="button"
    class="btn-panel-accent"
    title="Fits both axes to every crosstalk curve on the chart, live and history: the frequency range to the measured span, the dB window to the curves inside it."
    @click="autofitAll"
  >
    Autofit
  </button>

  <CollapsibleSection title="Crosstalk range (dB)" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Top
        <input
          :value="yTopDb"
          type="number"
          :step="DB_STEP"
          class="tb-input"
          @change="onTopChange"
        />
      </label>
      <label class="tb-field">
        Bottom
        <input
          :value="yBottomDb"
          type="number"
          :step="DB_STEP"
          class="tb-input"
          @change="onBottomChange"
        />
      </label>
    </div>
  </CollapsibleSection>

  <CollapsibleSection title="Frequency range (Hz)" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        From
        <select v-model.number="xFromHz" class="tb-input">
          <option v-for="hz in X_FROM_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
      <label class="tb-field">
        To
        <select v-model.number="xToHz" class="tb-input">
          <option v-for="hz in X_TO_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
    </div>
  </CollapsibleSection>

  <CollapsibleSection title="Display">
    <label class="tb-check-row">
      <input v-model="autofitOnDone" type="checkbox" class="tb-check" /> Autofit when measurement done
    </label>
    <div class="tb-field">
      <span class="tb-group">Style</span>
      <label class="tb-check-row"><input v-model="thickLines" type="checkbox" class="tb-check" /> Thick lines</label>
      <label class="tb-check-row"><input v-model="showDataPoints" type="checkbox" class="tb-check" /> Show data points</label>
    </div>
  </CollapsibleSection>

  <AnnotationsPanel :view="CROSSTALK_ANNOTATION_VIEW" />
</template>

<script setup lang="ts">
import AnnotationsPanel from './AnnotationsPanel.vue'
import { BODE_ANNOTATION_VIEW } from '../charts/annotationViews'
import { computed, watch } from 'vue'
import type { ImpedanceZScale } from '../api/client'
import type { ImpedanceTrace } from '../composables/useImpedance'
import { useMeasurements } from '../composables/useMeasurements'
import { useOutputImpedance } from '../composables/useOutputImpedance'
import { dbOhmsToOhms } from '../impedance/equivalent'
import { MIN_PHASE_SPAN_DEG } from '../impedance/phaseAxis'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * The output-impedance chart's display rail: the ohms window and scale, the damping-factor and
 * phase toggles, a phase window, a frequency window and the style toggles. Its own small panel
 * rather than a flag on ImpedanceDisplayPanel — that one carries a Nyquist view, a short standard
 * and a coherence toggle, none of which this screen has, and the damping-factor axis is this
 * screen's alone.
 */
const props = defineProps<{
  traces: ImpedanceTrace[]
  /** Whether the run on screen measured a phase — the toggle is inert otherwise. */
  phaseAvailable: boolean
}>()

const zTopOhms = defineModel<number>('zTopOhms', { required: true })
const zBottomOhms = defineModel<number>('zBottomOhms', { required: true })
const zScale = defineModel<ImpedanceZScale>('zScale', { required: true })
const showDampingFactor = defineModel<boolean>('showDampingFactor', { required: true })
const showPhase = defineModel<boolean>('showPhase', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })
const phaseTopDeg = defineModel<number>('phaseTopDeg', { required: true })
const phaseBottomDeg = defineModel<number>('phaseBottomDeg', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

const { running } = useOutputImpedance()
const { overlays } = useMeasurements()

const PHASE_MIN = -1440
const PHASE_MAX = 1440
const PHASE_TICK_DEG = 45
/**
 * What Autofit leaves when the curve has no spread of its own: a flat resistive fixture reads
 * 0.00° at every frequency, and a window fitted to that exactly would have no height at all.
 * Two ticks, so the fitted axis still carries labels.
 *
 * <p>Deliberately <b>not</b> what the Top and Bottom boxes are clamped to — see the handlers
 * below.</p>
 */
const PHASE_AUTOFIT_MIN_SPAN = PHASE_TICK_DEG * 2

/** A logarithmic axis cannot reach zero; a linear one may start there. Milliohms are the whole point here, so the floor is low. */
const Z_LOG_MIN = 0.0001
const Z_MAX = 1e6
const zMin = computed(() => (zScale.value === 'Log' ? Z_LOG_MIN : 0))

const X_FROM_OPTIONS = [1, 2, 5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

/** See ImpedanceDisplayPanel.commitBoundedNumber for why `:value` + `@change`. */
function commitBoundedNumber(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current
  el.value = String(next)
  return next
}

function onTopZChange(event: Event) {
  zTopOhms.value = commitBoundedNumber(event, zTopOhms.value, Math.max(zBottomOhms.value * 1.1, zBottomOhms.value + 0.0001), Z_MAX)
}

function onBottomZChange(event: Event) {
  zBottomOhms.value = commitBoundedNumber(event, zBottomOhms.value, zMin.value, Math.min(zTopOhms.value / 1.1, zTopOhms.value - 0.0001))
}

watch(zScale, (scale) => {
  if (scale === 'Log' && zBottomOhms.value < Z_LOG_MIN) zBottomOhms.value = Z_LOG_MIN
})

/**
 * Top and Bottom are held apart by {@link MIN_PHASE_SPAN_DEG} — the chart's own guard against a
 * window with no height — and by nothing more (Joost, 2026-10-22). They used to be held two 45°
 * ticks apart, i.e. a 90° window at the narrowest, and on this chart that is the whole of the
 * default window: at +90°/−90° the Bottom box refused anything above 0°, and with the top at 90°
 * after an Autofit the Top box refused anything below 90°. So a phase that lives inside one
 * quadrant — which is most of them, a capacitor near −90° and a coil near +90° — could not be
 * framed in the half of the plot the phase is drawn in.
 */
function onTopPhaseChange(event: Event) {
  phaseTopDeg.value = commitBoundedNumber(event, phaseTopDeg.value, phaseBottomDeg.value + MIN_PHASE_SPAN_DEG, PHASE_MAX)
}

function onBottomPhaseChange(event: Event) {
  phaseBottomDeg.value = commitBoundedNumber(event, phaseBottomDeg.value, PHASE_MIN, phaseTopDeg.value - MIN_PHASE_SPAN_DEG)
}

/** Every Zout curve on the chart: live plus checked-on history. */
const fitTraces = computed<ImpedanceTrace[]>(() => [
  ...props.traces.filter((t) => t.kind === 'Impedance'),
  ...overlays.value.filter((o) => o.visible).flatMap((o) => (o.impedanceTraces ?? []).filter((t) => t.kind === 'Impedance')),
])

function roundDown125(v: number): number {
  const magnitude = Math.pow(10, Math.floor(Math.log10(v)))
  const m = v / magnitude
  return (m >= 5 ? 5 : m >= 2 ? 2 : 1) * magnitude
}

function roundUp125(v: number): number {
  const magnitude = Math.pow(10, Math.floor(Math.log10(v)))
  const m = v / magnitude
  return (m > 5 ? 10 : m > 2 ? 5 : m > 1 ? 2 : 1) * magnitude
}

/**
 * The ohms window from every curve on the chart, rounded outward to a 1/2/5 step. A magnitude-only
 * run can hold exact zeros (a point read as no impedance at all), which are no bound on a log axis
 * and are skipped; a linear window still starts at zero, the ImpedanceDisplayPanel rule.
 */
function autofitZ() {
  let min = Infinity
  let max = -Infinity
  for (const t of fitTraces.value) {
    for (let i = 0; i < t.freqHz.length; i++) {
      if (t.freqHz[i] < xFromHz.value || t.freqHz[i] > xToHz.value) continue
      const lo = dbOhmsToOhms(t.magnitudeMinDbOhms[i])
      const hi = dbOhmsToOhms(t.magnitudeMaxDbOhms[i])
      if (hi > Z_LOG_MIN) max = Math.max(max, hi)
      if (lo > Z_LOG_MIN) min = Math.min(min, lo)
    }
  }
  if (!Number.isFinite(max) || !(max > 0)) return
  if (!Number.isFinite(min)) min = max / 10

  zBottomOhms.value = zScale.value === 'Log' ? Math.max(Z_LOG_MIN, roundDown125(min)) : 0
  zTopOhms.value = Math.min(Z_MAX, roundUp125(max))
}

function autofitPhase() {
  let min = Infinity
  let max = -Infinity
  for (const t of fitTraces.value) {
    for (let i = 0; i < t.phaseDeg.length; i++) {
      if (t.freqHz[i] < xFromHz.value || t.freqHz[i] > xToHz.value) continue
      min = Math.min(min, t.phaseDeg[i])
      max = Math.max(max, t.phaseDeg[i])
    }
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) return

  const bottom = Math.floor(min / PHASE_TICK_DEG) * PHASE_TICK_DEG
  let top = Math.ceil(max / PHASE_TICK_DEG) * PHASE_TICK_DEG
  if (top - bottom < PHASE_AUTOFIT_MIN_SPAN) top = bottom + PHASE_AUTOFIT_MIN_SPAN
  phaseBottomDeg.value = Math.max(PHASE_MIN, bottom)
  phaseTopDeg.value = Math.min(PHASE_MAX, top)
}

function autofitX() {
  const list = fitTraces.value.filter((t) => t.freqHz.length > 0)
  if (list.length === 0) return
  const from = Math.min(...list.map((t) => t.startFreqHz))
  const to = Math.max(...list.map((t) => t.endFreqHz))
  xFromHz.value = [...X_FROM_OPTIONS].reverse().find((v) => v <= from) ?? X_FROM_OPTIONS[0]
  xToHz.value = X_TO_OPTIONS.find((v) => v >= to) ?? X_TO_OPTIONS[X_TO_OPTIONS.length - 1]
}

/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30). Frequency first: the ohms and phase
 * fits are both scoped to the frequency window.
 */
function autofitAll() {
  autofitX()
  autofitZ()
  if (props.phaseAvailable) autofitPhase()
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
    title="Fits every axis to what is drawn, live and history: the frequency window to the measured band, then the ohms and phase axes to the curves inside it. The damping-factor axis follows the ohms one — it is that window inverted."
    @click="autofitAll"
  >
    Autofit
  </button>

  <CollapsibleSection title="Impedance axis" size="xs">
    <label
      class="tb-field"
      title="Logarithmic is the one scale that fits a solid-state amplifier's milliohms and a tube amplifier's ohms in one window, and puts the damping-factor curve on a mirrored axis. Linear reads a flat Zout as flat."
    >
      Scale
      <select v-model="zScale" class="tb-input">
        <option value="Linear">Linear</option>
        <option value="Log">Logarithmic</option>
      </select>
    </label>
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Bottom (Ω)
        <input :value="zBottomOhms" type="number" step="any" class="tb-input" @change="onBottomZChange" />
      </label>
      <label class="tb-field">
        Top (Ω)
        <input :value="zTopOhms" type="number" step="any" class="tb-input" @change="onTopZChange" />
      </label>
    </div>
    <!-- The chart's one Autofit, in the first axis section rather than a scroll below the fields
         it overwrites. -->
    <label class="tb-check-row" title="The damping factor — nominal over |Zout| — on a right-hand axis that mirrors the ohms axis.">
      <input v-model="showDampingFactor" type="checkbox" class="tb-check" />
      Show damping factor
    </label>
    <label
      class="tb-check-row"
      :class="{ 'opacity-50': !phaseAvailable }"
      :title="phaseAvailable ? 'The phase of Zout: positive is inductive (an output coil), near zero resistive.' : 'Only a run with the right channel as reference measures a phase.'"
    >
      <input v-model="showPhase" type="checkbox" :disabled="!phaseAvailable" />
      Show phase
    </label>
  </CollapsibleSection>

  <CollapsibleSection v-if="showPhase && phaseAvailable" title="Phase axis" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Bottom (°)
        <input :value="phaseBottomDeg" type="number" step="15" class="tb-input" @change="onBottomPhaseChange" />
      </label>
      <label class="tb-field">
        Top (°)
        <input :value="phaseTopDeg" type="number" step="15" class="tb-input" @change="onTopPhaseChange" />
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

  <AnnotationsPanel :view="BODE_ANNOTATION_VIEW" />
</template>

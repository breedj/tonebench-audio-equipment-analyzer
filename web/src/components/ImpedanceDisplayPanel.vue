<script setup lang="ts">
import { computed, watch } from 'vue'
import type { ImpedanceView, ImpedanceZScale } from '../api/client'
import { useImpedance, type ImpedanceTrace } from '../composables/useImpedance'
import CollapsibleSection from './CollapsibleSection.vue'
import { dbOhmsToOhms } from '../impedance/equivalent'
import { standardOnGrid } from '../impedance/standard'

const props = defineProps<{
  traces: ImpedanceTrace[]
  running: boolean
}>()

const view = defineModel<ImpedanceView>('view', { required: true })
const showPhase = defineModel<boolean>('showPhase', { required: true })
const showShortCal = defineModel<boolean>('showShortCal', { required: true })
const showCoherence = defineModel<boolean>('showCoherence', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const zTopOhms = defineModel<number>('zTopOhms', { required: true })
const zBottomOhms = defineModel<number>('zBottomOhms', { required: true })
const zScale = defineModel<ImpedanceZScale>('zScale', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })
const phaseTopDeg = defineModel<number>('phaseTopDeg', { required: true })
const phaseBottomDeg = defineModel<number>('phaseBottomDeg', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

// Matches the frequency-response panel's constants, so the two phase axes behave identically.
const PHASE_MIN = -1440
const PHASE_MAX = 1440
const PHASE_TICK_DEG = 45
const PHASE_MIN_SPAN = PHASE_TICK_DEG * 2

/** A logarithmic axis cannot reach zero; a linear one may start there. */
const Z_LOG_MIN = 0.001

/**
 * The same ceiling the server clamps an open circuit to (`ImpedanceMath.MaxOhms`, 1e9). It used to
 * be 1e7, which is below what an open-ish DUT — a straight wire, an amplifier, anything that is not
 * a fixture — actually reports, so Autofit could not contain the data and, on the log scale, an
 * all-open trace fitted its bottom to 1e9 and its top to 1e7: an inverted window and a blank chart
 * that read as "the button does nothing".
 */
const Z_MAX = 1e9
const zMin = computed(() => (zScale.value === 'Log' ? Z_LOG_MIN : 0))

const X_FROM_OPTIONS = [1, 2, 5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

/**
 * Clamped through `:value` + `@change` rather than `v-model`, and that is not a style choice: a
 * `defineModel` write round-trips as a prop, so a same-tick handler reading the model would clamp
 * against the stale value and let an out-of-range number through.
 */
function commitBoundedNumber(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current

  el.value = String(next)
  return next
}

const trace = computed(() => props.traces.find((t) => t.kind === 'Impedance') ?? null)

// The stored short standard, for the autofit below. Same module-level state the chart reads, so the
// two cannot disagree about which standard or which reference resistor is in play.
const { storedShortCal, referenceOhms } = useImpedance()

// The window has to keep some height whichever scale it is on: a ratio on the log axis, where a
// bottom of zero is not allowed, and a ratio or a milliohm on the linear one, where it is.
function onTopZChange(event: Event) {
  zTopOhms.value = commitBoundedNumber(event, zTopOhms.value, Math.max(zBottomOhms.value * 1.1, zBottomOhms.value + 0.001), Z_MAX)
}

function onBottomZChange(event: Event) {
  zBottomOhms.value = commitBoundedNumber(event, zBottomOhms.value, zMin.value, Math.min(zTopOhms.value / 1.1, zTopOhms.value - 0.001))
}

/**
 * Switching to the log axis with the bottom parked at zero — the linear default — would blank the
 * plot, so the bottom is lifted to the smallest thing the log axis can show and the next Autofit
 * puts it somewhere sensible.
 */
watch(zScale, (scale) => {
  if (scale === 'Log' && zBottomOhms.value < Z_LOG_MIN) zBottomOhms.value = Z_LOG_MIN
})

function onTopPhaseChange(event: Event) {
  phaseTopDeg.value = commitBoundedNumber(event, phaseTopDeg.value, phaseBottomDeg.value + PHASE_MIN_SPAN, PHASE_MAX)
}

function onBottomPhaseChange(event: Event) {
  phaseBottomDeg.value = commitBoundedNumber(event, phaseBottomDeg.value, PHASE_MIN, phaseTopDeg.value - PHASE_MIN_SPAN)
}

/**
 * The impedance window from everything currently drawn, rounded outward to a 1/2/5 step so the axis
 * lands on readable decades rather than on the data's exact extremes.
 *
 * <p>On the <b>linear</b> axis the bottom is zero rather than the rounded-down minimum: an impedance
 * curve is read against zero — a driver's 6 Ω rest and 60 Ω peak are one picture, and a resistor's
 * flatness is only visible against it — and a window fitted to the data's own extremes stretches
 * every milliohm of noise to full height instead. The log axis keeps the rounded minimum, since
 * there zero does not exist and a decade is a decade wherever it sits.</p>
 *
 * <p><b>Everything drawn</b> means the short standard too, whenever its checkbox is ticked. A short
 * is a fraction of an ohm against a DUT of several, so a fit taken from the measured curve alone put
 * the standard a decade or more below the bottom of the window it had just produced — the one curve
 * the user had explicitly asked to see, fitted off the chart.</p>
 *
 * <p>It is sampled on the same grid the chart draws it on, through the same function, so the window
 * matches what is on screen rather than approximating it.</p>
 */
function autofitZ() {
  const t = trace.value
  const cal = showShortCal.value ? storedShortCal.value : null
  if ((!t || t.freqHz.length === 0) && !cal) return

  let min = Infinity
  let max = -Infinity

  if (t) {
    for (let i = 0; i < t.freqHz.length; i++) {
      if (t.freqHz[i] < xFromHz.value || t.freqHz[i] > xToHz.value) continue
      min = Math.min(min, dbOhmsToOhms(t.magnitudeMinDbOhms[i]))
      max = Math.max(max, dbOhmsToOhms(t.magnitudeMaxDbOhms[i]))
    }
  }

  if (cal) {
    // The chart's own grid choice, mirrored: the DUT's where there is one, the standard's otherwise.
    const grid = t && t.freqHz.length > 0 ? t.freqHz : cal.freqHz
    const curve = standardOnGrid(cal, grid, referenceOhms.value)
    const ohms = curve?.ohms

    for (let i = 0; ohms && i < ohms.length; i++) {
      const v = ohms[i]
      // Null outside the calibrated band, and a standard that inverts to zero has no place on a
      // logarithmic axis — neither is a bound.
      if (v == null || !(v > 0)) continue
      if (grid[i] < xFromHz.value || grid[i] > xToHz.value) continue
      min = Math.min(min, v)
      max = Math.max(max, v)
    }
  }

  if (!Number.isFinite(min) || !Number.isFinite(max) || !(min > 0) || !(max > 0)) return

  zBottomOhms.value = zScale.value === 'Log' ? Math.max(Z_LOG_MIN, roundDown125(min)) : 0
  zTopOhms.value = Math.min(Z_MAX, roundUp125(max))
}

/**
 * The phase window from everything drawn on the degrees axis, snapped out to whole ticks.
 *
 * <p>The short standard's own phase counts too when it is showing, on the same reasoning as the
 * impedance fit: a lead's angle climbs steeply at the top of the band — past 30° by 20 kHz on this
 * fixture — so a window fitted to a flat DUT alone would put it off the chart.</p>
 */
function autofitPhase() {
  const t = trace.value
  const cal = showShortCal.value ? storedShortCal.value : null
  if ((!t || t.phaseDeg.length === 0) && !cal) return

  let min = Infinity
  let max = -Infinity

  if (t) {
    for (let i = 0; i < t.phaseDeg.length; i++) {
      if (t.freqHz[i] < xFromHz.value || t.freqHz[i] > xToHz.value) continue
      min = Math.min(min, t.phaseDeg[i])
      max = Math.max(max, t.phaseDeg[i])
    }
  }

  if (cal) {
    const grid = t && t.freqHz.length > 0 ? t.freqHz : cal.freqHz
    const phaseDeg = standardOnGrid(cal, grid, referenceOhms.value)?.phaseDeg

    for (let i = 0; phaseDeg && i < phaseDeg.length; i++) {
      const v = phaseDeg[i]
      if (v == null) continue
      if (grid[i] < xFromHz.value || grid[i] > xToHz.value) continue
      min = Math.min(min, v)
      max = Math.max(max, v)
    }
  }

  if (!Number.isFinite(min) || !Number.isFinite(max)) return

  let bottom = Math.floor(min / PHASE_TICK_DEG) * PHASE_TICK_DEG
  let top = Math.ceil(max / PHASE_TICK_DEG) * PHASE_TICK_DEG
  if (top - bottom < PHASE_MIN_SPAN) top = bottom + PHASE_MIN_SPAN

  phaseBottomDeg.value = Math.max(PHASE_MIN, bottom)
  phaseTopDeg.value = Math.min(PHASE_MAX, top)
}

/** The nearest 1/2/5 step at or below a value — the same ladder the axis ticks use. */
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
 * Whether this run has already had its opening fit. Reset when a run starts, so every run gets one
 * and only one.
 */
let fittedThisRun = false

/** The measured band, snapped out to the dropdowns' own presets — the same shape as every other panel's. */
function autofitX() {
  const t = trace.value
  if (!t || t.freqHz.length === 0) return
  const from = t.freqHz[0]
  const to = t.freqHz[t.freqHz.length - 1]
  xFromHz.value = [...X_FROM_OPTIONS].reverse().find((v) => v <= from) ?? X_FROM_OPTIONS[0]
  xToHz.value = X_TO_OPTIONS.find((v) => v >= to) ?? X_TO_OPTIONS[X_TO_OPTIONS.length - 1]
}

/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30). Frequency first: both of the fits
 * below are scoped to the frequency window, so the other order sizes the axes to whatever the
 * previous window happened to hold.
 */
function autofitAll() {
  autofitX()
  autofitZ()
  autofitPhase()
}

watch(
  () => props.running,
  (isRunning, wasRunning) => {
    if (isRunning && !wasRunning) {
      fittedThisRun = false
      return
    }

    // Again at the end, because the opening fit was taken from the first point or two and a sweep
    // that later climbs into a resonance would otherwise stay clipped.
    if (wasRunning && !isRunning && autofitOnDone.value) autofitAll()
  },
)

/**
 * Fit once as soon as a run produces something, as well as when it ends.
 *
 * <p><b>Why the opening fit is needed at all.</b> The window otherwise stays wherever the stored
 * display settings left it, and a window left over from a different DUT puts the whole live sweep
 * off the chart — a 7.9 Ω resistor measured against a 2 kΩ–10 MΩ window from an earlier run shows
 * nothing at all until the run finishes. Minutes of a stepped sweep with an empty chart is
 * indistinguishable from a run that has hung.</p>
 *
 * <p>Once per run rather than on every trace, which is the reason this needs a flag rather than
 * just watching the trace: a stepped sweep republishes after every point, and refitting fifty times
 * makes the axis crawl and the curve squirm — the thing the end-of-run-only rule was avoiding.</p>
 */
watch(
  () => trace.value,
  (t) => {
    if (!props.running || fittedThisRun || !autofitOnDone.value) return
    if (!t || t.freqHz.length === 0) return

    fittedThisRun = true
    // The value axes only: a frequency window fitted to the two or three points a stepped sweep has
    // published so far would be narrower than the sweep it is about to draw, and this fires once.
    // The end-of-run fit above takes the frequency axis with it.
    autofitZ()
    autofitPhase()
  },
)
</script>

<template>
  <CollapsibleSection title="Display" size="xs">
    <label class="tb-field">
      Chart
      <select
        v-model="view"
        class="tb-input"
      >
        <option value="Bode">|Z| and phase</option>
        <option value="Nyquist">Complex plane (R vs X)</option>
      </select>
    </label>

    <template v-if="view === 'Bode'">
      <label class="tb-check-row">
        <input v-model="showPhase" type="checkbox" class="tb-check" />
        Phase
      </label>
      <label
        class="tb-check-row"
        title="How much of the measurement was explained by the stimulus. Below about 0.99 the impedance there is not to be trusted — usually the divider output has got too small."
      >
        <input v-model="showCoherence" type="checkbox" class="tb-check" />
        Coherence
      </label>
      <label
        class="tb-check-row"
        title="The stored short standard as impedance — the lead and connector impedance every reading sits on top of, and the floor below which this fixture cannot see."
      >
        <input v-model="showShortCal" type="checkbox" class="tb-check" />
        Short calibration
      </label>
    </template>

    <label class="tb-check-row">
      <input v-model="thickLines" type="checkbox" class="tb-check" />
      Thick lines
    </label>
    <label v-if="view === 'Bode'" class="tb-check-row">
      <input v-model="showDataPoints" type="checkbox" class="tb-check" />
      Data points
    </label>
  </CollapsibleSection>

  <CollapsibleSection v-if="view === 'Bode'" title="Impedance axis" size="xs">
    <label
      class="tb-field"
      title="Linear ohms is how an impedance curve is usually drawn and reads a flat load as flat. Logarithmic is the one scale that fits a fixture's milliohms and a driver's resonance peak in one window."
    >
      Scale
      <select
        v-model="zScale"
        class="tb-input"
      >
        <option value="Linear">Linear</option>
        <option value="Log">Logarithmic</option>
      </select>
    </label>
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Top (Ω)
        <input
          :value="zTopOhms"
          type="number"
          step="any"
          class="tb-input"
          @change="onTopZChange"
        />
      </label>
      <label class="tb-field">
        Bottom (Ω)
        <input
          :value="zBottomOhms"
          type="number"
          step="any"
          class="tb-input"
          @change="onBottomZChange"
        />
      </label>
    </div>
    <!-- The chart's one Autofit: impedance, phase and the frequency window together. It sits in
         the first axis section rather than a scroll below the fields it overwrites. -->
    <button
      type="button"
      class="rounded border px-2 py-1 text-xs btn-action"
      title="Fits every axis to what is drawn: the frequency window to the measured band, then the impedance and phase axes to the curves inside it (including the short standard when it is shown)."
      @click="autofitAll"
    >
      Autofit
    </button>
  </CollapsibleSection>

  <CollapsibleSection v-if="view === 'Bode' && showPhase" title="Phase axis" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Top (°)
        <input
          :value="phaseTopDeg"
          type="number"
          step="15"
          class="tb-input"
          @change="onTopPhaseChange"
        />
      </label>
      <label class="tb-field">
        Bottom (°)
        <input
          :value="phaseBottomDeg"
          type="number"
          step="15"
          class="tb-input"
          @change="onBottomPhaseChange"
        />
      </label>
    </div>
  </CollapsibleSection>

  <CollapsibleSection title="Frequency axis" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        From (Hz)
        <select
          v-model.number="xFromHz"
          class="tb-input"
        >
          <option v-for="hz in X_FROM_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
      <label class="tb-field">
        To (Hz)
        <select
          v-model.number="xToHz"
          class="tb-input"
        >
          <option v-for="hz in X_TO_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
    </div>

    <label class="tb-check-row">
      <!--
        The model is still named autofitOnDone, and stored as chartAutofitOnDone, because renaming it
        would orphan the setting on every measurement type already saved. It now fits twice: once
        when a run first produces a curve, once when it ends.
      -->
      <input v-model="autofitOnDone" type="checkbox" class="tb-check" />
      Autofit each measurement
    </label>
  </CollapsibleSection>
</template>

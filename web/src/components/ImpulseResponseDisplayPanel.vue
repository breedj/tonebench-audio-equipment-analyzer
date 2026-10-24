<script setup lang="ts">
import AnnotationsPanel from './AnnotationsPanel.vue'
import { computed } from 'vue'
import type {
  BurstDecayResolutionName,
  CsdWindowModeName,
  DecayChartMode,
  DecayPalette,
  ImpulseTraceJson,
  ImpulseView,
  ResponseDelayView,
} from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { useImpulseResponse } from '../composables/useImpulseResponse'
import { useMeasurements } from '../composables/useMeasurements'
import { autofitRange, autofitRangeInBand } from '../measurements/autofitRange'
import { CSD_FFT_LENGTHS, csdSpanMs, describeCsdFftLength, PALETTE_NAMES } from '../measurements/decay'
import { impulseReferenceLabel } from '../measurements/impulseReadings'
import { chooseDelayUnit, delayFitStepUs, delayValuesOf, unitToUs, usToUnit } from '../response/delayAxis'
import CollapsibleSection from './CollapsibleSection.vue'

const view = defineModel<ImpulseView>('view', { required: true })
const normalisedImpulse = defineModel<boolean>('normalisedImpulse', { required: true })
const showPhase = defineModel<boolean>('showPhase', { required: true })
const showCoherence = defineModel<boolean>('showCoherence', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const yTopDb = defineModel<number>('yTopDb', { required: true })
const yBottomDb = defineModel<number>('yBottomDb', { required: true })
const timeFromMs = defineModel<number>('timeFromMs', { required: true })
const timeToMs = defineModel<number>('timeToMs', { required: true })
const freqFromHz = defineModel<number>('freqFromHz', { required: true })
const freqToHz = defineModel<number>('freqToHz', { required: true })
const delayView = defineModel<ResponseDelayView>('delayView', { required: true })
const delayTopUs = defineModel<number>('delayTopUs', { required: true })
const delayBottomUs = defineModel<number>('delayBottomUs', { required: true })

/** Same convention as the response screen: held in µs, edited and drawn in the unit the window's size calls for. */
const delayUnit = computed(() => chooseDelayUnit(delayTopUs.value, delayBottomUs.value))
const delayTopInUnit = computed({
  get: () => usToUnit(delayTopUs.value, delayUnit.value),
  set: (v: number) => {
    if (Number.isFinite(v)) delayTopUs.value = unitToUs(v, delayUnit.value)
  },
})
const delayBottomInUnit = computed({
  get: () => usToUnit(delayBottomUs.value, delayUnit.value),
  set: (v: number) => {
    if (Number.isFinite(v)) delayBottomUs.value = unitToUs(v, delayUnit.value)
  },
})
const DELAY_VIEWS: { value: ResponseDelayView; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'Group', label: 'Group delay' },
  { value: 'Phase', label: 'Phase delay' },
]

// The two decay surfaces. How they are drawn is shared; what is analysed is per measurement.
const decayMode = defineModel<DecayChartMode>('decayMode', { required: true })
const decayPalette = defineModel<DecayPalette>('decayPalette', { required: true })
const decayColoredCurves = defineModel<boolean>('decayColoredCurves', { required: true })
const decayRangeDb = defineModel<number>('decayRangeDb', { required: true })
const csdFftLength = defineModel<number>('csdFftLength', { required: true })
const csdBlockShiftSamples = defineModel<number>('csdBlockShiftSamples', { required: true })
const csdBlockCount = defineModel<number>('csdBlockCount', { required: true })
const csdWindowMode = defineModel<CsdWindowModeName>('csdWindowMode', { required: true })
const csdRiseTimeMs = defineModel<number>('csdRiseTimeMs', { required: true })
const burstMaxPeriods = defineModel<number>('burstMaxPeriods', { required: true })
const burstPointsPerPeriod = defineModel<number>('burstPointsPerPeriod', { required: true })
const burstResolution = defineModel<BurstDecayResolutionName>('burstResolution', { required: true })

const { impulse, traces, harmonics, status, gateResolutionHz, running } = useImpulseResponse()

// The checked history items, which the two curve views draw alongside the measurement. Autofit has
// to see them or it sizes the axis to the live data alone — and with only history on screen it had
// nothing to fit at all, so the button sat disabled over a chart full of curves.
const { overlays } = useMeasurements()

/** Checked history items carrying an impulse waveform, in the order the chart draws them. */
const overlayImpulses = computed(() =>
  overlays.value.map((o) => (o.visible ? o.impulseTrace : null)).filter((t): t is ImpulseTraceJson => !!t),
)

/** Checked history items carrying a gated magnitude, likewise. */
const overlayGains = computed(() =>
  overlays.value
    .filter((o) => o.visible)
    .map((o) => o.responseTraces?.find((t) => t.kind === 'Gain') ?? null)
    .filter((t): t is ResponseTrace => t !== null),
)

const VIEWS: { value: ImpulseView; label: string }[] = [
  { value: 'Impulse', label: 'Impulse response' },
  { value: 'Response', label: 'Gated response' },
  { value: 'Harmonics', label: 'Harmonics' },
  { value: 'Csd', label: 'Cumulative spectral decay' },
  { value: 'BurstDecay', label: 'Burst decay' },
]

/** The two views that are analyses of the impulse rather than readings off it. */
const isDecayView = computed(() => view.value === 'Csd' || view.value === 'BurstDecay')

const CSD_WINDOW_MODES: { value: CsdWindowModeName; label: string }[] = [
  { value: 'Apodizing', label: 'CSD, apodizing window' },
  { value: 'Hann', label: 'STF, Hann window' },
]

const BURST_RESOLUTIONS: { value: BurstDecayResolutionName; label: string }[] = [
  { value: 'PreferTime', label: 'Prefer time resolution' },
  { value: 'PreferFrequency', label: 'Prefer frequency resolution' },
]

const CHART_MODES: { value: DecayChartMode; label: string }[] = [
  { value: 'Waterfall', label: 'Waterfall (f)' },
  { value: 'WaterfallPeriods', label: 'Waterfall (p)' },
  { value: 'Sonogram', label: 'Sonogram' },
]

const sampleRateHz = computed(() => status.value?.sampleRateHz ?? 0)

/** What the transform length means here — the two numbers the setting is actually about. */
const csdWindowLabel = computed(() => describeCsdFftLength(csdFftLength.value, sampleRateHz.value))

/** How far past the impulse the slices reach. The one number that decides whether the run is refused. */
const csdSpanLabel = computed(() =>
  `${csdSpanMs(csdBlockShiftSamples.value, csdBlockCount.value, sampleRateHz.value).toFixed(2)} ms of decay`,
)

/**
 * Fits the time axis to the gate that was actually measured, not to the panel's current form — the
 * chart is showing the data the run produced, and after a gate change those are two different
 * things until the next run.
 */
function fitToGate() {
  const i = impulse.value
  if (!i) return
  timeFromMs.value = -i.gatePreMs
  timeToMs.value = i.gatePostMs
}

/** The band the harmonic curves actually cover — the widest order's, since the others stop earlier. */
function fitToHarmonics() {
  const curves = harmonics.value
  if (curves.length === 0) return
  const lo = Math.min(...curves.map((c) => c.fundamentalHz[0] ?? 20))
  const hi = Math.max(...curves.map((c) => c.upperValidHz))
  freqFromHz.value = Math.max(1, lo)
  freqToHz.value = hi
}

/**
 * Whether Autofit has anything to do. In the volts impulse view the level axis fits itself
 * continuously — its magnitude spans six decades across DUTs, so a stored window would be wrong far
 * more often than right — but the *time* axis is still the button's to set, which is why a live
 * impulse qualifies whatever the normalisation says. Offering a button that cannot change anything
 * is worse than not offering one.
 */
const canAutofit = computed(() => {
  // The decay views have a range rather than a window: 0 dB is fixed by the surface's own
  // normalisation, so there is nothing for an autofit to find.
  if (isDecayView.value) return false
  if (view.value === 'Impulse') {
    return impulse.value !== null || (normalisedImpulse.value && overlayImpulses.value.length > 0)
  }
  if (view.value === 'Response') return traces.value.length > 0 || overlayGains.value.length > 0
  return harmonics.value.length > 0
})

const autofitTitle = computed(() => {
  if (view.value === 'Impulse') {
    return normalisedImpulse.value
      ? 'Fits the time axis to the measured gate and the level axis to the impulse drawn in it.'
      : 'Fits the time axis to the measured gate. The volts view already scales its own level axis.'
  }
  if (view.value === 'Harmonics') return 'Fits the frequency range to the band the harmonic curves cover, and the level axis to them.'
  return 'Fits the level axis, and the phase and delay axes with it, to what is currently drawn.'
})

/** How far below the peak the impulse view will fit. Past this it is showing the gate, not the DUT. */
const ImpulseFloorDb = -120

/** The impulse as the chart draws it in normalised mode: dB of each column's largest excursion, re the peak. */
function impulseLevelsDb(): number[] {
  // The overlays are on the same axis, each dB re its own peak exactly as the chart draws it, so the
  // fitted floor is the lowest of everything shown rather than of the live trace alone.
  const levels: number[] = []
  for (const trace of [...(impulse.value ? [impulse.value] : []), ...overlayImpulses.value]) {
    const peak = Math.max(trace.peakVolts, 1e-12)
    for (let i = 0; i < trace.maxVolts.length; i++) {
      levels.push(20 * Math.log10(Math.max(Math.abs(trace.maxVolts[i]), Math.abs(trace.minVolts[i]), 1e-12) / peak))
    }
  }
  return levels
}

/**
 * Sizes the level axis, and on the gated response the phase axis with it — the two are read
 * together, and fitting one while leaving the other is half an answer.
 */
function autofit() {
  if (view.value === 'Impulse') {
    // The time axis first — one Autofit per chart rather than one per axis (Joost, 2026-09-30), so
    // this is what the separate "Fit to gate" button used to do. Harmless with no live impulse.
    fitToGate()
    if (!normalisedImpulse.value) return
    // The peak is 0 dB by definition, so only the floor is in question — and it is bounded, not
    // measured: the gate tapers the impulse to nothing at its edges, so the lowest sample says where
    // the dB conversion bottoms out rather than anything about the DUT.
    const fitted = autofitRange(impulseLevelsDb(), { minBottom: ImpulseFloorDb })
    if (fitted) {
      yTopDb.value = 0
      yBottomDb.value = fitted.bottom
    }
    return
  }

  if (view.value === 'Response') {
    // The live gain when there is one, else the first overlay's — the phase and delay axes below are
    // read off a single curve, and the live measurement is the one being read when both are present.
    const gain = traces.value.find((t) => t.kind === 'Gain') ?? traces.value[0] ?? overlayGains.value[0]
    if (!gain) return

    // The level axis, though, is fitted to everything drawn on it: an overlay left outside the window
    // is exactly the "I checked it and nothing happened" the button is there to prevent.
    const levels: number[] = []
    const freqs: number[] = []
    for (const curve of [...traces.value, ...overlayGains.value]) {
      levels.push(...curve.magnitudeMinDb, ...curve.magnitudeMaxDb)
      freqs.push(...curve.freqHz, ...curve.freqHz)
    }
    const fitted = autofitRangeInBand(freqs, levels, freqFromHz.value, freqToHz.value)
    if (fitted) {
      yTopDb.value = fitted.top
      yBottomDb.value = fitted.bottom
    }

    if (delayView.value !== 'None') {
      const curve = delayValuesOf(gain, delayView.value)
      if (curve) {
        const us = curve.map((v) => v * 1e6)
        const inBand: number[] = []
        for (let i = 0; i < us.length; i++) {
          if (gain.freqHz[i] >= freqFromHz.value && gain.freqHz[i] <= freqToHz.value && Number.isFinite(us[i])) inBand.push(us[i])
        }
        const values = inBand.length > 0 ? inBand : us
        const step = delayFitStepUs(Math.max(...values) - Math.min(...values))
        const delay = autofitRange(values, { step, minSpan: step * 2, pad: 0.05 })
        if (delay) {
          delayTopUs.value = delay.top
          delayBottomUs.value = delay.bottom
        }
      }
    }
    return
  }

  // Harmonics. The frequency window first, for the reason every other panel fits x before y: the
  // level fit below is scoped to it. This is what the separate "Fit to measured band" button did.
  fitToHarmonics()

  // Every order at once: they share one axis, so fitting the loudest alone would push the rest off.
  const levels: number[] = []
  const freqs: number[] = []
  for (const curve of harmonics.value) {
    for (let i = 0; i < curve.levelDbc.length; i++) {
      if (curve.fundamentalHz[i] > curve.upperValidHz) continue
      freqs.push(curve.fundamentalHz[i])
      levels.push(curve.levelDbc[i])
    }
  }

  const fitted = autofitRangeInBand(freqs, levels, freqFromHz.value, freqToHz.value)
  if (fitted) {
    yTopDb.value = fitted.top
    yBottomDb.value = fitted.bottom
  }
}

// Shared with the report's impulse chapter, which prints this same block (measurements/impulseReadings.ts).
const referenceLabel = computed(() => {
  const s = status.value
  return s ? impulseReferenceLabel(s.referenceMode, s.referenceCalibrationName) : null
})
</script>

<template>
  <!-- The chart's one Autofit, at the top of the rail and in no section at all. It used to sit
       inside the first axis section, which read as if it belonged to that one axis (Joost,
       2026-10-18) — it has always fitted every axis the chart is showing, and now it looks like a
       chart action rather than a range control. Above the sections rather than between them so it
       is in the same place on every screen. Hidden under the two decay surfaces, which have no axis of
       their own to fit. -->
  <button
    v-if="!isDecayView"
    type="button"
    class="btn-panel-accent"
    :disabled="!canAutofit"
    :title="autofitTitle"
    @click="autofit"
  >
    Autofit
  </button>

  <CollapsibleSection title="Display">
    <label class="tb-field">
      View
      <select
        v-model="view"
        class="tb-input"
      >
        <option v-for="option in VIEWS" :key="option.value" :value="option.value">{{ option.label }}</option>
      </select>
    </label>

    <label class="tb-check-row">
      <input v-model="thickLines" type="checkbox" class="tb-check" />
      Thick lines
    </label>

    <template v-if="view === 'Impulse'">
      <label
        class="tb-check-row"
        title="Show the impulse as dB below its own peak instead of volts. Easier to read a decay off; harder to see polarity, which only the volts view carries."
      >
        <input v-model="normalisedImpulse" type="checkbox" class="tb-check" />
        Normalised (dB re peak)
      </label>

      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          From (ms)
          <input
            v-model.number="timeFromMs"
            type="number"
            step="0.5"
            class="tb-input"
          />
        </label>
        <label class="tb-field">
          To (ms)
          <input
            v-model.number="timeToMs"
            type="number"
            step="0.5"
            class="tb-input"
          />
        </label>
      </div>

    </template>

    <template v-else-if="view === 'Response'">
      <label class="tb-check-row">
        <input v-model="showPhase" type="checkbox" class="tb-check" />
        Phase
      </label>
      <label class="tb-check-row" title="Only meaningful from two averages upward — a single record always reads 1.">
        <input v-model="showCoherence" type="checkbox" class="tb-check" />
        Coherence
      </label>

      <!-- No phase window: the curve is wrapped into ±180° and its axis is the bottom half of the
           plot, fixed — see PHASE_SCALE_RANGE in response/phaseCurve.ts. -->

      <!-- The gated response's delay curves, on their own axis: physical delay only with the right
           input as the reference, excess delay against the generated sweep otherwise. -->
      <label
        class="tb-field"
        title="Group delay is −dφ/df, phase delay −φ/(360·f), of the gated response on their own axis at the right. With the right input as reference these are the DUT's physical delay; against the generated sweep the bulk delay went out with the record's arbitrary start point and only the excess remains."
      >
        Delay
        <select
          v-model="delayView"
          class="tb-input"
        >
          <option v-for="option in DELAY_VIEWS" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>

      <div v-if="delayView !== 'None'" class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          Delay top ({{ delayUnit }})
          <input
            v-model.number="delayTopInUnit"
            type="number"
            :step="delayUnit === 'ms' ? 0.1 : 10"
            class="tb-input"
          />
        </label>
        <label class="tb-field">
          Delay bottom ({{ delayUnit }})
          <input
            v-model.number="delayBottomInUnit"
            type="number"
            :step="delayUnit === 'ms' ? 0.1 : 10"
            class="tb-input"
          />
        </label>
      </div>
    </template>

    <template v-else-if="view === 'Harmonics'">
      <label class="tb-check-row">
        <input v-model="showDataPoints" type="checkbox" class="tb-check" />
        Data points
      </label>

      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          From (Hz)
          <input
            v-model.number="freqFromHz"
            type="number"
            step="1"
            class="tb-input"
          />
        </label>
        <label class="tb-field">
          To (Hz)
          <input
            v-model.number="freqToHz"
            type="number"
            step="100"
            class="tb-input"
          />
        </label>
      </div>

    </template>

    <!-- The decay surfaces. Everything here is an analysis of the impulse response already
         measured, so changing any of it redraws rather than re-measures — which is why it lives on
         the display panel and not beside the sweep settings. -->
    <template v-else>
      <label class="tb-field">
        Chart
        <select
          v-model="decayMode"
          class="tb-input"
          title="Stacked curves in oblique projection, or the same surface as a flat colour map. Waterfall (f) puts frequency across the curves and recedes in time, which shows where in the band something rings; Waterfall (p) puts time across and recedes in frequency, so each curve is one frequency's decay envelope. The sonogram shows exactly where in frequency it is, flat."
        >
          <option v-for="option in CHART_MODES" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>

      <label class="tb-field">
        Palette
        <select
          v-model="decayPalette"
          class="tb-input"
        >
          <option v-for="name in PALETTE_NAMES" :key="name" :value="name">{{ name }}</option>
        </select>
      </label>

      <label
        v-if="decayMode !== 'Sonogram'"
        class="tb-check-row"
        title="Colour each curve by level. Off draws them all in one colour, which reads the shape rather than the level."
      >
        <input v-model="decayColoredCurves" type="checkbox" class="tb-check" />
        Coloured curves
      </label>

      <label class="tb-field">
        Range (dB)
        <input
          v-model.number="decayRangeDb"
          type="number"
          min="10"
          max="70"
          step="5"
          class="tb-input"
          title="How far below 0 dB the chart reaches. 0 dB is whatever this surface normalised to — the loudest point of the whole surface for a cumulative spectral decay, each frequency's own maximum for a burst decay."
        />
      </label>

      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          From (Hz)
          <input
            v-model.number="freqFromHz"
            type="number"
            step="1"
            class="tb-input"
          />
        </label>
        <label class="tb-field">
          To (Hz)
          <input
            v-model.number="freqToHz"
            type="number"
            step="100"
            class="tb-input"
          />
        </label>
      </div>

      <template v-if="view === 'Csd'">
        <label class="tb-field">
          Analysis
          <select
            v-model="csdWindowMode"
            class="tb-input"
            title="The cumulative-spectral-decay window is ARTA's: a rectangle tapered at both ends, so every sample from the slice's own time to the end of the block counts at full weight — which is what a cumulative spectrum integrates. A Hann window is an ordinary short-time transform, centred on its own slice time — it reads a resonance building up as well as dying away, and smears the impulse across half a window."
          >
            <option v-for="option in CSD_WINDOW_MODES" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </label>

        <label class="tb-field">
          FFT length (samples)
          <select
            v-model.number="csdFftLength"
            class="tb-input"
          >
            <option v-for="length in CSD_FFT_LENGTHS" :key="length" :value="length">{{ length }}</option>
          </select>
          <span class="text-[10px] text-neutral-500 dark:text-neutral-400">{{ csdWindowLabel }}</span>
        </label>

        <div class="grid grid-cols-2 gap-2.5">
          <label class="tb-field">
            Block shift
            <input
              v-model.number="csdBlockShiftSamples"
              type="number"
              min="1"
              step="1"
              class="tb-input"
              title="Samples between one slice and the next — the surface's time step."
            />
          </label>
          <label class="tb-field">
            Max blocks
            <input
              v-model.number="csdBlockCount"
              type="number"
              min="2"
              max="200"
              step="1"
              class="tb-input"
            />
          </label>
        </div>
        <span class="text-[10px] text-neutral-500 dark:text-neutral-400">{{ csdSpanLabel }}</span>

        <label v-if="csdWindowMode === 'Apodizing'" class="tb-field">
          Rise time (ms)
          <input
            v-model.number="csdRiseTimeMs"
            type="number"
            min="0.02"
            max="1"
            step="0.02"
            class="tb-input"
            title="How long the window takes to open, and to close again at the far end of the block — a Blackman4 taper over each. Short keeps the leading edge sharp; too short and the edge itself is a step, whose leakage buries the decay being looked for."
          />
        </label>
      </template>

      <template v-else>
        <div class="grid grid-cols-2 gap-2.5">
          <label class="tb-field">
            Max periods
            <input
              v-model.number="burstMaxPeriods"
              type="number"
              min="2"
              max="100"
              step="1"
              class="tb-input"
            />
          </label>
          <label class="tb-field">
            Points per period
            <input
              v-model.number="burstPointsPerPeriod"
              type="number"
              min="1"
              max="8"
              step="1"
              class="tb-input"
            />
          </label>
        </div>

        <label class="tb-field">
          Time-frequency resolution
          <select
            v-model="burstResolution"
            class="tb-input"
            title="How wide the analysis wavelet is. Preferring frequency separates resonances that sit close together, at the cost of taking twice as many periods to open up — so the first cycles of every decay are the analysis rather than the DUT."
          >
            <option v-for="option in BURST_RESOLUTIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </label>
      </template>
    </template>


    <div v-if="!isDecayView && (view !== 'Impulse' || normalisedImpulse)" class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Top (dB)
        <input
          v-model.number="yTopDb"
          type="number"
          step="1"
          class="tb-input"
        />
      </label>
      <label class="tb-field">
        Bottom (dB)
        <input
          v-model.number="yBottomDb"
          type="number"
          step="1"
          class="tb-input"
        />
      </label>
    </div>
  </CollapsibleSection>

  <CollapsibleSection title="Measurement">
    <p v-if="!status" class="text-[11px] text-neutral-500 dark:text-neutral-400">Nothing measured yet.</p>
    <template v-else>
      <!-- Read off the status, not the config form: this describes the data on screen, which after a
           settings change is not what the panel currently holds. -->
      <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[11px]">
        <dt class="text-neutral-500 dark:text-neutral-400">Reference</dt>
        <dd>{{ referenceLabel }}</dd>
        <dt class="text-neutral-500 dark:text-neutral-400">Gate</dt>
        <dd>−{{ status.gatePreMs }} / +{{ status.gatePostMs }} ms</dd>
        <dt class="text-neutral-500 dark:text-neutral-400">Resolved above</dt>
        <dd>{{ gateResolutionHz.toFixed(0) }} Hz</dd>
        <dt class="text-neutral-500 dark:text-neutral-400">Records</dt>
        <dd>{{ status.averagesTaken }} of {{ status.averageCount }}</dd>
        <dt class="text-neutral-500 dark:text-neutral-400">Generator</dt>
        <dd>{{ status.generatorAmpDbv.toFixed(2) }} dBV</dd>
        <template v-if="status.latencySamples !== null">
          <dt class="text-neutral-500 dark:text-neutral-400">DUT delay</dt>
          <dd>{{ status.latencySamples.toFixed(1) }} samples</dd>
        </template>
      </dl>
      <p v-if="running" class="text-[11px] text-neutral-500 dark:text-neutral-400">{{ status.phase }}…</p>
      <p v-if="status.error" class="text-[11px] text-red-600 dark:text-red-400">{{ status.error }}</p>
    </template>
  </CollapsibleSection>

  <!-- The notes of whichever chart is on screen. `view`'s values are the annotation view ids, so
       this follows the Impulse/Response/Harmonics/CSD/burst switch with no mapping -- and on the
       two decay surfaces, which carry no notes, it simply lists none. -->
  <AnnotationsPanel :view="view" />
</template>

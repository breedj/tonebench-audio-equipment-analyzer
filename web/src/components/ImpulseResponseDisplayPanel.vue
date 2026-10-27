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
import { clampNumberField } from '../numberField'
import {
  CSD_FFT_LENGTHS,
  DECAY_DEPTH_PERCENT,
  DECAY_ROTATION_DEG,
  DEFAULT_CSD_ANALYSIS,
  csdAchievedSpanMs,
  csdSliceStepSamples,
  describeCsdFftLength,
  PALETTE_NAMES,
} from '../measurements/decay'
import { impulseReferenceLabel } from '../measurements/impulseReadings'
import { chooseDelayUnit, delayFitStepUs, delayValuesOf, unitToUs, usToUnit } from '../response/delayAxis'
import {
  DECAY_AXIS_LENGTH,
  DECAY_LIGHT_AZIMUTH,
  DECAY_LIGHT_ELEVATION,
  DECAY_VIEW_MAGNIFICATION,
  DECAY_VIEW_MOVE_X,
  DECAY_VIEW_MOVE_Y,
  decayViewDefaults,
} from '../measurements/decay3d'
import { harmonicsUnavailableNote } from '../measurements/harmonicsAvailability'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * Whether the GPU surface is drawing the decay charts rather than the Canvas 2D one.
 *
 * <p>Two of the controls below are the 2D painter's own and reach nothing on the lit mesh: the
 * fill between the curves, and the colour of the curves themselves — a surface has no gaps to
 * fill and no separate curves to colour. They are hidden rather than disabled, because a disabled
 * control invites the reader to work out how to enable it, and here there is nothing to enable.
 * Everything else in the section is shared: Rotation and Tilt move the camera, Palette is the
 * height ramp, and the mode picks which surface is drawn.</p>
 */
const props = defineProps<{ gpuSurface?: boolean }>()

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
const decayFilled = defineModel<boolean>('decayFilled', { required: true })
const decayRotationDeg = defineModel<number>('decayRotationDeg', { required: true })
const decayMoveX = defineModel<number>('decayMoveX', { required: true })
const decayMoveY = defineModel<number>('decayMoveY', { required: true })
const decayMagnification = defineModel<number>('decayMagnification', { required: true })
const decayLightAzimuthDeg = defineModel<number>('decayLightAzimuthDeg', { required: true })
const decayLightElevationDeg = defineModel<number>('decayLightElevationDeg', { required: true })
const decayAxisLengthX = defineModel<number>('decayAxisLengthX', { required: true })
const decayAxisLengthY = defineModel<number>('decayAxisLengthY', { required: true })
const decayAxisLengthZ = defineModel<number>('decayAxisLengthZ', { required: true })
const decayDepthPercent = defineModel<number>('decayDepthPercent', { required: true })
const decayRangeDb = defineModel<number>('decayRangeDb', { required: true })
const csdFftLength = defineModel<number>('csdFftLength', { required: true })
const csdSpanMs = defineModel<number>('csdSpanMs', { required: true })
const csdSlices = defineModel<number>('csdSlices', { required: true })
const csdStartBeforePeakMs = defineModel<number>('csdStartBeforePeakMs', { required: true })
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

/** Checked history items carrying harmonic curves, which Autofit and its note both count. */
const overlayHarmonics = computed(() => overlays.value.filter((o) => o.visible && o.harmonicCurves?.length))

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

/**
 * What the across and depth edges of the box are carrying, for the two axis-length sliders that
 * move them. Which is which swaps with the projection — Waterfall (f) runs frequency across and
 * recedes in time, Waterfall (p) the other way about — so a fixed pair of labels would be wrong
 * half the time, and "X" and "Z" say nothing to anyone.
 */
const acrossAxisLabel = computed(() => (decayMode.value === 'WaterfallPeriods' ? 'Time' : 'Frequency'))
const depthAxisLabel = computed(() => (decayMode.value === 'WaterfallPeriods' ? 'Frequency' : 'Time'))

/**
 * Puts every one of the ten view sliders back where a new measurement starts, from the one function
 * that also seeds a new measurement — see `decayViewDefaults`, which is where the reasoning for
 * including Rotation and Tilt is.
 *
 * <p>It writes the models rather than touching the chart, so it marks the measurement dirty like
 * any other slider, is saved like any other setting, and the chart follows because it follows them.</p>
 */
function resetView() {
  const d = decayViewDefaults()
  decayRotationDeg.value = d.decayRotationDeg
  decayDepthPercent.value = d.decayDepthPercent
  decayMoveX.value = d.decayMoveX
  decayMoveY.value = d.decayMoveY
  decayMagnification.value = d.decayMagnification
  decayLightAzimuthDeg.value = d.decayLightAzimuthDeg
  decayLightElevationDeg.value = d.decayLightElevationDeg
  decayAxisLengthX.value = d.decayAxisLengthX
  decayAxisLengthY.value = d.decayAxisLengthY
  decayAxisLengthZ.value = d.decayAxisLengthZ
}

const resetViewTitle =
  'Puts the camera, the box and the light back where a new measurement starts — these eight sliders, and Rotation and Tilt above them.'

const CSD_WINDOW_MODES: { value: CsdWindowModeName; label: string }[] = [
  { value: 'Apodizing', label: 'CSD, apodizing window' },
  { value: 'Hann', label: 'STF, Hann window' },
]

/**
 * Labelled by bandwidth rather than by which end of the trade it favours, because that is the thing
 * two programs can be matched on: ARTA's "prefer time" is 1/3 octave and its "prefer frequency" is
 * 1/6, where this app's two old names are 2/3 and 1/3. Naming the octave fraction makes the rungs
 * line up without anyone having to know that.
 */
const BURST_RESOLUTIONS: { value: BurstDecayResolutionName; label: string }[] = [
  { value: 'PreferTime', label: '2/3 octave — fastest decay' },
  { value: 'PreferFrequency', label: '1/3 octave' },
  { value: 'SixthOctave', label: '1/6 octave — finest detail' },
]

const CHART_MODES: { value: DecayChartMode; label: string }[] = [
  { value: 'Waterfall', label: 'Waterfall (f)' },
  { value: 'WaterfallPeriods', label: 'Waterfall (p)' },
  { value: 'Sonogram', label: 'Sonogram' },
]

const sampleRateHz = computed(() => status.value?.sampleRateHz ?? 0)

/** What the transform length means here — the two numbers the setting is actually about. */
const csdWindowLabel = computed(() => describeCsdFftLength(csdFftLength.value, sampleRateHz.value))

/**
 * What the span asked for really comes to, and the shift it took to get there. The shift is a whole
 * number of samples, so 2.5 ms at 192 kHz is 8 samples and really 2.46 ms — printing the figure the
 * chart will actually draw is the same courtesy the transform length gets beside it.
 */
const csdSpanLabel = computed(() => {
  const rate = sampleRateHz.value
  if (!(rate > 0)) return `${csdSlices.value} slices`

  const analysis = { ...DEFAULT_CSD_ANALYSIS, spanMs: csdSpanMs.value, slices: csdSlices.value }
  const span = csdAchievedSpanMs(analysis, rate)
  const step = csdSliceStepSamples(analysis, rate)
  const head = csdStartBeforePeakMs.value > 0 ? `, from −${csdStartBeforePeakMs.value.toFixed(2)} ms` : ''
  return `${span.toFixed(2)} ms, ${step.toFixed(step < 10 ? 1 : 0)} samples per slice${head}`
})

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

/**
 * The highest frequency worth asking for: the measurement's own Nyquist, which in this app's terms
 * is the frequency span. Past it there is nothing measured — the decay analyses clamp their band to
 * it server-side, and the chart draws the grid the server answers with — so the control stops there
 * rather than offering a stretch of axis nothing can fill.
 *
 * <p>The <em>spinner</em> stops there; a value already stored above it is left alone and simply
 * means "as far as the measurement goes". Clamping the stored number instead would mark a
 * measurement dirty merely for having been opened at a narrower span.</p>
 */
const freqCeilingHz = computed(() => (sampleRateHz.value > 0 ? Math.round(sampleRateHz.value / 2) : undefined))

/*
 * The three fields whose floor is kept here rather than in a `min` attribute, so that their
 * spinners step on round numbers — see clampNumberField, which is where the reason lives. Their
 * ceilings are still attributes: `max` is no part of HTML's step base.
 */
function clampFreqFrom() {
  freqFromHz.value = clampNumberField(freqFromHz.value, 1, freqCeilingHz.value)
}

function clampFreqTo() {
  freqToHz.value = clampNumberField(freqToHz.value, 2, freqCeilingHz.value)
}

function clampCsdSpan() {
  csdSpanMs.value = clampNumberField(csdSpanMs.value, 0.05, 500)
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

/**
 * The line under a disabled Autofit. Only the Harmonics view has a reason that can be named —
 * everywhere else the button is dead because nothing has been measured yet, which the chart's own
 * empty state already says and a second line beside it would only repeat.
 */
const autofitBlockedNote = computed(() => (
  view.value === 'Harmonics'
    ? harmonicsUnavailableNote(status.value?.stimulus, harmonics.value.length > 0 || overlayHarmonics.value.length > 0)
    : null
))

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
  <!-- Why that button is dead, where there is a reason worth giving. Beside the control rather
       than only under the chart: a disabled button with nothing near it reads as a fault, and the
       rail is where somebody goes looking when a view draws nothing. Same wording as the chart's
       own line, from one module. -->
  <p
    v-if="!canAutofit && autofitBlockedNote"
    class="text-[11px] text-neutral-500 dark:text-neutral-400"
  >
    {{ autofitBlockedNote }}
  </p>

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
            :max="freqCeilingHz"
            step="10"
            class="tb-input"
            @change="clampFreqFrom"
          />
        </label>
        <label class="tb-field">
          To (Hz)
          <input
            v-model.number="freqToHz"
            type="number"
            :max="freqCeilingHz"
            step="1000"
            class="tb-input"
            @change="clampFreqTo"
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

      <template v-if="decayMode !== 'Sonogram'">
        <!-- Both of these are the Canvas 2D painter's and reach nothing on the GPU surface; see
             the `gpuSurface` prop. -->
        <label
          v-if="!props.gpuSurface"
          class="tb-check-row"
          title="Fill the surface between the curves instead of leaving the page between them, and draw the curves themselves in white. The level is then carried by the fill, so the curves read the shape."
        >
          <input v-model="decayFilled" type="checkbox" class="tb-check" />
          Filled surface
        </label>

        <!-- Hidden rather than disabled under a filled surface: it decides the colour of the
             curves, and there the fill has already decided it. -->
        <label
          v-if="!props.gpuSurface && !decayFilled"
          class="tb-check-row"
          title="Colour each curve by level. Off draws them all in one colour, which reads the shape rather than the level."
        >
          <input v-model="decayColoredCurves" type="checkbox" class="tb-check" />
          Coloured curves
        </label>

        <!-- Two ends of one thing — where the depth axis points and how far it runs — so they are
             read and set together. Sliders rather than boxes: neither has a figure anybody knows in
             advance, they are turned until the picture looks right, and the chart follows the drag.
             Stacked rather than side by side, because a slider needs the width to be worth
             dragging. -->
        <label class="tb-field">
          <span class="tb-field-row">
            <span>Rotation</span>
            <span class="tb-field-value">{{ decayRotationDeg }}°</span>
          </span>
          <input
            v-model.number="decayRotationDeg"
            type="range"
            :min="DECAY_ROTATION_DEG.min"
            :max="DECAY_ROTATION_DEG.max"
            step="1"
            class="tb-range"
            title="Which way the stack recedes, in degrees from straight down. Positive leans it to the left, as ARTA draws it; zero stacks the curves straight down the chart, and a negative angle sends them to the right."
          />
        </label>
        <label class="tb-field">
          <span class="tb-field-row">
            <span>Tilt</span>
            <span class="tb-field-value">{{ decayDepthPercent }}%</span>
          </span>
          <input
            v-model.number="decayDepthPercent"
            type="range"
            :min="DECAY_DEPTH_PERCENT.min"
            :max="DECAY_DEPTH_PERCENT.max"
            step="1"
            class="tb-range"
            title="How far the view is tilted back. On the flat projection that is how much of the chart's height the depth axis takes, so a deep-looking surface is one with a squashed dB scale; on the 3D surface it raises the camera."
          />
        </label>

        <!-- The five the vendor's own demo offers and this chart did not. In a section of their
             own and collapsed, because none of them is touched on an ordinary visit: the defaults
             are the view the chart opens on, and these are for matching a picture or lighting an
             awkward surface. Only on the GPU chart — the flat projection has no camera and no
             light, so there would be nothing for any of them to move. -->
        <CollapsibleSection
          v-if="props.gpuSurface"
          title="Surface view"
          size="xs"
          icon="sliders"
          :default-open="false"
        >
          <label class="tb-field">
            <span class="tb-field-row">
              <span>Move horizontally</span>
              <span class="tb-field-value">{{ decayMoveX.toFixed(1) }}</span>
            </span>
            <input
              v-model.number="decayMoveX"
              type="range"
              :min="DECAY_VIEW_MOVE_X.min"
              :max="DECAY_VIEW_MOVE_X.max"
              step="0.1"
              class="tb-range"
              title="Slides the surface across the plot. The engine's own translation, which is a fine adjustment here rather than a coarse one — the whole range is a few tens of pixels."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>Move vertically</span>
              <span class="tb-field-value">{{ decayMoveY.toFixed(1) }}</span>
            </span>
            <input
              v-model.number="decayMoveY"
              type="range"
              :min="DECAY_VIEW_MOVE_Y.min"
              :max="DECAY_VIEW_MOVE_Y.max"
              step="0.1"
              class="tb-range"
              title="Slides the surface up and down the plot."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>Magnification</span>
              <span class="tb-field-value">{{ decayMagnification }}%</span>
            </span>
            <input
              v-model.number="decayMagnification"
              type="range"
              :min="DECAY_VIEW_MAGNIFICATION.min"
              :max="DECAY_VIEW_MAGNIFICATION.max"
              step="5"
              class="tb-range"
              title="How large the surface is drawn, as a percentage of the framing the plot's own shape gives it. 100% is that framing; past it the surface can run off the plot, which is what the two Move sliders are then for."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>Rotate light horizontally</span>
              <span class="tb-field-value">{{ decayLightAzimuthDeg }}°</span>
            </span>
            <input
              v-model.number="decayLightAzimuthDeg"
              type="range"
              :min="DECAY_LIGHT_AZIMUTH.min"
              :max="DECAY_LIGHT_AZIMUTH.max"
              step="1"
              class="tb-range"
              title="Where the light sits around the box. The default lights the side the camera is on; turning it right round puts the surface in its own shadow."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>{{ acrossAxisLabel }} axis length</span>
              <span class="tb-field-value">{{ decayAxisLengthX.toFixed(1) }}</span>
            </span>
            <input
              v-model.number="decayAxisLengthX"
              type="range"
              :min="DECAY_AXIS_LENGTH.min"
              :max="DECAY_AXIS_LENGTH.max"
              :step="DECAY_AXIS_LENGTH.step"
              class="tb-range"
              title="How long the box is drawn across. The three axes have no common unit, so there is no proportion to be right — stretch whichever one you are reading."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>Level axis length</span>
              <span class="tb-field-value">{{ decayAxisLengthY.toFixed(1) }}</span>
            </span>
            <input
              v-model.number="decayAxisLengthY"
              type="range"
              :min="DECAY_AXIS_LENGTH.min"
              :max="DECAY_AXIS_LENGTH.max"
              :step="DECAY_AXIS_LENGTH.step"
              class="tb-range"
              title="How tall the box is drawn. Raising it makes a shallow decay legible; it changes the shape of the picture and not the dB range, which is Range (dB)."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>{{ depthAxisLabel }} axis length</span>
              <span class="tb-field-value">{{ decayAxisLengthZ.toFixed(1) }}</span>
            </span>
            <input
              v-model.number="decayAxisLengthZ"
              type="range"
              :min="DECAY_AXIS_LENGTH.min"
              :max="DECAY_AXIS_LENGTH.max"
              :step="DECAY_AXIS_LENGTH.step"
              class="tb-range"
              title="How deep the box is drawn — how far the stack recedes. A longer one separates slices that overlap."
            />
          </label>

          <label class="tb-field">
            <span class="tb-field-row">
              <span>Rotate light vertically</span>
              <span class="tb-field-value">{{ decayLightElevationDeg }}°</span>
            </span>
            <input
              v-model.number="decayLightElevationDeg"
              type="range"
              :min="DECAY_LIGHT_ELEVATION.min"
              :max="DECAY_LIGHT_ELEVATION.max"
              step="1"
              class="tb-range"
              title="How high the light sits. The distance does not change with it, so this turns the light rather than dimming it — how dark the darkest colour reads is the palette's floor, not this."
            />
          </label>

          <!-- At the foot of the section rather than in its header: beside the chevron it would be
               hit by anyone reaching to collapse the panel, and ten sliders is a lot to put back by
               hand. It covers Rotation and Tilt too, although their sliders are above this section
               — they are the camera, and a reset that left the box at whatever angle the last turn
               gave it would not be handing back the view it promises. The title says so. -->
          <button type="button" class="btn-panel-accent w-full" :title="resetViewTitle" @click="resetView">
            Reset view
          </button>
        </CollapsibleSection>
      </template>

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
            :max="freqCeilingHz"
            step="10"
            class="tb-input"
            @change="clampFreqFrom"
          />
        </label>
        <label class="tb-field">
          To (Hz)
          <input
            v-model.number="freqToHz"
            type="number"
            :max="freqCeilingHz"
            step="1000"
            class="tb-input"
            @change="clampFreqTo"
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
            Decay span (ms)
            <input
              v-model.number="csdSpanMs"
              type="number"
              max="500"
              step="0.5"
              class="tb-input"
              @change="clampCsdSpan"
              title="How far past the impulse the surface reaches. In milliseconds rather than in samples, so it means the same measurement at every sample rate — the step between slices is worked out from it and printed below."
            />
          </label>
          <label class="tb-field">
            Slices
            <input
              v-model.number="csdSlices"
              type="number"
              min="2"
              max="200"
              step="1"
              class="tb-input"
              title="How many spectra to cut that span into. More is a finer picture of the decay and a denser chart; ARTA's own advice is that too coarse a step obscures high-frequency resonances."
            />
          </label>
        </div>

        <div class="grid grid-cols-2 gap-2.5">
          <label class="tb-field">
            Start before peak (ms)
            <input
              v-model.number="csdStartBeforePeakMs"
              type="number"
              min="0"
              max="50"
              step="0.5"
              class="tb-input"
              title="How far ahead of the impulse to begin, so the first slices show the run-up to it rather than the decay away from it. Zero starts at the impulse. ARTA's time zero is its cursor, which its manual has you set some way before the peak — set the same head start here to put the two surfaces side by side. The time axis goes negative rather than re-zeroing, so the impulse stays at 0 ms."
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
            title="How wide the analysis wavelet is, as a fraction of an octave. A narrower filter separates resonances that sit close together and resolves more detail at the top of the band, at the cost of lasting more periods — so the first cycles of every decay are the analysis opening up rather than the DUT. ARTA calls 1/3 octave prefer time and 1/6 octave prefer frequency, so match it by the fraction rather than by the words."
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

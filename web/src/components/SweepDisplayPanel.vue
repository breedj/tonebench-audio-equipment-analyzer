<script setup lang="ts">
/**
 * Display options for the sweep chart, on either axis. Everything here is a pure view preference —
 * nothing reaches the device or re-runs a sweep, so all of it stays enabled while one is running.
 *
 * Every trace has its own checkbox, and this panel is the only place they can be switched: the
 * chart's legend is read-only, so the checkbox and the chart can never disagree. Same arrangement
 * as the previous app's Graph data box.
 *
 * The `xAxis` model is what tells the two screens apart. It is 'frequency' on the THD-vs-frequency
 * screen, where it never changes and its selector is hidden; on the amplitude screen it is one of
 * the three DUT-referred choices and the user picks between them here.
 *
 * Multi-root: the two axis windows are their own sections ahead of Display, laid out the way the
 * FFT screen's dBV range / Frequency range are (ChartDisplayPanel.vue) — label over a full-width
 * field, Autofit under — so the sections flow into MeasurementLayout's aside grid one by one like
 * every other display panel's.
 */

import { computed, watch } from 'vue'
import { useImdSweep } from '../composables/useImdSweep'
import { useSweep } from '../composables/useSweep'
import { useMeasurements } from '../composables/useMeasurements'
import { sweepDistortionDb, sweepTraceLabel, sweepTraceValue, type SweepTraceFamily, type SweepTraceKey } from '../sweep/points'
import { AMPLITUDE_X_AXES, sweepXLabel, sweepXUnit, sweepXValue, type SweepXAxis } from '../sweep/xAxis'
import type { ThdMethodName, ThdSweepPoint } from '../api/client'
import CollapsibleSection from './CollapsibleSection.vue'
import SegmentedToggle from './SegmentedToggle.vue'
import { useAppTheme } from '../composables/useAppTheme'
import { sweepTraceColor, type SweepTraceColorKey } from '../sweep/traceColors'

const props = defineProps<{
  /** Which sweep this panel serves — decides the trace checkbox names (THD/D2.. or IMD/IM2..) and which store Autofit reads. Defaults to the THD family. */
  traceFamily?: SweepTraceFamily
}>()

const family = computed(() => props.traceFamily ?? 'thd')

/** The checkbox names: THD/D2… or IMD/IM2…, never the normalized "n" prefix — the checkbox is the trace, whichever way the run is read. */
const t = (key: SweepTraceKey) => sweepTraceLabel(key, 'Conventional', family.value)

const unit = defineModel<'percent' | 'db'>('unit', { required: true })
const showMagnitude = defineModel<boolean>('showMagnitude', { required: true })
const showThd = defineModel<boolean>('showThd', { required: true })
const showD2 = defineModel<boolean>('showD2', { required: true })
const showD3 = defineModel<boolean>('showD3', { required: true })
const showD4 = defineModel<boolean>('showD4', { required: true })
const showD5 = defineModel<boolean>('showD5', { required: true })
const showD6Plus = defineModel<boolean>('showD6Plus', { required: true })
const showNoiseFloor = defineModel<boolean>('showNoiseFloor', { required: true })
const showLeft = defineModel<boolean>('showLeft', { required: true })
const showRight = defineModel<boolean>('showRight', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

/**
 * Axis bounds. Fixed lists rather than free-text inputs, matching the previous app's graph
 * settings: the y axis is logarithmic over up to seven decades, so the only useful stops are the
 * decade boundaries, and a typed-in 0.0003 would just draw the gridlines somewhere unhelpful.
 *
 * Both y values are percentages even when the chart is showing dB — the chart converts. Keeping
 * one unit here means switching %/dB cannot silently move the window.
 */
const yMaxPercent = defineModel<number>('yMaxPercent', { required: true })
const yMinPercent = defineModel<number>('yMinPercent', { required: true })
const yTopDb = defineModel<number>('yTopDb', { required: true })
const yBottomDb = defineModel<number>('yBottomDb', { required: true })
/** X bounds, in whatever unit `xAxis` is in — hertz, volts or watts. */
const xFrom = defineModel<number>('xFrom', { required: true })
const xTo = defineModel<number>('xTo', { required: true })
const xAxis = defineModel<SweepXAxis>('xAxis', { required: true })

/** True on the THD-vs-frequency screen — the one axis with a fixed set of useful bounds. */
const isFrequencyAxis = computed(() => xAxis.value === 'frequency')

const Y_MAX_OPTIONS = [100, 10, 1]
const Y_MIN_OPTIONS = [0.1, 0.01, 0.001, 0.0001, 0.00001, 0.000001]
const X_FROM_OPTIONS = [5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

/**
 * Smallest x bound the volt/watt axes accept. They are typed rather than picked from a list — the
 * useful window depends entirely on the DUT (a headphone amp and a 100 W power amp share no
 * sensible preset, and output power spans microwatts to tens of watts) — so the only rule that has
 * to be enforced is the one the log scale itself imposes: strictly positive.
 */
const X_MIN_VALUE = 1e-9

/** 0.00001 rather than 1e-5 — toString() switches to exponent notation at 1e-7, but 1e-6 is fine. */
function percentLabel(v: number): string {
  return v.toString()
}

/**
 * dB bounds are typed, not picked from a list: the useful window depends on the DUT (a 26 dB
 * amplifier needs a top the percent list cannot express at all) and the decade stops that make
 * sense on a log percent axis mean nothing on a linear dB one.
 */
const DB_MIN = -200
const DB_MAX = 100
const DB_STEP = 10

/** Smallest window the axis may be squeezed to. Top == bottom is a zero-height scale, not a chart. */
const DB_MIN_SPAN = 10

/**
 * Reads the field, clamps it, and writes the clamped number back into both the model and the input.
 *
 * Deliberately `:value` + `@change` rather than `v-model.number` with a clamp afterwards. Two
 * reasons, and the first is a bug that version had: `defineModel` writes go out as an emit and come
 * back as a prop, so a handler running in the same tick still reads the *previous* value — clamping
 * against it wrote the old bound back over whatever had just been typed. Reading `event.target`
 * keeps it synchronous and self-consistent. The second is that `change` fires on blur, Enter and
 * every spinner click, but not on each keystroke, so the axis does not thrash while someone is
 * halfway through typing "-160".
 */
function commitDb(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current

  el.value = String(next)
  return next
}

// Each end is clamped against the other, so the pair can never cross or meet: the top stops
// DB_MIN_SPAN above the bottom, and the bottom the same distance below the top.
function onTopDbChange(event: Event) {
  yTopDb.value = commitDb(event, yTopDb.value, yBottomDb.value + DB_MIN_SPAN, DB_MAX)
}

function onBottomDbChange(event: Event) {
  yBottomDb.value = commitDb(event, yBottomDb.value, DB_MIN, yTopDb.value - DB_MIN_SPAN)
}

/**
 * Autofit for the Distortion range and Frequency range sections below. Both read straight off the
 * same sources ThdSweepChart itself plots from — the live sweep's points (useSweep) plus whatever
 * stored sweeps are checked on (useMeasurements' overlays) — rather than duplicating the chart's own
 * state, since both composables are already the shared singleton the chart reads from too.
 *
 * Unlike SpectrumChart's dBV Autofit (a free-typed field, so it can land on any number), the percent
 * axis here is a fixed dropdown — Y_MAX_OPTIONS/Y_MIN_OPTIONS/X_FROM_OPTIONS/X_TO_OPTIONS are the
 * only values the <select> can even show — so autofit has to snap to whichever preset actually
 * encloses the data, not compute an arbitrary bound. The dB axis has no such list (a free-typed
 * field, step 10), so it rounds out to the nearest 10 dB instead, the same convention as Spectrum's.
 */
// The live run Autofit fits: the THD store's or the IMD store's, by family. Both keep their points
// in the chart's shape (imd/points.ts), so everything below reads them alike.
const thdSweep = useSweep()
const imdSweep = useImdSweep()
const sweepPoints = computed(() => (family.value === 'imd' ? imdSweep.points.value : thdSweep.points.value))
const running = computed(() => (family.value === 'imd' ? imdSweep.running.value : thdSweep.running.value))
const thdMethod = computed<ThdMethodName>(() => (family.value === 'imd' ? 'Conventional' : thdSweep.thdMethod.value))
const requestedEndFreqHz = computed(() => (family.value === 'imd' ? null : thdSweep.requestedEndFreqHz.value))
const { overlays } = useMeasurements()

/**
 * One run's points with what Autofit needs to know about them: which figures the run is about
 * (a normalized run's nTHD is not its THD, and fitting the wrong one puts the trace off-screen),
 * and how far it was asked to go — a normalized sweep measures reference points past its end for
 * the harmonics' sake, and fitting the x axis to those would open the window to 100 kHz for a
 * sweep that was asked for 20.
 */
interface SweepRun {
  points: ThdSweepPoint[]
  method: ThdMethodName
  endFreqHz: number | null
}

/** The smallest option that is still >= value, or the largest option if value exceeds all of them. */
function smallestAtLeast(options: number[], value: number): number {
  const atLeast = options.filter((o) => o >= value)
  return atLeast.length > 0 ? Math.min(...atLeast) : Math.max(...options)
}

/** The largest option that is still <= value, or the smallest option if value is below all of them. */
function largestAtMost(options: number[], value: number): number {
  const atMost = options.filter((o) => o <= value)
  return atMost.length > 0 ? Math.max(...atMost) : Math.min(...options)
}

/** Stored sweeps actually drawable right now — visible, and carrying points — same filter ThdSweepChart applies to its own overlays prop. */
function activeOverlaySweeps(): SweepRun[] {
  return overlays.value
    .filter((o) => o.visible && o.sweepPoints?.length)
    .map((o) => ({ points: o.sweepPoints!, method: o.sweepMethod ?? 'Conventional', endFreqHz: o.sweepEndFreqHz ?? null }))
}

/** Every run on screen, the live sweep first. */
function activeRuns(): SweepRun[] {
  return [
    { points: sweepPoints.value, method: thdMethod.value, endFreqHz: requestedEndFreqHz.value },
    ...activeOverlaySweeps(),
  ]
}

function activeChannels(): number[] {
  const channels: number[] = []
  if (showLeft.value) channels.push(0)
  if (showRight.value) channels.push(1)
  return channels
}

/** Highest/lowest value across every currently-checked trace and channel, live sweep plus visible overlays. */
function computeDistortionExtent(): { min: number; max: number } | null {
  const keys: SweepTraceKey[] = []
  if (unit.value === 'db' && showMagnitude.value) keys.push('magnitude')
  if (showThd.value) keys.push('thd')
  if (showD2.value) keys.push('d2')
  if (showD3.value) keys.push('d3')
  if (showD4.value) keys.push('d4')
  if (showD5.value) keys.push('d5')
  if (showD6Plus.value) keys.push('d6plus')
  if (showNoiseFloor.value) keys.push('noise')

  const channels = activeChannels()
  if (keys.length === 0 || channels.length === 0) return null

  let min = Infinity
  let max = -Infinity
  function consider(run: SweepRun) {
    for (const p of run.points) {
      if (!channels.includes(p.channel)) continue
      for (const key of keys) {
        // The same figure the chart plots for this run — sweepTraceValue is the one place that
        // decides which of a point's two sets of figures its run is about.
        const raw = sweepTraceValue(p, key, run.method)
        if (!Number.isFinite(raw)) continue
        // A non-positive percentage has no position on the log percent axis (see ThdSweepChart's
        // toY) and would drag the dB axis to -Infinity, so it is excluded from either extent — the
        // same gap-not-plunge rule the chart itself draws with.
        if (key !== 'magnitude' && !(raw > 0)) continue
        // Gain-compensated on the dB axis, exactly as the chart draws it (sweepDistortionDb) —
        // Autofit has to fit what is on screen, and the compensation moves a trace by the DUT's
        // whole gain. A point with no stored gain yields NaN and is skipped, the chart's gap.
        const v = unit.value === 'db' && key !== 'magnitude' ? sweepDistortionDb(raw, p.gainDb) : raw
        if (!Number.isFinite(v)) continue
        if (v > max) max = v
        if (v < min) min = v
      }
    }
  }
  for (const run of activeRuns()) consider(run)

  return Number.isFinite(min) && Number.isFinite(max) ? { min, max } : null
}

function requestAutofitDistortion() {
  const extent = computeDistortionExtent()
  if (!extent) return

  if (unit.value === 'db') {
    const top = Math.min(DB_MAX, Math.ceil(extent.max / DB_STEP) * DB_STEP)
    const bottom = Math.max(DB_MIN, Math.floor(extent.min / DB_STEP) * DB_STEP)
    yTopDb.value = top - bottom >= DB_MIN_SPAN ? top : Math.min(DB_MAX, bottom + DB_MIN_SPAN)
    yBottomDb.value = bottom
  } else {
    yMaxPercent.value = smallestAtLeast(Y_MAX_OPTIONS, extent.max)
    yMinPercent.value = largestAtMost(Y_MIN_OPTIONS, extent.min)
  }
}

/** The measured span along whichever axis is selected — the same sweepXValue the chart plots with. */
function computeXExtent(): { min: number; max: number } | null {
  const channels = activeChannels()
  if (channels.length === 0) return null

  let min = Infinity
  let max = -Infinity
  function consider(run: SweepRun) {
    for (const p of run.points) {
      if (!channels.includes(p.channel)) continue
      // The band that was asked for, not the reference points a normalized sweep measured above
      // it -- those are still drawn if the window is widened, but they are not what the run is
      // about. Only on the frequency axis: the level axes plot the same points against a level.
      if (isFrequencyAxis.value && run.endFreqHz !== null && p.freqHz > run.endFreqHz * 1.001) continue
      let x = sweepXValue(p, xAxis.value)
      if (!(x > 0) || !Number.isFinite(x)) continue
      // The top point snaps to the nearest coherent bin, which can land a few hertz *above* the
      // requested end (20 kHz becomes 20015.6 Hz), and the preset just above that is 50 kHz. A
      // sweep asked for 20 kHz should fit to 20 kHz.
      if (isFrequencyAxis.value && run.endFreqHz !== null) x = Math.min(x, run.endFreqHz)
      if (x > max) max = x
      if (x < min) min = x
    }
  }
  for (const run of activeRuns()) consider(run)

  return Number.isFinite(min) && Number.isFinite(max) ? { min, max } : null
}

/** Down to the next 1/2/5 step below `v` — the same mantissa ladder the log axis ticks on. */
function roundOutDown(v: number): number {
  const decade = 10 ** Math.floor(Math.log10(v))
  const mantissa = v / decade
  const step = mantissa >= 5 ? 5 : mantissa >= 2 ? 2 : 1
  return step * decade
}

/** Up to the next 1/2/5 step above `v`. */
function roundOutUp(v: number): number {
  const decade = 10 ** Math.floor(Math.log10(v))
  const mantissa = v / decade
  const step = mantissa > 5 ? 10 : mantissa > 2 ? 5 : mantissa > 1 ? 2 : 1
  return step * decade
}

function requestAutofitX() {
  const extent = computeXExtent()
  if (!extent) return

  if (isFrequencyAxis.value) {
    xFrom.value = largestAtMost(X_FROM_OPTIONS, extent.min)
    xTo.value = smallestAtLeast(X_TO_OPTIONS, extent.max)
    return
  }

  const from = Math.max(X_MIN_VALUE, roundOutDown(extent.min))
  const to = roundOutUp(extent.max)
  xFrom.value = from
  xTo.value = to > from ? to : from * 10
}

/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30). The frequency window is fitted
 * first: every level fit below is scoped to it, so fitting the other way round sizes the axis to
 * whatever the old window happened to hold.
 */
function autofitAll() {
  requestAutofitX()
  requestAutofitDistortion()
}

// "Autofit when measurement done": fires on the running->not-running transition, whether the sweep
// finished, was cancelled, or was stopped manually -- same signal the RUN/STOP button itself uses.
watch(running, (isRunning, wasRunning) => {
  if (wasRunning && !isRunning && autofitOnDone.value) {
    autofitAll()
  }
})

/**
 * Typed x bounds, clamped on commit. Same `:value` + `@change` shape as the dB fields above, and for
 * the same two reasons — a defineModel write comes back as a prop next tick, so a clamp written
 * against the model reads the previous value; and `change` fires on blur/Enter rather than per
 * keystroke, so the axis does not thrash mid-typing.
 */
function commitX(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && typed > 0 ? Math.min(hi, Math.max(lo, typed)) : current

  el.value = String(next)
  return next
}

// Neither end may cross the other: a log scale needs from < to, and equal ends are a zero-width
// axis rather than a chart. A decade of separation is the smallest window worth drawing here.
function onXFromChange(event: Event) {
  xFrom.value = commitX(event, xFrom.value, X_MIN_VALUE, xTo.value / 1.5)
}

function onXToChange(event: Event) {
  xTo.value = commitX(event, xTo.value, xFrom.value * 1.5, Number.MAX_VALUE)
}

// The colour each Graph data row shows beside its checkbox — the chart's own, so the rail names the
// line it is switching rather than leaving the reader to match them by eye.
const { isDark } = useAppTheme()
function swatch(key: SweepTraceColorKey): string {
  return sweepTraceColor(key, isDark.value)
}
</script>

<template>
  <CollapsibleSection
    :title="unit === 'db' ? 'dB range' : 'Distortion range'"
    :suffix="unit === 'db' ? undefined : '(%)'"
  >
    <template v-if="unit === 'db'">
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          Top
          <input
            :value="yTopDb"
            type="number"
            :min="yBottomDb + DB_MIN_SPAN"
            :max="DB_MAX"
            :step="DB_STEP"
            class="tb-input"
            @change="onTopDbChange"
          />
        </label>
        <label class="tb-field">
          Bottom
          <input
            :value="yBottomDb"
            type="number"
            :min="DB_MIN"
            :max="yTopDb - DB_MIN_SPAN"
            :step="DB_STEP"
            class="tb-input"
            @change="onBottomDbChange"
          />
        </label>
      </div>
    </template>
    <template v-else>
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          Top
          <select v-model.number="yMaxPercent" class="tb-input">
            <option v-for="v in Y_MAX_OPTIONS" :key="v" :value="v">{{ percentLabel(v) }}</option>
          </select>
        </label>
        <label class="tb-field">
          Bottom
          <select v-model.number="yMinPercent" class="tb-input">
            <option v-for="v in Y_MIN_OPTIONS" :key="v" :value="v">{{ percentLabel(v) }}</option>
          </select>
        </label>
      </div>
    </template>
    <!-- The chart's one Autofit, in the first section rather than a scroll below the fields it
         overwrites. -->
    <button
      type="button"
      class="btn-panel-accent"
      title="Fits both axes to every currently-checked trace: the sweep axis to the measured span, and the distortion axis to the highest and lowest value in it. Percent snaps to the enclosing dropdown preset; dB rounds out to the nearest 10 dB."
      @click="autofitAll"
    >
      Autofit
    </button>
  </CollapsibleSection>

  <CollapsibleSection :title="`${sweepXLabel(xAxis)} range`" :suffix="`(${sweepXUnit(xAxis)})`">
    <!-- Only the amplitude screen has a choice to make: output voltage and output power are what
         the DUT delivered, input voltage is what it was driven with. -->
    <label v-if="!isFrequencyAxis" class="tb-field">
      X axis
      <select v-model="xAxis" class="tb-input">
        <option v-for="a in AMPLITUDE_X_AXES" :key="a.value" :value="a.value">{{ a.label }}</option>
      </select>
    </label>
    <template v-if="isFrequencyAxis">
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          From
          <select v-model.number="xFrom" class="tb-input">
            <option v-for="v in X_FROM_OPTIONS" :key="v" :value="v">{{ v }}</option>
          </select>
        </label>
        <label class="tb-field">
          To
          <select v-model.number="xTo" class="tb-input">
            <option v-for="v in X_TO_OPTIONS" :key="v" :value="v">{{ v }}</option>
          </select>
        </label>
      </div>
    </template>
    <template v-else>
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          From
          <input :value="xFrom" type="number" step="any" :min="X_MIN_VALUE" class="tb-input" @change="onXFromChange" />
        </label>
        <label class="tb-field">
          To
          <input :value="xTo" type="number" step="any" :min="X_MIN_VALUE" class="tb-input" @change="onXToChange" />
        </label>
      </div>
    </template>
  </CollapsibleSection>

  <CollapsibleSection title="Display">
    <span class="tb-group">Units</span>
    <!-- A toggle rather than a select: two mutually exclusive options that people flip between
         constantly while reading a trace. -->
    <SegmentedToggle
      v-model="unit"
      group-label="Units"
      :options="[
        { value: 'percent', label: '%' },
        { value: 'db', label: 'dB' },
      ]"
    />

    <label class="tb-check-row">
      <input v-model="autofitOnDone" type="checkbox" class="tb-check" /> Autofit when measurement done
    </label>

    <span class="tb-group pt-1">Channels</span>
    <div class="flex gap-5">
      <label class="tb-check-row">
        <input v-model="showLeft" type="checkbox" class="tb-check" />
        Left
      </label>
      <label class="tb-check-row">
        <input v-model="showRight" type="checkbox" class="tb-check" />
        Right
      </label>
    </div>

    <span class="tb-group pt-1">Graph data</span>
    <!-- Each row carries the colour its line is drawn in, from sweep/traceColors.ts — the one thing
         a list of seven checkboxes could not say before, and the thing a reader of the chart most
         needs from it. -->
    <!-- Only on the dB axis: magnitude is a gain in dB, which has no position on a percent
         scale. The previous app greys the same checkbox out on its D% graph. -->
    <label
      v-if="unit === 'db'"
      class="tb-check-row"
      title="The measured fundamental relative to the generator setpoint — the DUT's frequency response, riding along with the sweep."
    >
      <input v-model="showMagnitude" type="checkbox" class="tb-check" />
      <span class="flex-1">Magnitude</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('magnitude') }"></span>
    </label>
    <label class="tb-check-row">
      <input v-model="showThd" type="checkbox" class="tb-check" />
      <span class="flex-1">{{ t('thd') }}</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('thd') }"></span>
    </label>
    <label class="tb-check-row">
      <input v-model="showD2" type="checkbox" class="tb-check" />
      <span class="flex-1">{{ t('d2') }}</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('d2') }"></span>
    </label>
    <label class="tb-check-row">
      <input v-model="showD3" type="checkbox" class="tb-check" />
      <span class="flex-1">{{ t('d3') }}</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('d3') }"></span>
    </label>
    <label class="tb-check-row">
      <input v-model="showD4" type="checkbox" class="tb-check" />
      <span class="flex-1">{{ t('d4') }}</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('d4') }"></span>
    </label>
    <label class="tb-check-row">
      <input v-model="showD5" type="checkbox" class="tb-check" />
      <span class="flex-1">{{ t('d5') }}</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('d5') }"></span>
    </label>
    <label class="tb-check-row">
      <input v-model="showD6Plus" type="checkbox" class="tb-check" />
      <span class="flex-1">{{ t('d6plus') }}</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('d6plus') }"></span>
    </label>
    <label class="tb-check-row" title="The generator-off residual, as a share of each point's fundamental.">
      <input v-model="showNoiseFloor" type="checkbox" class="tb-check" />
      <span class="flex-1">Noise floor</span>
      <span class="h-[3px] w-4 shrink-0 rounded-full" :style="{ background: swatch('noise') }"></span>
    </label>

    <span class="tb-group pt-1">Style</span>
    <label class="tb-check-row">
      <input v-model="thickLines" type="checkbox" class="tb-check" />
      Thick lines
    </label>
    <label class="tb-check-row">
      <input v-model="showDataPoints" type="checkbox" class="tb-check" />
      Show data points
    </label>
  </CollapsibleSection>
</template>

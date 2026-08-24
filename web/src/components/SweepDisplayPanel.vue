<script setup lang="ts">
/**
 * Display options for the THD-vs-frequency chart. Everything here is a pure view preference —
 * nothing reaches the device or re-runs a sweep, so all of it stays enabled while one is running.
 *
 * Every trace has its own checkbox, and this panel is the only place they can be switched: the
 * chart's legend is read-only, so the checkbox and the chart can never disagree. Same arrangement
 * as the previous app's Graph data box.
 */

import { useSweep } from '../composables/useSweep'
import { useMeasurements } from '../composables/useMeasurements'
import { percentToDb } from '../sweep/points'
import type { ThdSweepPoint } from '../api/client'

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
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })

const Y_MAX_OPTIONS = [100, 10, 1]
const Y_MIN_OPTIONS = [0.1, 0.01, 0.001, 0.0001, 0.00001, 0.000001]
const X_FROM_OPTIONS = [5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

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
const { points: sweepPoints } = useSweep()
const { overlays } = useMeasurements()

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
function activeOverlaySweeps(): ThdSweepPoint[][] {
  return overlays.value.filter((o) => o.visible && o.sweepPoints?.length).map((o) => o.sweepPoints!)
}

function activeChannels(): number[] {
  const channels: number[] = []
  if (showLeft.value) channels.push(0)
  if (showRight.value) channels.push(1)
  return channels
}

/** One point's raw value for a given trace key — mirrors ThdSweepChart's valueOf(). */
function rawValue(point: ThdSweepPoint, key: string): number {
  switch (key) {
    case 'magnitude':
      return point.gainDb
    case 'thd':
      return point.thdPercent
    case 'd2':
    case 'd3':
    case 'd4':
    case 'd5':
      return point.harmonicPercent[Number(key.slice(1)) - 2] ?? Number.NaN
    case 'd6plus':
      return point.d6PlusPercent
    default:
      return point.noisePercent
  }
}

/** Highest/lowest value across every currently-checked trace and channel, live sweep plus visible overlays. */
function computeDistortionExtent(): { min: number; max: number } | null {
  const keys: string[] = []
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
  function consider(pts: ThdSweepPoint[]) {
    for (const p of pts) {
      if (!channels.includes(p.channel)) continue
      for (const key of keys) {
        const raw = rawValue(p, key)
        if (!Number.isFinite(raw)) continue
        // A non-positive percentage has no position on the log percent axis (see ThdSweepChart's
        // toY) and would drag the dB axis to -Infinity, so it is excluded from either extent — the
        // same gap-not-plunge rule the chart itself draws with.
        if (key !== 'magnitude' && !(raw > 0)) continue
        const v = unit.value === 'db' && key !== 'magnitude' ? percentToDb(raw) : raw
        if (v > max) max = v
        if (v < min) min = v
      }
    }
  }
  consider(sweepPoints.value)
  for (const pts of activeOverlaySweeps()) consider(pts)

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

function computeFrequencyExtent(): { min: number; max: number } | null {
  const channels = activeChannels()
  if (channels.length === 0) return null

  let min = Infinity
  let max = -Infinity
  function consider(pts: ThdSweepPoint[]) {
    for (const p of pts) {
      if (!channels.includes(p.channel) || !(p.freqHz > 0)) continue
      if (p.freqHz > max) max = p.freqHz
      if (p.freqHz < min) min = p.freqHz
    }
  }
  consider(sweepPoints.value)
  for (const pts of activeOverlaySweeps()) consider(pts)

  return Number.isFinite(min) && Number.isFinite(max) ? { min, max } : null
}

function requestAutofitFrequency() {
  const extent = computeFrequencyExtent()
  if (!extent) return
  xFromHz.value = largestAtMost(X_FROM_OPTIONS, extent.min)
  xToHz.value = smallestAtLeast(X_TO_OPTIONS, extent.max)
}
</script>

<template>
  <section class="flex flex-col gap-3 rounded-lg border border-neutral-200 p-3 dark:border-neutral-800">
    <h2 class="text-sm font-semibold text-neutral-900 dark:text-neutral-100">Display</h2>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Units</span>
      <!-- Toggle group rather than a select: two mutually exclusive options that people flip
           between constantly while reading a trace. -->
      <div class="inline-flex overflow-hidden rounded border border-neutral-300 dark:border-neutral-700">
        <button
          type="button"
          class="flex-1 px-3 py-1"
          :class="
            unit === 'percent'
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          @click="unit = 'percent'"
        >
          %
        </button>
        <button
          type="button"
          class="flex-1 border-l border-neutral-300 px-3 py-1 dark:border-neutral-700"
          :class="
            unit === 'db'
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          @click="unit = 'db'"
        >
          dB
        </button>
      </div>
    </div>

    <div class="flex flex-col gap-1.5 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Channels</span>
      <div class="flex gap-3">
        <label class="flex items-center gap-1.5">
          <input v-model="showLeft" type="checkbox" />
          Left
        </label>
        <label class="flex items-center gap-1.5">
          <input v-model="showRight" type="checkbox" />
          Right
        </label>
      </div>
    </div>

    <div class="flex flex-col gap-1.5 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Graph data</span>
      <!-- Only on the dB axis: magnitude is a gain in dB, which has no position on a percent
           scale. The previous app greys the same checkbox out on its D% graph. -->
      <label
        v-if="unit === 'db'"
        class="flex items-center gap-1.5"
        title="The measured fundamental relative to the generator setpoint — the DUT's frequency response, riding along with the sweep."
      >
        <input v-model="showMagnitude" type="checkbox" />
        Magnitude
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showThd" type="checkbox" />
        THD
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showD2" type="checkbox" />
        D2
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showD3" type="checkbox" />
        D3
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showD4" type="checkbox" />
        D4
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showD5" type="checkbox" />
        D5
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showD6Plus" type="checkbox" />
        D6+
      </label>
      <label class="flex items-center gap-1.5" title="The generator-off residual, as a share of each point's fundamental.">
        <input v-model="showNoiseFloor" type="checkbox" />
        Noise floor
      </label>
    </div>

    <div class="flex flex-col gap-1.5 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">{{ unit === 'db' ? 'dB range' : 'Distortion range (%)' }}</span>
      <template v-if="unit === 'db'">
        <label class="flex items-center justify-between gap-2">
          Top
          <input
            :value="yTopDb"
            type="number"
            :min="yBottomDb + DB_MIN_SPAN"
            :max="DB_MAX"
            :step="DB_STEP"
            class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="onTopDbChange"
          />
        </label>
        <label class="flex items-center justify-between gap-2">
          Bottom
          <input
            :value="yBottomDb"
            type="number"
            :min="DB_MIN"
            :max="yTopDb - DB_MIN_SPAN"
            :step="DB_STEP"
            class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            @change="onBottomDbChange"
          />
        </label>
      </template>
      <template v-else>
        <label class="flex items-center justify-between gap-2">
          Top
          <select
            v-model.number="yMaxPercent"
            class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          >
            <option v-for="v in Y_MAX_OPTIONS" :key="v" :value="v">{{ percentLabel(v) }}</option>
          </select>
        </label>
        <label class="flex items-center justify-between gap-2">
          Bottom
          <select
            v-model.number="yMinPercent"
            class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          >
            <option v-for="v in Y_MIN_OPTIONS" :key="v" :value="v">{{ percentLabel(v) }}</option>
          </select>
        </label>
      </template>
      <button
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Fits to the highest and lowest value across every currently-checked trace. Percent snaps to the enclosing dropdown preset; dB rounds out to the nearest 10 dB."
        @click="requestAutofitDistortion"
      >
        Autofit
      </button>
    </div>

    <div class="flex flex-col gap-1.5 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Frequency range (Hz)</span>
      <label class="flex items-center justify-between gap-2">
        From
        <select
          v-model.number="xFromHz"
          class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="v in X_FROM_OPTIONS" :key="v" :value="v">{{ v }}</option>
        </select>
      </label>
      <label class="flex items-center justify-between gap-2">
        To
        <select
          v-model.number="xToHz"
          class="w-28 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        >
          <option v-for="v in X_TO_OPTIONS" :key="v" :value="v">{{ v }}</option>
        </select>
      </label>
      <button
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        title="Fits From/To to the lowest and highest measured frequency, snapped to the enclosing dropdown preset."
        @click="requestAutofitFrequency"
      >
        Autofit
      </button>
    </div>

    <div class="flex flex-col gap-1.5 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Style</span>
      <label class="flex items-center gap-1.5">
        <input v-model="thickLines" type="checkbox" />
        Thick lines
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="showDataPoints" type="checkbox" />
        Show data points
      </label>
    </div>
  </section>
</template>

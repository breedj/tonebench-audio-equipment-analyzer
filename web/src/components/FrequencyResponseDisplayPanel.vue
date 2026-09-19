<script setup lang="ts">
import { computed, watch } from 'vue'
import type { NyquistRadiusName, ResponseProjection, ResponseView } from '../api/client'
import { useFrequencyResponse, type ResponseTrace } from '../composables/useFrequencyResponse'
import { useMeasurements, type OverlayEntry } from '../composables/useMeasurements'
import CollapsibleSection from './CollapsibleSection.vue'

/**
 * Whether coherence is a meaningful thing to offer. A chirp measures it from the cross-spectra of
 * several records; a stepped sine has one deterministic capture per point and nothing to compare it
 * with, so the toggle is hidden there rather than offering a control that always reads 1.
 */
const props = withDefaults(defineProps<{ allowCoherence?: boolean }>(), { allowCoherence: true })

const view = defineModel<ResponseView>('view', { required: true })
const projection = defineModel<ResponseProjection>('projection', { required: true })
const nyquistRadius = defineModel<NyquistRadiusName>('nyquistRadius', { required: true })
const nyquistRadiusFloorDb = defineModel<number>('nyquistRadiusFloorDb', { required: true })
const restoreDelay = defineModel<boolean>('restoreDelay', { required: true })
const showLeft = defineModel<boolean>('showLeft', { required: true })
const showRight = defineModel<boolean>('showRight', { required: true })
const showPhase = defineModel<boolean>('showPhase', { required: true })
const showCoherence = defineModel<boolean>('showCoherence', { required: true })
const showOneDb = defineModel<boolean>('showOneDb', { required: true })
const showThreeDb = defineModel<boolean>('showThreeDb', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
// Model names stay yTopDb/yBottomDb for backward compat (App.vue/params_json), but they now name
// the Gain (dB) view's own window only -- see the yTopDb/yBottomDb computed proxies below, which
// pick this pair or yTopDbv/yBottomDbv depending on which view is showing.
const yTopDbGain = defineModel<number>('yTopDb', { required: true })
const yBottomDbGain = defineModel<number>('yBottomDb', { required: true })
const yTopDbv = defineModel<number>('yTopDbv', { required: true })
const yBottomDbv = defineModel<number>('yBottomDbv', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })
const phaseTopDeg = defineModel<number>('phaseTopDeg', { required: true })
const phaseBottomDeg = defineModel<number>('phaseBottomDeg', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

const { traces, averageCount, phaseKind, rightChannelIsReference, phaseAvailable, running, latencySamples } =
  useFrequencyResponse()

/**
 * A complex response needs a magnitude and a phase measured against the *same* reference.
 * `ChannelDifference` does not have that -- its gain is against the commanded generator level and
 * its phase against the right input -- so the projections are refused rather than offered with a
 * caveat. Empty is exempt for the same reason `phaseAvailable` exempts it: a type that has run
 * nothing has no phase yet, and hiding the control there suggests it never has one.
 */
const complexAvailable = computed(
  () => phaseKind.value === 'DutResponse' || phaseKind.value === 'PerTrace' || traces.value.length === 0,
)

const projectionHint = computed(() =>
  complexAvailable.value
    ? 'Bode is magnitude and phase against frequency; Nyquist and Nichols are the same data as one complex curve.'
    : phaseKind.value === 'ChannelDifference'
      ? 'This run measures gain against the generator but phase against the right input, so it has no single complex response. Make the right channel the reference.'
      : 'This run measured no phase. Turn the right-channel generator on and make it the reference.',
)

/** Bode-only controls: the dB window, the bandwidth markers, the channel checkboxes and both Autofits. */
const bodeShowing = computed(() => projection.value === 'Bode' || !complexAvailable.value)

const PROJECTIONS: { value: ResponseProjection; label: string }[] = [
  { value: 'Bode', label: 'Bode' },
  { value: 'Nyquist', label: 'Nyquist' },
  { value: 'Nichols', label: 'Nichols' },
]
const { overlays } = useMeasurements()

/** The y window actually in effect -- FR (dBV) and Gain (dB) are independent so switching views never clobbers the other one's range. */
const yTopDb = computed({
  get: () => (view.value === 'Dbv' ? yTopDbv.value : yTopDbGain.value),
  set: (v: number) => {
    if (view.value === 'Dbv') yTopDbv.value = v
    else yTopDbGain.value = v
  },
})
const yBottomDb = computed({
  get: () => (view.value === 'Dbv' ? yBottomDbv.value : yBottomDbGain.value),
  set: (v: number) => {
    if (view.value === 'Dbv') yBottomDbv.value = v
    else yBottomDbGain.value = v
  },
})

const DB_MIN = -200
const DB_MAX = 200
const DB_STEP = 2
const DB_MIN_SPAN = 4

// Wide enough for several turns of accumulated phase (a DUT with real excess delay runs well past
// 360°) without being unbounded; matches the chart's own axis-tick increments.
const PHASE_MIN = -1440
const PHASE_MAX = 1440
const PHASE_STEP = 15
// Two of the chart's own rounding ticks (see PHASE_TICK_DEG below) — narrower than that would
// magnify a flat phase into noise the way the dB window's own minimum span guards against.
const PHASE_TICK_DEG = 45
const PHASE_MIN_SPAN = PHASE_TICK_DEG * 2

const X_FROM_OPTIONS = [1, 2, 5, 10, 20, 50, 100, 200, 500]
const X_TO_OPTIONS = [1000, 2000, 5000, 10000, 20000, 50000, 100000]

/**
 * Clamping through `:value` + `@change` rather than `defineModel` + `@change`. Two reasons, both
 * learned the hard way on the sweep panel: a `defineModel` write goes out as an emit and comes back
 * as a prop, so a handler in the same tick still reads the *previous* value and clamps against it,
 * writing the old bound back over what was just typed. And `change` fires on blur, Enter and each
 * spinner click but not per keystroke, so the axis does not thrash while someone types "-160".
 *
 * Shared by the dB and the phase range boxes below — both are a bounded number pinned against its
 * own partner field, just with different limits.
 */
function commitBoundedNumber(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current

  el.value = String(next)
  return next
}

function onTopDbChange(event: Event) {
  yTopDb.value = commitBoundedNumber(event, yTopDb.value, yBottomDb.value + DB_MIN_SPAN, DB_MAX)
}

function onBottomDbChange(event: Event) {
  yBottomDb.value = commitBoundedNumber(event, yBottomDb.value, DB_MIN, yTopDb.value - DB_MIN_SPAN)
}

function onTopPhaseChange(event: Event) {
  phaseTopDeg.value = commitBoundedNumber(event, phaseTopDeg.value, phaseBottomDeg.value + PHASE_MIN_SPAN, PHASE_MAX)
}

function onBottomPhaseChange(event: Event) {
  phaseBottomDeg.value = commitBoundedNumber(event, phaseBottomDeg.value, PHASE_MIN, phaseTopDeg.value - PHASE_MIN_SPAN)
}

/** Stored responses actually drawable right now — checked on and carrying traces of this kind. */
function activeOverlayResponseTraces(): OverlayEntry[] {
  return overlays.value.filter((o) => o.visible && o.responseTraces?.length)
}

/** Every currently-checked trace of a given kind, live plus visible overlays. */
function tracesOfKind(kind: ResponseTrace['kind']): ResponseTrace[] {
  const live = traces.value.find((t) => t.kind === kind)
  const overlayTraces = activeOverlayResponseTraces()
    .map((o) => o.responseTraces!.find((t) => t.kind === kind))
    .filter((t): t is ResponseTrace => !!t)
  return live ? [live, ...overlayTraces] : overlayTraces
}

/**
 * The curves autofit sizes against: every checked channel the current view is actually about, live
 * plus visible history overlays — not just Left, and not just the live measurement.
 */
const fitTraces = computed<ResponseTrace[]>(() => {
  if (view.value === 'Gain') return tracesOfKind('Gain')

  const kinds: ResponseTrace['kind'][] = []
  if (showLeft.value) kinds.push('Left')
  if (showRight.value) kinds.push('Right')
  return kinds.flatMap((kind) => tracesOfKind(kind))
})

/**
 * Fits the dB window to the curve, rounded outward to whole steps and never tighter than
 * DB_MIN_SPAN. Sized off the min *and* max columns, not just the plotted line: the band beneath it
 * is part of the curve, and fitting to the line alone clips the ripple out of view.
 */
/**
 * Matches the chart's own `phaseTrace` — Autofit has to size the curve that is actually drawn, and
 * the phase curve belongs to the measurement rather than to the view.
 */
const phaseTraces = computed<ResponseTrace[]>(() =>
  phaseKind.value === 'PerTrace' ? fitTraces.value : tracesOfKind('Gain'),
)

function autofitDb() {
  const list = fitTraces.value.filter((t) => t.magnitudeMaxDb.length > 0)
  if (list.length === 0) return

  let lo = Infinity
  let hi = -Infinity
  for (const trace of list) {
    for (let i = 0; i < trace.magnitudeMaxDb.length; i++) {
      const freqHz = trace.freqHz[i]
      if (freqHz < xFromHz.value || freqHz > xToHz.value) continue
      lo = Math.min(lo, trace.magnitudeMinDb[i])
      hi = Math.max(hi, trace.magnitudeMaxDb[i])
    }
  }

  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  const top = Math.ceil((hi + 1) / DB_STEP) * DB_STEP
  const bottom = Math.floor((lo - 1) / DB_STEP) * DB_STEP

  yTopDb.value = Math.min(DB_MAX, Math.max(top, bottom + DB_MIN_SPAN))
  yBottomDb.value = Math.max(DB_MIN, Math.min(bottom, yTopDb.value - DB_MIN_SPAN))
}

/**
 * Fits the phase window to the curve, rounded outward to PHASE_TICK_DEG and never tighter than
 * PHASE_MIN_SPAN — the same shape as autofitDb, on the traces' phaseDeg rather than their magnitude.
 *
 * Sized against `phaseTraces`, not `fitTraces`: the phase curve belongs to the measurement rather
 * than to the view, so this has to fit whichever curve(s) the chart is actually drawing.
 */
function autofitPhase() {
  const list = phaseTraces.value.filter((t) => t.phaseDeg.length > 0)
  if (list.length === 0) return

  let lo = Infinity
  let hi = -Infinity
  for (const trace of list) {
    for (let i = 0; i < trace.phaseDeg.length; i++) {
      const freqHz = trace.freqHz[i]
      if (freqHz < xFromHz.value || freqHz > xToHz.value) continue
      const deg = trace.phaseDeg[i]
      if (!Number.isFinite(deg)) continue
      lo = Math.min(lo, deg)
      hi = Math.max(hi, deg)
    }
  }

  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  let bottom = Math.floor(lo / PHASE_TICK_DEG) * PHASE_TICK_DEG
  let top = Math.ceil(hi / PHASE_TICK_DEG) * PHASE_TICK_DEG
  if (top - bottom < PHASE_MIN_SPAN) {
    const mid = (top + bottom) / 2
    bottom = mid - PHASE_TICK_DEG
    top = mid + PHASE_TICK_DEG
  }

  phaseTopDeg.value = Math.min(PHASE_MAX, top)
  phaseBottomDeg.value = Math.max(PHASE_MIN, bottom)
}

/** Fits the frequency range to the measured band, snapped to the dropdown's own options. */
function autofitX() {
  const list = fitTraces.value.filter((t) => t.freqHz.length > 0)
  if (list.length === 0) return

  const from = Math.min(...list.map((t) => t.startFreqHz))
  const to = Math.max(...list.map((t) => t.endFreqHz))

  xFromHz.value = [...X_FROM_OPTIONS].reverse().find((v) => v <= from) ?? X_FROM_OPTIONS[0]
  xToHz.value = X_TO_OPTIONS.find((v) => v >= to) ?? X_TO_OPTIONS[X_TO_OPTIONS.length - 1]
}

// "Autofit when measurement done": running is watched here (rather than in App.vue) because the
// autofit functions and the running/traces state are both already local to this file and to
// useFrequencyResponse() -- this is the same running->not-running transition that flips RUN back
// to a start-labeled button.
watch(running, (isRunning, wasRunning) => {
  // Gated on the Bode chart actually being up: these fit the dB and frequency windows, and firing
  // them under a polar projection silently rewrites a window the user cannot see.
  if (wasRunning && !isRunning && autofitOnDone.value && bodeShowing.value) {
    autofitDb()
    autofitX()
    if (showPhase.value && phaseAvailable.value) autofitPhase()
  }
})
</script>

<template>
  <!-- The axis windows are their own sections ahead of Display, laid out the way the FFT screen's
       dBV range / Frequency range are (ChartDisplayPanel.vue): label over a full-width field,
       Autofit under. Multi-root, so they flow into MeasurementLayout's aside grid one by one like
       SweepDisplayPanel's. -->
  <CollapsibleSection v-if="bodeShowing" :title="view === 'Dbv' ? 'dBV range' : 'Gain range'" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      Top
      <input
        :value="yTopDb"
        type="number"
        :step="DB_STEP"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onTopDbChange"
      />
    </label>
    <label class="flex flex-col gap-1 text-xs">
      Bottom
      <input
        :value="yBottomDb"
        type="number"
        :step="DB_STEP"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onBottomDbChange"
      />
    </label>
    <button
      type="button"
      class="w-full rounded border border-blue-300 bg-blue-50 px-2 py-1 text-xs text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
      @click="autofitDb"
    >
      Autofit
    </button>
  </CollapsibleSection>

  <CollapsibleSection title="Frequency range (Hz)" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      From
      <select
        v-model.number="xFromHz"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="hz in X_FROM_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
      </select>
    </label>
    <label class="flex flex-col gap-1 text-xs">
      To
      <select
        v-model.number="xToHz"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      >
        <option v-for="hz in X_TO_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
      </select>
    </label>
    <button
      type="button"
      class="w-full rounded border border-blue-300 bg-blue-50 px-2 py-1 text-xs text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
      @click="autofitX"
    >
      Autofit
    </button>
  </CollapsibleSection>

  <!-- Only meaningful while the Phase trace is on and there is one to draw -- hidden rather than
       shown-but-inert, since the axis it configures is not drawn otherwise. -->
  <CollapsibleSection v-if="bodeShowing && showPhase && phaseAvailable" title="Phase range (°)" size="xs">
    <label class="flex flex-col gap-1 text-xs">
      Top
      <input
        :value="phaseTopDeg"
        type="number"
        :step="PHASE_STEP"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onTopPhaseChange"
      />
    </label>
    <label class="flex flex-col gap-1 text-xs">
      Bottom
      <input
        :value="phaseBottomDeg"
        type="number"
        :step="PHASE_STEP"
        class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        @change="onBottomPhaseChange"
      />
    </label>
    <button
      type="button"
      class="w-full rounded border border-blue-300 bg-blue-50 px-2 py-1 text-xs text-blue-700 hover:bg-blue-100 dark:border-blue-700 dark:bg-blue-950/40 dark:text-blue-300 dark:hover:bg-blue-900/40"
      title="Fits the window to the phase curve. In FR (dBV) view that trace carries no phase at all, so this settles on the narrowest span around 0 deg rather than anything useful -- switch to Gain view to fit a real reading."
      @click="autofitPhase"
    >
      Autofit
    </button>
  </CollapsibleSection>

  <CollapsibleSection title="Display">
    <!-- The toggle groups here (Projection, Radius, View) highlight the selected option the way the
         sweep screens' Units toggle does, black on white: one look for "which of these is on" across
         every display rail. -->
    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Projection</span>
      <div
        class="flex overflow-hidden rounded border border-neutral-300 dark:border-neutral-700"
        :title="projectionHint"
      >
        <button
          v-for="option in PROJECTIONS"
          :key="option.value"
          type="button"
          class="flex-1 border-l border-neutral-300 px-2 py-1 first:border-l-0 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-700"
          :class="
            projection === option.value
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          :disabled="option.value !== 'Bode' && !complexAvailable"
          @click="projection = option.value"
        >
          {{ option.label }}
        </button>
      </div>
    </div>

    <!-- Only meaningful under a polar projection, so it is not shown under Bode at all rather than
         sitting there inert. -->
    <div v-if="!bodeShowing && projection === 'Nyquist'" class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Radius</span>
      <div class="flex overflow-hidden rounded border border-neutral-300 dark:border-neutral-700">
        <button
          type="button"
          class="flex-1 px-2 py-1"
          :class="
            nyquistRadius === 'Linear'
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          title="The textbook plot: distances mean what they look like."
          @click="nyquistRadius = 'Linear'"
        >
          Linear
        </button>
        <button
          type="button"
          class="flex-1 border-l border-neutral-300 px-2 py-1 dark:border-neutral-700"
          :class="
            nyquistRadius === 'Db'
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          title="Keeps a wide range on screen, but distance to −1 is no longer metric."
          @click="nyquistRadius = 'Db'"
        >
          dB
        </button>
      </div>
      <label v-if="nyquistRadius === 'Db'" class="flex items-center justify-between gap-2">
        Floor (dB)
        <input
          v-model.number="nyquistRadiusFloorDb"
          type="number"
          max="-6"
          step="10"
          class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        />
      </label>
    </div>

    <!-- Only the chirp removes a delay, so only the chirp can put one back. -->
    <label v-if="!bodeShowing && latencySamples !== null" class="flex items-center gap-1.5 text-xs">
      <input v-model="restoreDelay" type="checkbox" />
      <span title="The chirp estimator removes a bulk delay per record; this puts it back, so the contour is the physical one. Overlays are always drawn uncorrected.">
        Restore removed delay
      </span>
    </label>

    <div v-if="bodeShowing" class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">View</span>
      <div class="flex overflow-hidden rounded border border-neutral-300 dark:border-neutral-700">
        <button
          type="button"
          class="flex-1 px-2 py-1"
          :class="
            view === 'Dbv'
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          @click="view = 'Dbv'"
        >
          FR (dBV)
        </button>
        <button
          type="button"
          class="flex-1 border-l border-neutral-300 px-2 py-1 dark:border-neutral-700"
          :class="
            view === 'Gain'
              ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
              : 'hover:bg-neutral-50 dark:hover:bg-neutral-800'
          "
          @click="view = 'Gain'"
        >
          Gain (dB)
        </button>
      </div>
    </div>

    <label class="flex items-center gap-1.5 text-xs">
      <input v-model="autofitOnDone" type="checkbox" /> Autofit when measurement done
    </label>

    <div v-if="bodeShowing && view === 'Dbv'" class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Channels</span>
      <label class="flex items-center gap-1.5"><input v-model="showLeft" type="checkbox" /> Left</label>
      <!-- Not offered when the right input is the reference: what its dBV trace shows then is the
           generator, not a measurement of anything. -->
      <label v-if="!rightChannelIsReference" class="flex items-center gap-1.5">
        <input v-model="showRight" type="checkbox" /> Right
      </label>
    </div>

    <div v-if="bodeShowing" class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Bandwidth</span>
      <label class="flex items-center gap-1.5" title="Dashed markers and a labelled arrow at the -1 dB corners.">
        <input v-model="showOneDb" type="checkbox" /> −1 dB
      </label>
      <label class="flex items-center gap-1.5" title="Dashed markers and a labelled arrow at the -3 dB corners.">
        <input v-model="showThreeDb" type="checkbox" /> −3 dB
      </label>
    </div>

    <div v-if="bodeShowing" class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Extra traces</span>
      <!-- Hidden outright when the measurement on screen has no phase: with no stimulus on the right
           input there is nothing to compare the left against, and a single channel's own angle is
           meaningless without a capture trigger. See the store's phaseAvailable. -->
      <label
        v-if="phaseAvailable"
        class="flex items-center gap-1.5"
        :title="
          phaseKind === 'ChannelDifference'
            ? 'Unwrapped phase of the left input relative to the right, on its own axis at the right in degrees. Both inputs are measuring signals of their own here, so this is the difference between two paths rather than the DUT\'s phase response. Range set below, independently of the dB one.'
            : 'Unwrapped phase, with the bulk propagation delay removed, on its own axis at the right in degrees. Range set below, independently of the dB one.'
        "
      >
        <input v-model="showPhase" type="checkbox" />
        {{ phaseKind === 'ChannelDifference' ? 'Phase difference' : 'Phase' }}
      </label>
      <label
        v-if="props.allowCoherence"
        class="flex items-center gap-1.5"
        :title="
          averageCount > 1
            ? 'How repeatable the response was, per bin, across the averaged records: 1 means every record agreed, and it falls where noise or too little stimulus energy makes that bin untrustworthy. Own axis at the right, 0 to 1.'
            : 'Needs two or more averages — a single record always reads 1.'
        "
      >
        <input v-model="showCoherence" type="checkbox" /> Coherence
      </label>
      <p
        v-if="props.allowCoherence && showCoherence && averageCount <= 1"
        class="text-[11px] text-amber-600 dark:text-amber-400"
      >
        Coherence needs 2+ averages; a single record reads 1 everywhere.
      </p>
    </div>

    <div class="flex flex-col gap-1 text-xs">
      <span class="text-neutral-500 dark:text-neutral-400">Style</span>
      <label class="flex items-center gap-1.5"><input v-model="thickLines" type="checkbox" /> Thick lines</label>
      <label class="flex items-center gap-1.5">
        <input v-model="showDataPoints" type="checkbox" /> Show data points
      </label>
    </div>
  </CollapsibleSection>
</template>

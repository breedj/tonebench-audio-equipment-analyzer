<script setup lang="ts">
import { computed, ref } from 'vue'
import CollapsibleSection from './CollapsibleSection.vue'
import AnnotationsPanel from './AnnotationsPanel.vue'
import { OSCILLOSCOPE_ANNOTATION_VIEW } from '../charts/annotationViews'
import { useNumberDrafts } from '../composables/useNumberDrafts'
import { useOscilloscope } from '../composables/useOscilloscope'
import { recordExtremes } from '../scope/measurements'
import {
  VOLTS_PER_DIVISION_LADDER,
  fitVoltWindow,
  formatVoltsPerDivision,
  roundVoltsForWindow,
  snapVoltsPerDivision,
  voltWindow,
} from '../scope/voltAxis'
import StepperTile from './StepperTile.vue'
import { stepLadder } from '../panels/stepperGrid'
import { clampWindow, fromChartMs, recordWindow, scopeTimeScale, toChartMs } from '../scope/timeAxis'
import {
  SCOPE_CURSOR_MODES,
  cursorPairNeedsPlacing,
  cursorTimeBBox,
  cursorTimeBFromBox,
  cursorsTrack,
  placeCursorPair,
  type ScopeCursorMode,
  type ScopeCursors,
} from '../scope/cursors'
import {
  persistenceDraws,
  SCOPE_INTENSITY_MODES,
  SCOPE_PERSISTENCE_OPTIONS,
  type ScopeIntensityMode,
  type ScopePersistence,
} from '../scope/persistence'
import { capturedScopeChannels, scopeChannelDrawn } from '../scope/display'
import { PALETTE_NAMES } from '../measurements/decay'
import SegmentedToggle from './SegmentedToggle.vue'
import { SCOPE_VIEWS, scopeViewIsXy, type ScopeView } from '../scope/xy'
import {
  SCOPE_COUPLINGS,
  type ScopeSignalPathSettings,
} from '../scope/signalPath'
import ChannelPair from './ChannelPair.vue'
import ScopeMathDialog from './ScopeMathDialog.vue'
import ScopeToggleIcon from './ScopeToggleIcon.vue'
import { SCOPE_CURSOR_STYLES, cursorBoxRows, type ScopeCursorStyle } from '../scope/cursorOverlay'
import { SCOPE_DRAWINGS, drawingNeedsRawSlice, type ScopeDrawing } from '../scope/interpolation'
import {
  DEFAULT_SCOPE_MATH_SPEC,
  SCOPE_MATH_SLOTS,
  mathOption,
  mathSpecDraws,
  mathSpecLabel,
  mathSpecMissing,
  scopeMathSlice,
  type ScopeMathSlot,
  type ScopeMathSpec,
} from '../scope/math'
import type { DecayPalette } from '../api/client'
import type { OverlayEntry } from '../composables/useMeasurements'

/**
 * The oscilloscope chart's display rail: the vertical pair, the time window, one Autofit for both,
 * and the style toggles.
 *
 * <p><b>The vertical pair is V/div and a centre, not a top and a bottom</b> — see
 * <code>scope/voltAxis.ts</code>. A scope is read by counting squares, so a division has to be a
 * round number; the two ends of the window are derived from the pair rather than typed.</p>
 */

const voltsPerDivision = defineModel<number>('voltsPerDivision', { required: true })
const voltOffsetV = defineModel<number>('voltOffsetV', { required: true })
const timeFromS = defineModel<number>('timeFromS', { required: true })
const timeToS = defineModel<number>('timeToS', { required: true })
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
const showTriggerLevel = defineModel<boolean>('showTriggerLevel', { required: true })
const showLeft = defineModel<boolean>('showLeft', { required: true })
const showRight = defineModel<boolean>('showRight', { required: true })
const showResidual = defineModel<boolean>('showResidual', { required: true })
const persistence = defineModel<ScopePersistence>('persistence', { required: true })
const intensityMode = defineModel<ScopeIntensityMode>('intensityMode', { required: true })
const intensityPalette = defineModel<DecayPalette>('intensityPalette', { required: true })
/** The two math channels — see scope/math.ts. A pair, because the chart draws two. */
const maths = defineModel<[ScopeMathSpec, ScopeMathSpec]>('maths', { required: true })
const chartView = defineModel<ScopeView>('chartView', { required: true })
const drawing = defineModel<ScopeDrawing>('drawing', { required: true })
/** Which style the cursors are read in. A preference, where their positions are session state. */
const cursorStyle = defineModel<ScopeCursorStyle>('cursorStyle', { required: true })

/**
 * Whether the chosen drawing is actually reaching the picture.
 *
 * <p>Both of the interesting modes need the samples, and a wide window has none — the analyzer
 * sends both extremes of each column instead. So the rail says when the setting is not in force
 * rather than leaving a control that silently does nothing.</p>
 */
/**
 * Whether the chosen drawing is the one actually on the chart.
 *
 * <p><b>Its paragraph is gone and the fact moved onto the button</b> (Joost, 2026-11-21). The
 * caption was three lines under a three-button row, saying what the <em>chart's</em> own header
 * already says a few pixels away — a decimated slice reads "min/max per col…" up there and "every
 * sample drawn" when it is not. What a tooltip cannot carry is that the mode you chose is not the
 * one being drawn, so the lit button says that itself: dimmed, with the reason on its title. The
 * state stays on screen, which is the rule; it is the prose that goes.</p>
 */
const drawingInForce = computed(() => !drawingNeedsRawSlice(drawing.value) || record.value?.channels?.[0]?.raw === true)

/**
 * The display path, applied by the analyzer to the whole record before anything is drawn.
 *
 * <p>One field today. The in-path filter stood beside the coupling here until 2026-11-18 and
 * will again; see scope/signalPath.ts for what the first one got wrong.</p>
 */
const path = defineModel<ScopeSignalPathSettings>('path', { required: true })


/** True while the goniometer is the picture on screen, which hides the controls it has no axes for. */
const xyOn = computed(() => scopeViewIsXy(chartView.value))

/**
 * True in the two views the zoom bar is hidden in — the goniometer and the eye.
 *
 * <p>Which is the one place the Time window boxes are still drawn: those views have no time axis,
 * so there is no navigator over the record to put the window on, and the boxes are then the only
 * way to say which stretch is plotted.</p>
 */
const planeShowing = computed(() => chartView.value === 'XY' || chartView.value === 'Eye')

/** The colour controls only mean something once something is being kept. */
const persistenceOn = computed(() => persistenceDraws(persistence.value))

/** The cursors. See scope/cursors.ts — session state, so they are not in <code>params_json</code>. */
const cursors = defineModel<ScopeCursors>('cursors', { required: true })

/** The pass/fail mask. See scope/mask.ts — the tolerance is a specification, so it is stored. */
const maskOn = defineModel<boolean>('maskOn', { required: true })
const maskToleranceV = defineModel<number>('maskToleranceV', { required: true })
const maskToleranceS = defineModel<number>('maskToleranceS', { required: true })

const emit = defineEmits<{
  /**
   * A cursor has been moved, or switched on or off: the parent gates the measurements once.
   *
   * <p>Emitted from the control rather than watched upstream, for the chart's own reason — the gate
   * is a fetch, and a watcher would ask the server for a window per keystroke.</p>
   */
  cursorsCommitted: []
  /**
   * Throw the accumulated picture away and start it again from the next record.
   *
   * <p>An action rather than a setting, so it is emitted rather than modelled: what it clears is
   * three charts' pixel buffers — the waveform view's, the goniometer's and the eye's — and the
   * parent is the one thing that can reach all three.</p>
   */
  clearPersistence: []
}>()

const props = defineProps<{
  /** The record the settings ask for, so the window can be fitted before anything is captured. */
  recordSeconds: number
  preTriggerFraction: number
  /**
   * What the mask is built from: the name of the first checked history item, or null where none is
   * checked.
   *
   * <p>The <em>first</em> rather than one chosen here, which is what lets the setting be stored
   * without a result id in it — see scope/mask.ts and the stored display's own note.</p>
   */
  maskReferenceName?: string | null
  /** How the run has gone so far: records that stayed inside the band, and records that did not. */
  maskTally?: { passed: number; failed: number }
  /**
   * The checked history items the chart is drawing, so Autofit frames them too.
   *
   * <p>It framed the live record alone until 2026-11-21, which is wrong in the one case the
   * references exist for: a stored capture taller than the live one was left running off the
   * top of a window that claims to show the comparison. Only the <em>vertical</em> needs them
   * — the analyzer cuts every reference to this same window, so its span is the window and
   * fitting time to it would be circular.</p>
   */
  references?: OverlayEntry[]
}>()

const { record, recordBounds } = useOscilloscope()

/**
 * What is being typed into each number box on this rail, until it is committed.
 *
 * <p>Every box here is <code>:value</code> + <code>@change</code> rather than <code>v-model</code>,
 * because each converts — seconds to the rail's own time unit, a tolerance to the same. That is
 * exactly the shape Vue's <code>value</code> carve-out eats: this panel re-renders several times a
 * second, since it reads the record, the drawing's raw-ness, the math clamp count, the window span
 * and the mask tally, and each of those renders re-stamps the committed figure over whatever is
 * half typed. Reported from the bench on the mask's Time box — which is the one that shows it,
 * because the tally beside it is replaced on <em>every</em> record. See useNumberDrafts.ts.</p>
 */
const drafts = useNumberDrafts()

/**
 * The bounds a window is clamped to: the record's own span, or — before anything is captured — the
 * span the settings ask for.
 *
 * <p>The settings' span is the right fallback rather than "no bounds at all": an empty scope's axis
 * is where its next capture will land, so a window typed now is checked against the same record it
 * will be read on.</p>
 */
const bounds = computed(
  () => recordBounds.value ?? recordWindow(props.recordSeconds, props.preTriggerFraction),
)

/**
 * The unit the two time boxes are typed in.
 *
 * <p>Taken from the <b>record</b> and not from the window on screen, which is the one choice here
 * that is not obvious: a unit that followed the zoom would change under the user's fingers while
 * they typed a figure into it. The record is fixed for the length of a measurement, so the boxes
 * stay in one unit while the axis labels and the readouts follow the window — which is what those
 * are for.</p>
 */
const boxScale = computed(() => scopeTimeScale(toChartMs(props.recordSeconds)))

/** A stored second as the box shows it, and back. */
function toBox(seconds: number): number {
  const value = toChartMs(seconds) / boxScale.value.msPerUnit
  // Four figures: finer than the box's own step and short enough to read.
  return Number(value.toPrecision(6))
}

function fromBox(value: number): number {
  return fromChartMs(value * boxScale.value.msPerUnit)
}

const fromBoxValue = computed(() => toBox(timeFromS.value))
const toBoxValue = computed(() => toBox(timeToS.value))

/** One step of the boxes: a hundredth of the record, which is about a tenth of a division. */
const boxStep = computed(() => {
  const span = toChartMs(props.recordSeconds) / boxScale.value.msPerUnit
  return Number((span / 100).toPrecision(1)) || 0.001
})

/**
 * Both ends are settled as a pair and only then written.
 *
 * <p>A <code>defineModel</code> with a listener on it does not read back the value just assigned —
 * it emits and the new value arrives as a prop on the parent's next render — so settling one end
 * against the other by reading it back hands out the window being replaced. That is the
 * <code>fitDbWindow</code> lesson, and it is reachable here by typing a From above the current To.</p>
 */
function commitWindow(fromS: number, toS: number) {
  const clamped = clampWindow({ fromS, toS }, bounds.value)
  timeFromS.value = clamped.fromS
  timeToS.value = clamped.toS
}

function onFromChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(fromBoxValue.value)
    return
  }
  commitWindow(fromBox(typed), timeToS.value)
}

function onToChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(toBoxValue.value)
    return
  }
  commitWindow(timeFromS.value, fromBox(typed))
}

function onOffsetChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (Number.isFinite(typed)) voltOffsetV.value = typed
  else (event.target as HTMLInputElement).value = String(voltOffsetV.value)
}

/**
 * Which inputs the record on hand carries — what the measurement is capturing.
 *
 * <p>A channel the Acquisition panel is not capturing has nothing to show, so its box here says so
 * and is disabled rather than being a tick that does nothing. Before the first capture there is no
 * record to ask, and both are offered.</p>
 */
const captured = computed(() => capturedScopeChannels(record.value?.channels))


/** An X–Y figure needs both inputs; one of them draws a horizontal line that looks like a measurement. */
const bothCaptured = computed(() => captured.value.left && captured.value.right)

/**
 * The two math channels, as the rail draws them: what each computes, and what is wrong with it.
 *
 * <p>One row per slot rather than two blocks of markup, so a third could not be added with one of
 * its notes missing.</p>
 */
const mathRows = computed(() =>
  SCOPE_MATH_SLOTS.map((slot) => {
    const spec = maths.value[slot - 1]
    return {
      slot,
      spec,
      on: mathSpecDraws(spec),
      /** The expression with its sources filled in — "L − R" rather than "A − B". */
      label: mathSpecLabel(spec),
      title: mathOption(spec.operation).title,
      /**
       * Which chosen inputs this measurement is not capturing.
       *
       * <p>The server answers with no trace at all in that case, which is the honest result — an
       * operation computed against an array of zeros would draw A itself and label it A − B — but
       * an absent trace with nothing beside it reads as a broken setting, so the rail says which
       * box to tick.</p>
       */
      missing: mathSpecMissing(spec, captured.value.left, captured.value.right),
      /**
       * How many samples A ÷ B railed on, when it did.
       *
       * <p>Reported rather than hidden, which is the whole reason the count comes back from the
       * server: a ratio trace spends every zero crossing of its denominator at the limit, so one
       * that is mostly at ±1000 is a picture of the clamp and its reader has to be told.</p>
       */
      clamped: scopeMathSlice(record.value, slot)?.clampedSamples ?? 0,
    }
  }),
)

/** Which channel's dialog is open, or null. */
const mathDialogSlot = ref<ScopeMathSlot | null>(null)

const mathDialogSpec = computed(() =>
  mathDialogSlot.value ? maths.value[mathDialogSlot.value - 1] : DEFAULT_SCOPE_MATH_SPEC,
)

/** One channel set from the dialog. The pair is replaced rather than mutated, as every model here is. */
function onMathApply(spec: ScopeMathSpec) {
  const slot = mathDialogSlot.value
  if (!slot) return
  const next: [ScopeMathSpec, ScopeMathSpec] = [maths.value[0], maths.value[1]]
  next[slot - 1] = spec
  maths.value = next
}

/**
 * The extremes of what is <b>drawn</b> — exact even on a decimated slice. See scope/measurements.ts.
 *
 * <p>Only the visible channels, because this is what Autofit frames: fitting a hidden trace would
 * leave the one on screen in a corner of a window sized for something the reader cannot see.</p>
 */
const extremes = computed(() => {
  const slices = (record.value?.channels ?? []).filter((slice) =>
    slice.channel === 1 ? showRight.value : showLeft.value,
  )
  // The references on the same gate the chart draws them on -- the rail's box *and* the
  // measurement capturing that channel -- so what Autofit frames is what is on the chart.
  const captured = capturedScopeChannels(record.value?.channels)
  const shown = { left: showLeft.value, right: showRight.value }
  for (const entry of props.references ?? []) {
    for (const slice of entry.scopeRecord?.channels ?? []) {
      if (scopeChannelDrawn(slice.channel, shown, captured)) slices.push(slice)
    }
  }
  return recordExtremes(slices)
})

function autofitVolts() {
  const measured = extremes.value
  if (!measured) return
  const fit = fitVoltWindow(measured.maxV, measured.minV)
  voltsPerDivision.value = fit.voltsPerDivision
  voltOffsetV.value = fit.offsetV
}

/** The whole record, which is what a scope's horizontal autoset lands on. */
function autofitTime() {
  const span = bounds.value
  commitWindow(span.fromS, span.toS)
}

/**
 * One Autofit per chart, not one per axis. The time window is fitted first, so the vertical fit is
 * taken over the record the axis is about to show rather than over whatever the old window held.
 */
function autofitAll() {
  autofitTime()
  autofitVolts()
}

/*
 * Nothing fits a finished capture on its own. There was an "Autofit when measurement done"
 * checkbox here until 2026-11-19, and it is gone -- control and behaviour both.
 *
 * This screen already has three deliberate ways to frame a trace: Autofit, Autoset and the zoom
 * bar. An automatic fit fights all three, because it throws away a window the reader has just
 * chosen, at the one moment they are looking at it. chartAutofitOnDone stays readable in a stored
 * params_json and is simply not read here -- the ChartDecayLightAzimuthDeg precedent.
 */


/** Which figure stands for each drawing mode — see scope/displayIcons.ts. */
const DRAWING_ICONS: Record<ScopeDrawing, string> = {
  Linear: 'drawLines',
  Sinc: 'drawSinc',
  Dots: 'drawDots',
}

/**
 * The three plain on/off toggles, as data.
 *
 * <p>A list rather than three copies of the same markup, so the button, its figure, its name and
 * its tooltip are declared together and a fourth cannot be added with one of them missing. The
 * <code>label</code> is the <code>aria-label</code> — what a screen reader hears in place of the
 * label text these buttons no longer carry — and the <code>title</code> is the longer sentence a
 * checkbox never had room for.</p>
 */
const showToggles = computed(() => [
  {
    icon: 'triggerLevel',
    label: 'Trigger level',
    title: 'Trigger level — draws the level line and its hysteresis band over the trace, with the grip that drags it.',
    on: showTriggerLevel.value,
    toggle: () => {
      showTriggerLevel.value = !showTriggerLevel.value
    },
  },
  {
    icon: 'residual',
    label: 'Residual',
    title:
      'Residual — draws each channel again with its fitted fundamental subtracted, so what is left is the distortion and the noise. The fit is a least-squares sine with its frequency refined, so a tone off a bin cancels too; a record with no fundamental has nothing to remove and nothing is drawn.',
    on: showResidual.value,
    toggle: () => {
      showResidual.value = !showResidual.value
    },
  },
  {
    icon: 'thickLines',
    label: 'Thick lines',
    title: 'Thick lines — a heavier trace, which is easier to follow across the graticule.',
    on: thickLines.value,
    toggle: () => {
      thickLines.value = !thickLines.value
    },
  },
  {
    icon: 'dataPoints',
    label: 'Show data points',
    title:
      'Show data points — a marker on every captured sample. Drawn only where the window is tight enough for the chart to hold real samples, because a marker on a min/max column would mark the column.',
    on: showDataPoints.value,
    toggle: () => {
      showDataPoints.value = !showDataPoints.value
    },
  },
])

/**
 * The two cursor pairs, as buttons rather than tick boxes.
 *
 * <p>Separate from <code>showToggles</code> although they are drawn the same way, because they
 * belong in the Cursors section with the boxes and the readings they are about — and because each
 * one <em>places</em> its pair on the way on rather than merely setting a flag.</p>
 */
const cursorToggles = computed(() => [
  {
    icon: 'cursorTime',
    label: 'Time cursors',
    title:
      'Time cursors — two vertical lines reading the time between them and its reciprocal. While they are on, the readings under the chart are of the stretch of the record between them rather than of the whole window.',
    on: cursors.value.timeOn,
    toggle: () => toggleTimeCursors(!cursors.value.timeOn),
  },
  {
    icon: 'cursorLevel',
    label: 'Level cursors',
    title:
      'Level cursors — two horizontal lines reading the level difference between them. They gate nothing: a pair of levels names no stretch of the record. With both pairs on, the readout also carries ΔV/Δt.',
    on: cursors.value.voltOn,
    toggle: () => toggleVoltCursors(!cursors.value.voltOn),
  },
])

/**
 * The persistence ladder as a row of buttons.
 *
 * <p>The <b>short</b> label, because five of them share the rail's width; the long one is the
 * tooltip, so "32" still says "32 records" to anyone who asks.</p>
 */
const persistenceOptions = computed(() =>
  SCOPE_PERSISTENCE_OPTIONS.map((option) => ({
    value: option.value,
    label: option.shortLabel,
    title: option.label,
  })),
)

const intensityOptions = computed(() =>
  SCOPE_INTENSITY_MODES.map((option) => ({ value: option.value, label: option.label })),
)

/**
 * The coupling as a pair of buttons.
 *
 * <p>Written through a computed rather than bound directly, because the model is the whole path
 * object: the setter replaces it, which is what the <code>@change</code> handler did before and
 * what keeps a future second field in the path from being dropped by a write to this one.</p>
 */
const coupling = computed<ScopeSignalPathSettings['coupling']>({
  get: () => path.value.coupling,
  set: (value) => {
    path.value = { ...path.value, coupling: value }
  },
})

// Spread rather than picked field by field, so a column added to the table — the figure and the
// hue were — reaches the control without a second edit here.
const couplingOptions = computed(() => SCOPE_COUPLINGS.map((option) => ({ ...option })))

const verticalLabel = computed(() => formatVoltsPerDivision(voltsPerDivision.value))

/** The window's own reading, so a zoom can be seen in the rail as well as on the chart. */
const windowSpan = computed(() => {
  const span = Math.abs(timeToS.value - timeFromS.value)
  const scale = scopeTimeScale(toChartMs(span))
  return `${(toChartMs(span) / scale.msPerUnit).toPrecision(3)} ${scale.label}`
})


// ---------------------------------------------------------------------------
// Cursors
// ---------------------------------------------------------------------------

/**
 * Switching a pair on, which also <b>places it where it can be seen</b>.
 *
 * <p>The defaults are both cursors at zero, and a pair left over from another measurement may be
 * off this record entirely — either way there is nothing to read and nothing to grab. See
 * <code>cursorPairNeedsPlacing</code>; the placement happens only on the way <em>on</em>, so a pair
 * the reader has put somewhere is never moved for them.</p>
 */
function toggleTimeCursors(on: boolean) {
  const next: ScopeCursors = { ...cursors.value, timeOn: on }
  if (on && cursorPairNeedsPlacing(next.timeAS, next.timeBS, timeFromS.value, timeToS.value)) {
    const placed = placeCursorPair(timeFromS.value, timeToS.value)
    next.timeAS = placed.a
    next.timeBS = placed.b
  }
  cursors.value = next
  emit('cursorsCommitted')
}

function toggleVoltCursors(on: boolean) {
  const next: ScopeCursors = { ...cursors.value, voltOn: on }
  const level = voltWindow(voltsPerDivision.value, voltOffsetV.value)
  if (on && cursorPairNeedsPlacing(next.voltAV, next.voltBV, level.bottomV, level.topV)) {
    const placed = placeCursorPair(level.bottomV, level.topV)
    next.voltAV = placed.a
    next.voltBV = placed.b
  }
  cursors.value = next
  emit('cursorsCommitted')
}

/** A cursor written from a box: replaced, never mutated, and committed, a typed figure being a commit. */
function setCursor(field: 'timeAS' | 'timeBS' | 'voltAV' | 'voltBV', value: number) {
  cursors.value = { ...cursors.value, [field]: value }
  emit('cursorsCommitted')
}

/**
 * The mask's time tolerance in the same unit as every other time box on this rail.
 *
 * <p>Clamped at zero on the way in rather than bounded by a <code>min</code> attribute: a `min`
 * anchors the spinner's own step grid, which is this project's documented number-input trap, and a
 * negative tolerance is not a narrower band but no band at all.</p>
 */
const maskToleranceBoxValue = computed(() => toBox(maskToleranceS.value))

function onMaskTimeChange(event: Event) {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed) || typed < 0) {
    ;(event.target as HTMLInputElement).value = String(maskToleranceBoxValue.value)
    return
  }
  maskToleranceS.value = fromBox(typed)
}

function clampMaskTolerance(event: Event, _unit: 'volts') {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed) || typed < 0) {
    maskToleranceV.value = 0
    ;(event.target as HTMLInputElement).value = '0'
  }
}

/** The time boxes are in the window boxes' own unit, so the four figures can be compared by eye. */
const cursorABoxValue = computed(() => toBox(cursors.value.timeAS))
const cursorBBoxValue = computed(() => toBox(cursorTimeBBox(cursors.value)))

/** Whether B is typed as a span from A — the Siglent's X2-X1. See scope/cursors.ts. */
const cursorBLabel = computed(() => (cursors.value.timeBIsSpan ? `B − A (${boxScale.value.label})` : `B (${boxScale.value.label})`))

function toggleCursorBSpan() {
  cursors.value = { ...cursors.value, timeBIsSpan: !cursors.value.timeBIsSpan }
}

/**
 * Which traces the level pair can follow under Track.
 *
 * <p>Only what is on screen, because a square can only be drawn on a line that is drawn — but the
 * chosen source is never silently swapped for another, so a source that goes away leaves the
 * reading absent and says so rather than tracking something else.</p>
 */
const trackSourceOptions = computed(() => {
  const options: { value: string; label: string }[] = []
  if (showLeft.value) options.push({ value: 'ch0', label: 'Left' })
  if (showRight.value) options.push({ value: 'ch1', label: 'Right' })
  for (const slot of SCOPE_MATH_SLOTS) {
    if (mathSpecDraws(maths.value[slot - 1])) options.push({ value: `math${slot}`, label: `Math ${slot}` })
  }
  return options
})

/** True where Track is on and the chosen trace is not among them. */
const trackSourceMissing = computed(
  () => cursorsTrack(cursors.value) && !trackSourceOptions.value.some((o) => o.value === cursors.value.trackSource),
)

function onTrackSourceChange(event: Event) {
  cursors.value = { ...cursors.value, trackSource: (event.target as HTMLSelectElement).value }
}

function onCursorModeChange(mode: ScopeCursorMode) {
  cursors.value = { ...cursors.value, mode }
  emit('cursorsCommitted')
}

function onCursorTimeChange(event: Event, field: 'timeAS' | 'timeBS') {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(
      field === 'timeAS' ? cursorABoxValue.value : cursorBBoxValue.value,
    )
    return
  }
  // Not clamped to the window, deliberately: a cursor off the window is drawn nowhere and still
  // reads its own position, and clamping would silently move a pair a zoom is about to bring back.
  setCursor(field, field === 'timeBS' ? cursorTimeBFromBox(cursors.value, fromBox(typed)) : fromBox(typed))
}

function onCursorVoltChange(event: Event, field: 'voltAV' | 'voltBV') {
  const typed = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(typed)) {
    ;(event.target as HTMLInputElement).value = String(cursors.value[field])
    return
  }
  setCursor(field, typed)
}

/** One press of a level cursor's spinner: a tenth of a division, which is how finely one is placed. */
const cursorVoltStep = computed(() => voltsPerDivision.value / 10)

/**
 * What the pairs read — Δt, 1/Δt, ΔV and the slope.
 *
 * <p><code>cursorBoxRows</code> rather than <code>cursorReadings</code>, so this table and the
 * chart's Fixed box are literally the same list: that is the rule those two functions were written
 * to keep, and the slope was the one row the rail was missing — which is the headline reading under
 * Track.</p>
 */
const cursorRows = computed(() =>
  cursorBoxRows(
    cursors.value,
    voltsPerDivision.value,
    Math.abs(toChartMs(timeToS.value) - toChartMs(timeFromS.value)),
  ),
)

/** Snapped on the way in, so a window arriving from a drag-zoom's "Set axis" lands on a rung. */
function setVoltsPerDivision(volts: number) {
  voltsPerDivision.value = snapVoltsPerDivision(volts)
}

/** One rung of the ladder, from the tile's own two buttons. */
function stepVoltsPerDivision(direction: 1 | -1) {
  setVoltsPerDivision(stepLadder(VOLTS_PER_DIVISION_LADDER, voltsPerDivision.value, direction))
}

/** What the tile and the grid write a rung as: the ladder's own unit, without the `/div` the tile
 *  draws for itself. */
function voltsPerDivisionGridLabel(volts: number): string {
  return formatVoltsPerDivision(volts).replace('/div', '')
}

const verticalValue = computed(() => voltsPerDivisionGridLabel(voltsPerDivision.value))

/**
 * One press of the centre's own buttons is one division, which is how a scope's vertical position
 * knob is read: "bring it down two squares". A fixed step is a different distance at every V/div,
 * and at the bottom of the ladder it is ten thousand of them.
 */
function stepOffset(direction: 1 | -1) {
  const step = voltsPerDivision.value
  if (!Number.isFinite(step) || step <= 0) return
  // Rounded onto the step's own grid, or a centre nudged once from a fitted window carries that
  // window's fraction of a division for ever.
  voltOffsetV.value = roundVoltsForWindow(voltOffsetV.value + direction * step, step * 8)
}
</script>

<template>
  <!-- The chart's one Autofit, at the top of the rail and in no section at all — the same place on
       every screen. -->
  <button
    type="button"
    class="btn-panel-accent"
    title="Fits both axes to the record on screen: the time window to the whole record, the V/div and centre to the trace inside it."
    @click="autofitAll"
  >
    Autofit
  </button>

  <CollapsibleSection
    title="View"
    size="xs"
    hint="The waveform against time, which is an oscilloscope’s ordinary picture; the two inputs against each other as a shape; or every cycle folded on the signal’s own period. One at a time rather than three charts, so whichever is on screen keeps the full height."
  >
    <!-- A scope's mode button, not two charts: the two are the same record drawn two ways and only
         one of them can be read at a time, so a toggle keeps the chart its full height. -->
    <SegmentedToggle
      v-model="chartView"
      group-label="Chart view"
      :options="SCOPE_VIEWS.map((v) => ({ value: v.value, label: v.label, title: v.title }))"
    />
    <!-- No paragraph under the buttons: SegmentedToggle already puts each option’s own title on
         its own button, and that text IS what this said. What the three views are is the panel’s
         hint. -->
    <p v-if="xyOn && !bothCaptured" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
      Nothing to draw: an X–Y figure needs both inputs. Tick them under Acquisition ▸ Channels.
    </p>
  </CollapsibleSection>

  <CollapsibleSection
    title="Vertical"
    size="xs"
    :hint="`Eight divisions, so the window is ${verticalLabel} × 8 centred on the figure beside it.`"
  >
    <!-- Stacked rather than side by side, which is the one place this departs from the design and
         is forced by the rail: Joost drew the pair at half width over a panel wider than this one,
         where half of 226 px leaves **26 px** between the two buttons — measured, and not enough
         for the caption, let alone the value. Full width gives each tile the room the design's own
         full-width tiles have. -->
    <div class="flex flex-col gap-2.5">
      <StepperTile
        label="V/div"
        tone="violet"
        :display="verticalValue"
        :ladder="VOLTS_PER_DIVISION_LADDER"
        :value="voltsPerDivision"
        :format="voltsPerDivisionGridLabel"
        title="How many volts one vertical division is worth. Eight of them make the window, centred on the figure beside it."
        auto-title="Autoscale: fits the vertical to the whole of what is drawn — the checked history items included — and centres the window on it."
        @step="stepVoltsPerDivision"
        @pick="setVoltsPerDivision"
        @auto="autofitVolts"
      />
      <!-- The centre has no ladder and keeps its own box: it is a continuous voltage, so it has to
           stay typable, and the draft is what lets it be typed into while records arrive. -->
      <StepperTile
        label="Centre (V)"
        tone="violet"
        title="Where the middle of the graticule sits. A DUT with a DC offset is brought back on screen with this rather than by widening the V/div. The buttons move it one division at a time."
        @step="stepOffset"
      >
        <input
          :value="drafts.show('offset', voltOffsetV)"
          type="number"
          :step="voltsPerDivision"
          class="w-full appearance-none border-0 bg-transparent p-0 text-[15px] font-semibold tabular-nums shadow-none outline-none focus:ring-0 [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
          :style="{ color: 'var(--kind-violet-chip-fg)' }"
          @input="drafts.type('offset', $event)"
          @change="drafts.end('offset'); onOffsetChange($event)"
          @blur="drafts.end('offset')"
        />
      </StepperTile>
    </div>
  </CollapsibleSection>

  <!-- The window is the zoom bar's, and these boxes are gone with it (Joost, 2026-11-21): the bar
       is under the chart on every waveform screen, it moves the same one value, and the two of them
       spent a section of a rail whose whole problem is height saying the same thing twice.

       Except in the X–Y and Eye views, which have no zoom bar at all — their x axis is volts and a
       phase of the period, so a navigator over time is hidden there. These boxes are then the only
       way to say which stretch of the record is plotted, and taking the last control off a view is
       not what was asked for. -->
  <CollapsibleSection
    v-if="planeShowing"
    :title="`Time window (${boxScale.label})`"
    panel-id="time-window"
    size="xs"
    :hint="`Which stretch of the record the figure is plotted from — ${windowSpan}. The waveform view has the zoom bar for this; these two views have no time axis to put one on.`"
  >
    <!-- Both ends relative to the trigger at t = 0, negative to its left — the same convention the
         decay surfaces' head start uses, and the one the server's own fromS/toS carry. -->
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        From
        <input
          :value="drafts.show('from', fromBoxValue)"
          type="number"
          :step="boxStep"
          class="tb-input"
          title="Negative is before the trigger."
          @input="drafts.type('from', $event)"
          @change="drafts.end('from'); onFromChange($event)"
          @blur="drafts.end('from')"
        />
      </label>
      <label class="tb-field">
        To
        <input
          :value="drafts.show('to', toBoxValue)"
          type="number"
          :step="boxStep"
          class="tb-input"
          @input="drafts.type('to', $event)"
          @change="drafts.end('to'); onToChange($event)"
          @blur="drafts.end('to')"
        />
      </label>
    </div>
  </CollapsibleSection>

  <!-- Icon buttons rather than a column of checkboxes (Joost, 2026-11-19). Six labelled rows is
       the most legible thing there is and also the tallest, on a rail whose whole problem is
       height; these put the same six controls in three short rows. The name a label carried is the
       button's own tooltip AND its aria-label, so nothing is lost to a reader who hovers or to a
       screen reader, only to one who is scanning -- which is what the figure is for. Lit is the
       accent and unlit is the track's own recess, the idiom .btn-toggle-off already carries, so
       the state does not rest on colour alone and survives a monochrome screenshot. -->
  <CollapsibleSection
    title="Display"
    hint="What the chart draws and how. Data points appear only where the window is tight enough for the chart to hold real samples, because a marker on a min/max column would mark the column rather than a sample."
  >
    <div class="tb-field">
      <span class="tb-group">Channels</span>
      <!-- Which captured inputs are *drawn*. The same control as Acquisition's "which are captured"
           and the Generator's "which play", because they are one question asked three times. -->
      <ChannelPair
        v-model:left="showLeft"
        v-model:right="showRight"
        group-label="Draw"
        :left-disabled="!captured.left"
        :right-disabled="!captured.right"
        left-disabled-title="Not being captured — tick L under Acquisition ▸ Channels."
        right-disabled-title="Not being captured — tick R under Acquisition ▸ Channels."
      />
    </div>

    <div class="tb-field">
      <span class="tb-group">Drawing</span>
      <div class="tb-segment-track grid grid-cols-3">
        <!-- NOT v-once, although the list behind it is frozen: that is right for an <option>,
             whose rendered content never depends on state, and wrong for a button whose lit class
             and aria-pressed do. Tried it; the group rendered once and then looked dead while the
             mode underneath it changed. There is nothing to gain here anyway -- the flicker
             v-once answers is an open select popup being re-laid out, which a row of buttons has
             no equivalent of. -->
        <button
          v-for="option in SCOPE_DRAWINGS"
          :key="option.value"
          type="button"
          class="tb-segment flex items-center justify-center"
          :class="[
            drawing === option.value ? 'tb-segment-on' : '',
            // Chosen but not in force: the slice is min/max columns, so Sinc and Dots fall back to
            // lines. Dimmed rather than captioned — see drawingInForce.
            drawing === option.value && !drawingInForce ? 'opacity-50' : '',
          ]"
          :aria-pressed="drawing === option.value"
          :aria-label="option.label"
          :title="
            drawing === option.value && !drawingInForce
              ? option.label +
                ' — lines for now: this window is wide enough that the analyzer is sending both extremes of each column rather than the samples. Zoom in until the chart header says every sample is drawn.'
              : option.label + ' — ' + option.title
          "
          @click="drawing = option.value"
        >
          <ScopeToggleIcon :name="DRAWING_ICONS[option.value]" />
        </button>
      </div>
    </div>

    <div class="tb-field">
      <span class="tb-group">Show</span>
      <div class="flex flex-wrap gap-1.5">
        <button
          v-for="toggle in showToggles"
          :key="toggle.icon"
          type="button"
          class="flex h-[34px] w-[34px] items-center justify-center rounded-lg border transition"
          :class="toggle.on ? 'btn-action' : 'btn-toggle-off'"
          :aria-pressed="toggle.on"
          :aria-label="toggle.label"
          :title="toggle.title"
          @click="toggle.toggle()"
        >
          <ScopeToggleIcon :name="toggle.icon" />
        </button>
      </div>
    </div>
  </CollapsibleSection>


  <CollapsibleSection
    title="Signal path"
    size="xs"
    hint="Runs on the analyzer, over the whole record, before anything is drawn or measured — so the picture and every figure beside it are of one waveform. Every level reading below is of the coupled trace, moved by the record’s own mean; what is stored is always the capture as it arrived."
  >
    <!-- It runs on the analyzer, over the whole record, before anything is drawn or measured:
         doing it on the client would change the picture and leave every reading describing
         something else. See scope/signalPath.ts, which also records what the in-path filter that
         stood here until 2026-11-18 got wrong, and what rebuilding it properly needs. -->
    <div class="tb-field">
      <span class="tb-group">Coupling</span>
      <SegmentedToggle v-model="coupling" :options="couplingOptions" group-label="Coupling" />
    </div>

  </CollapsibleSection>

  <CollapsibleSection
    title="Math"
    size="xs"
    hint="Two channels, each combining two inputs you choose. Each is drawn against its own axis on the right — violet for Math 1, pink and dashed for Math 2 — because a derived quantity is frequently not in volts and, even when it is, routinely far smaller than what it came from."
  >
    <!-- One button per channel, each showing what it computes and opening its own dialog. The
         derived waveform is computed server-side on the samples, never on the drawn columns: the
         difference of two envelopes is not the envelope of a difference, and on a differential
         pair that error is the entire reading. See scope/math.ts. -->
    <template v-for="row in mathRows" :key="row.slot">
      <button
        type="button"
        class="flex h-[34px] w-full items-center gap-2 rounded-lg border px-2 text-left text-[12px] transition"
        :class="row.on ? 'btn-action' : 'btn-toggle-off'"
        :aria-pressed="row.on"
        :aria-label="`Math ${row.slot}`"
        :title="row.on ? row.title : 'No trace. Press to choose an operation and its two inputs.'"
        @click="mathDialogSlot = row.slot"
      >
        <span class="shrink-0 font-semibold">Math {{ row.slot }}</span>
        <span class="min-w-0 flex-1 truncate text-right">{{ row.label }}</span>
      </button>

      <template v-if="row.on">
        <p v-if="row.missing.length" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
          Math {{ row.slot }} has nothing to draw: it needs the
          {{ row.missing.join(' and the ') }} input, which this measurement is not capturing. Tick
          it under Acquisition ▸ Channels.
        </p>
        <p v-else-if="row.clamped > 0" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
          Math {{ row.slot }}: {{ row.clamped.toLocaleString() }} samples railed at ±1000 — the
          denominator passes through zero there. A ratio is only meaningful where B is a settled
          level; for a transfer function use the frequency-response measurement.
        </p>
      </template>
    </template>

    <ScopeMathDialog
      v-if="mathDialogSlot"
      :open="mathDialogSlot !== null"
      :slot="mathDialogSlot"
      :spec="mathDialogSpec"
      :captured-left="captured.left"
      :captured-right="captured.right"
      @close="mathDialogSlot = null"
      @apply="onMathApply"
    />
  </CollapsibleSection>

  <CollapsibleSection
    title="Persistence"
    size="xs"
    hint="How often the trace passes through a pixel decides how it is painted, so a steady waveform burns in bright and jitter, noise or a once-in-a-while glitch shows as a haze around it. The newest record is always drawn on top at full strength."
  >
    <!-- A history of a *display*, not of a capture: the setting is stored with the measurement, and
         what it accumulates is thrown away whenever a pixel would stop meaning the same time and
         the same level. -->
    <!-- Buttons rather than a dropdown: five rungs of one ladder that a reader turns up and down
         while watching the picture, which is exactly what a segmented control is for — and the
         short labels are why the table carries two (see scope/persistence.ts). -->
    <div class="tb-field">
      <span class="tb-group">Keep</span>
      <SegmentedToggle v-model="persistence" :options="persistenceOptions" group-label="Persistence" />
    </div>

    <template v-if="persistenceOn">
      <div class="tb-field">
        <span class="tb-group">Colour</span>
        <SegmentedToggle v-model="intensityMode" :options="intensityOptions" group-label="Persistence colour" />
      </div>
      <label v-if="intensityMode === 'Multi'" class="tb-field">
        Map
        <select v-model="intensityPalette" class="tb-input">
          <!-- v-once, as Math's list. -->
          <option v-for="name in PALETTE_NAMES" v-once :key="name" :value="name">{{ name }}</option>
        </select>
      </label>
      <!-- Inside the section the picture belongs to, not up beside Autofit: Autofit is one action
           for every axis of the chart, and this one acts on a single display. Offered only while
           something is being kept, like the colour controls above it — with nothing accumulated
           there is nothing a press could do, and a button that is always dead reads as a fault. -->
      <button
        type="button"
        class="btn-panel-accent"
        title="Throws away what has been accumulated and starts again from the next record. The newest trace is unaffected: it is drawn in full whatever the haze holds."
        @click="emit('clearPersistence')"
      >
        Clear persistence
      </button>
    </template>

  </CollapsibleSection>

  <CollapsibleSection
    title="Cursors"
    size="xs"
    hint="Two pairs of measuring lines, dragged by the lettered grips on the chart — time along the bottom, level down the left. The time pair also gates the readings under the chart to the stretch between them, re-measured on the samples in it."
  >
    <!-- A cursor is a transient reading that belongs to the session, not a note that belongs to the
         measurement - so none of this is in params_json, and none of it is in a report. -->
    <!-- Which of the two the figures are written in. A preference, unlike the positions beside it:
         see scope/cursorOverlay.ts. -->
    <div class="tb-field">
      <span class="tb-group">Style</span>
      <SegmentedToggle v-model="cursorStyle" :options="SCOPE_CURSOR_STYLES" group-label="Cursor style" />
    </div>

    <!-- Placed by hand, or following the trace. Not v-model, because switching to Track changes
         what the level figures *are* and the parent re-gates on it. -->
    <div class="tb-field">
      <span class="tb-group">Mode</span>
      <SegmentedToggle
        :model-value="cursors.mode"
        :options="SCOPE_CURSOR_MODES"
        group-label="Cursor mode"
        @update:model-value="onCursorModeChange"
      />
    </div>

    <label v-if="cursors.mode === 'Track'" class="tb-field">
      Follows
      <select :value="cursors.trackSource" class="tb-input" @change="onTrackSourceChange">
        <option v-for="option in trackSourceOptions" :key="option.value" :value="option.value">
          {{ option.label }}
        </option>
        <!-- The chosen source stays selectable while it is off screen, so turning its trace back on
             puts the reading back rather than having quietly moved to another trace. -->
        <option v-if="trackSourceMissing" :value="cursors.trackSource">
          {{ cursors.trackSource }} (not drawn)
        </option>
      </select>
    </label>
    <p v-if="trackSourceMissing" class="text-[11px] leading-snug text-amber-600 dark:text-amber-400">
      Nothing to follow: that trace is not on the chart. Draw it, or pick one that is.
    </p>

    <!-- The two pairs, as the Show group's buttons: the name a label carried is the button's own
         tooltip and its aria-label, so nothing is lost to a reader who hovers or to a screen
         reader - only to one who is scanning, which is what the figure is for. -->
    <div class="tb-field">
      <span class="tb-group">Cursors</span>
      <div class="flex flex-wrap gap-1.5">
        <button
          v-for="toggle in cursorToggles"
          :key="toggle.icon"
          type="button"
          class="flex h-[34px] w-[34px] items-center justify-center rounded-lg border transition"
          :class="toggle.on ? 'btn-action' : 'btn-toggle-off'"
          :aria-pressed="toggle.on"
          :aria-label="toggle.label"
          :title="toggle.title"
          @click="toggle.toggle()"
        >
          <ScopeToggleIcon :name="toggle.icon" />
        </button>
      </div>
    </div>

    <div v-if="cursors.timeOn" class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        A ({{ boxScale.label }})
        <input
          :value="drafts.show('cursorA', cursorABoxValue)"
          type="number"
          :step="boxStep"
          class="tb-input"
          @input="drafts.type('cursorA', $event)"
          @change="drafts.end('cursorA'); onCursorTimeChange($event, 'timeAS')"
          @blur="drafts.end('cursorA')"
        />
      </label>
      <label class="tb-field">
        <span class="tb-field-row">
          {{ cursorBLabel }}
          <!-- One press switches B between a position and a span from A. A pair is placed either to
               mark two places or to mark one interval; in the second case the figure the reader has
               in mind is the interval. -->
          <button
            type="button"
            class="text-[11px] font-semibold text-blue-600 hover:underline dark:text-blue-400"
            :title="
              cursors.timeBIsSpan
                ? 'B is typed as the span from A. Press to type it as a position instead.'
                : 'B is typed as a position. Press to type it as the span from A instead — the figure you usually have in mind.'
            "
            @click="toggleCursorBSpan"
          >
            {{ cursors.timeBIsSpan ? 'Δ' : 'abs' }}
          </button>
        </span>
        <input
          :value="drafts.show('cursorB', cursorBBoxValue)"
          type="number"
          :step="boxStep"
          class="tb-input"
          @input="drafts.type('cursorB', $event)"
          @change="drafts.end('cursorB'); onCursorTimeChange($event, 'timeBS')"
          @blur="drafts.end('cursorB')"
        />
      </label>
    </div>

    <p v-if="cursorsTrack(cursors)" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      The level cursors follow the trace at A and B, with a square drawn where each one sits — so
      they are read rather than placed. The level button draws their lines as well.
    </p>

    <div v-if="cursors.voltOn && !cursorsTrack(cursors)" class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        A (V)
        <input
          :value="drafts.show('voltA', cursors.voltAV)"
          type="number"
          :step="cursorVoltStep"
          class="tb-input"
          @input="drafts.type('voltA', $event)"
          @change="drafts.end('voltA'); onCursorVoltChange($event, 'voltAV')"
          @blur="drafts.end('voltA')"
        />
      </label>
      <label class="tb-field">
        B (V)
        <input
          :value="drafts.show('voltB', cursors.voltBV)"
          type="number"
          :step="cursorVoltStep"
          class="tb-input"
          @input="drafts.type('voltB', $event)"
          @change="drafts.end('voltB'); onCursorVoltChange($event, 'voltBV')"
          @blur="drafts.end('voltB')"
        />
      </label>
    </div>

    <table v-if="cursorRows.length > 0" class="w-full text-left text-[11px]">
      <tbody>
        <tr v-for="row in cursorRows" :key="row.label">
          <td class="pr-2" :title="row.title">{{ row.label }}</td>
          <td class="font-mono tabular-nums">{{ row.value }}</td>
        </tr>
      </tbody>
    </table>
  </CollapsibleSection>

  <CollapsibleSection
    title="Mask"
    size="xs"
    icon="shield"
    tone="red"
    hint="Judges every record against the first history item ticked, opened out by the two tolerances below — a level, and a time that lets an edge arrive early or late rather than only sit at a different level."
  >
    <label class="tb-check-row">
      <input
        v-model="maskOn"
        type="checkbox"
        class="tb-check"
        title="Judges every record against a band built from the reference on the chart: the reference opened out by the two tolerances below. The chart says pass or fail per record and the count here is of the run."
      />
      Compare against the reference
    </label>

    <p
      v-if="maskOn && !maskReferenceName"
      class="text-[11px] leading-snug text-amber-700 dark:text-amber-300"
    >
      Tick a history item to compare against — the first one checked is the one the band is built
      from.
    </p>
    <p v-else-if="maskOn" class="text-[11px] leading-snug text-neutral-500 dark:text-neutral-400">
      Against <strong>{{ maskReferenceName }}</strong>.
    </p>

    <div v-if="maskOn" class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Level (V)
        <input
          v-model.number="maskToleranceV"
          type="number"
          step="0.01"
          class="tb-input"
          title="How far above and below the reference the trace may go."
          @change="clampMaskTolerance($event, 'volts')"
        />
      </label>
      <label class="tb-field">
        Time ({{ boxScale.label }})
        <input
          :value="drafts.show('maskTime', maskToleranceBoxValue)"
          type="number"
          :step="boxStep"
          class="tb-input"
          title="How far early or late the trace may be. This opens the band to the whole height of whatever the reference does within it, so an edge is allowed to move rather than only to sit at a different level."
          @input="drafts.type('maskTime', $event)"
          @change="drafts.end('maskTime'); onMaskTimeChange($event)"
          @blur="drafts.end('maskTime')"
        />
      </label>
    </div>

    <p
      v-if="maskOn && maskTally && maskTally.passed + maskTally.failed > 0"
      class="text-[11px] leading-snug"
      :class="
        maskTally.failed > 0
          ? 'text-red-700 dark:text-red-300'
          : 'text-emerald-700 dark:text-emerald-300'
      "
    >
      {{ maskTally.passed.toLocaleString() }} passed, {{ maskTally.failed.toLocaleString() }} failed
      — of the records this screen read.
    </p>
  </CollapsibleSection>

  <AnnotationsPanel :view="OSCILLOSCOPE_ANNOTATION_VIEW" />
</template>

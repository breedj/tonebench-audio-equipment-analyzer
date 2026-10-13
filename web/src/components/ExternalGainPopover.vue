<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { displayPrecision, fromDisplay, toDisplay, type GainUnit } from '../soundcard/gainUnits'
import PanelIcon from './PanelIcon.vue'

/**
 * The two external-gain boxes and their unit selector, as a small panel above the status bar.
 *
 * <p><b>Why a popover rather than two boxes on the bar.</b> The gain is edited rarely — when the
 * bench is re-wired — but has to be <i>visible</i> constantly, because a divider the bench has
 * forgotten about makes every reported level wrong by its whole size. Those two pull opposite ways,
 * and trying to satisfy both with one widget is what previously put an editable copy on the bar and
 * a second, read-only copy of the same number a few pixels away. The badge answers the visibility
 * half; this answers the editing half, and the number exists once.</p>
 *
 * <p>It edits whichever analyzer is live — each bench stores its own gain, and the routing is in
 * App.vue's <c>setExternalGain</c>. This component is told nothing about that: it is handed the
 * effective figures and emits the new ones.</p>
 *
 * <p>Positioned by the caller (absolute, bottom-anchored over the bar) rather than here, because
 * only the bar knows where its own badge is.</p>
 *
 * <p>Since the 2026-10-12 redesign it is an ordinary panel — the tinted round icon, the 14 px title
 * and the 12 px radius every other panel carries — lifted off the bar by its shadow rather than by
 * being a different colour. It used to be painted in <b>ground</b> for that job, which was the one
 * place in the app where something on top of a surface was darker than it; the shadow says the same
 * thing without the exception, and the footer strip is the ground tone instead.</p>
 */
const props = defineProps<{
  leftDb: number
  rightDb: number
  unit: GainUnit
  /** Which bench is being edited, for the line at the foot. */
  benchLabel: string
  /** Device changes are refused mid-run, so the fields say so rather than failing on commit. */
  disabled?: boolean
}>()

const emit = defineEmits<{
  apply: [gain: { leftDb: number; rightDb: number; unit: GainUnit }]
  close: []
}>()

/**
 * Drafts rather than direct binding, for the reason the Configuration dialog's fields already
 * carry: the status poll replaces the backing numbers about once a second, so a bound input is
 * overwritten mid-type — "-2" on its way to "-20" becomes 0 before it can be committed.
 */
const leftDraft = ref('')
const rightDraft = ref('')
const leftInput = ref<HTMLInputElement | null>(null)

/** Reseeded from the props whenever they change, which includes the unit being switched. */
watch(
  () => [props.leftDb, props.rightDb, props.unit].join('|'),
  () => {
    leftDraft.value = show(props.leftDb)
    rightDraft.value = show(props.rightDb)
  },
  { immediate: true },
)

// Opened by a press, so the first box takes the caret: the whole point of the panel is to type in
// it, and reaching for the mouse again to do that is a step the press already expressed.
void nextTick(() => leftInput.value?.select())

function show(db: number): string {
  return String(Number(toDisplay(db, props.unit).toFixed(displayPrecision(props.unit))))
}

/**
 * A field the user did not touch keeps its stored dB **exactly**, rather than being re-derived from
 * what the box shows.
 *
 * <p>The box shows a rounded figure — four decimals of a ratio — so `fromDisplay(show(db))` is not
 * `db`. Under the ratio unit a stored −6 dB displays as ×0.5012 and comes back as −5.99978, which
 * is nothing physically and still wrong in two ways that matter: opening the panel and pressing
 * Enter would alter the stored value, and toggling the unit a few times would walk it. Comparing
 * the draft against what the prop renders is what makes an untouched field a no-op, and it has to
 * be a string comparison in the display unit rather than a numeric one in dB, since that is the
 * only place the two are expressed the same way.</p>
 */
function commit() {
  emit('apply', {
    leftDb: leftDraft.value === show(props.leftDb) ? props.leftDb : fromDisplay(Number(leftDraft.value), props.unit),
    rightDb: rightDraft.value === show(props.rightDb) ? props.rightDb : fromDisplay(Number(rightDraft.value), props.unit),
    unit: props.unit,
  })
}

/**
 * Switching the unit commits the <i>stored</i> dB unchanged and only changes how it is read — so a
 * −20 dB divider shown as ×0.1 is still the same divider. Reading the drafts here instead would
 * reinterpret a half-typed number in the new unit.
 */
function changeUnit(event: Event) {
  emit('apply', {
    leftDb: props.leftDb,
    rightDb: props.rightDb,
    unit: (event.target as HTMLSelectElement).value as GainUnit,
  })
}
</script>

<template>
  <!-- Laid out as Joost's "External gain popover" mockup of 2026-10-12: the panel chrome every other
       panel carries (tinted round icon, 14 px title, 12 px radius), the sentence under it, the two
       boxes and the unit on one row, and which bench they belong to on a footer strip. -->
  <div
    class="flex w-[340px] flex-col rounded-xl border border-neutral-200 shadow-xl dark:border-neutral-800"
    :style="{ backgroundColor: 'var(--surface-panel)' }"
    role="dialog"
    aria-label="External gain"
    @keydown.esc.stop="emit('close')"
  >
    <div class="flex items-center gap-2.5 py-3 pl-3.5 pr-1.5">
      <PanelIcon icon="sine" tone="teal" />
      <h2 class="m-0 flex-1 text-sm font-semibold text-neutral-900 dark:text-neutral-100">External gain</h2>
      <button
        type="button"
        class="flex h-8 w-8 items-center justify-center rounded-lg text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
        aria-label="Close"
        @click="emit('close')"
      >
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
          <path d="M6 6l12 12M18 6L6 18" />
        </svg>
      </button>
    </div>

    <p class="m-0 px-3.5 pb-3 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
      What sits between the DUT and each input — a 10:1 divider is
      <span class="font-mono text-neutral-900 dark:text-neutral-100">−20 dB</span>, or
      <span class="font-mono text-neutral-900 dark:text-neutral-100">×0.1</span>. Every level shown is
      then the DUT's, with this undone.
    </p>

    <!-- The two boxes carry the channel's own colour on their caption, the way every other pair of
         channel readings in the app does, so "which one is left" is answered without reading. -->
    <div class="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_84px] items-end gap-2.5 px-3.5 pb-3.5">
      <label class="tb-field">
        <span class="inline-flex items-center gap-1.5">
          <span class="h-0.5 w-3 shrink-0 rounded-full bg-blue-600 dark:bg-blue-400"></span>Left
        </span>
        <input
          ref="leftInput"
          type="number"
          class="tb-input h-[34px] text-right font-mono"
          :step="unit === 'Times' ? 0.01 : 0.1"
          :disabled="disabled"
          v-model="leftDraft"
          @change="commit"
          @keyup.enter="commit"
        />
      </label>
      <label class="tb-field">
        <span class="inline-flex items-center gap-1.5">
          <span class="h-0.5 w-3 shrink-0 rounded-full bg-red-600 dark:bg-red-400"></span>Right
        </span>
        <input
          type="number"
          class="tb-input h-[34px] text-right font-mono"
          :step="unit === 'Times' ? 0.01 : 0.1"
          :disabled="disabled"
          v-model="rightDraft"
          @change="commit"
          @keyup.enter="commit"
        />
      </label>
      <label class="tb-field">
        Unit
        <select
          class="tb-input h-[34px]"
          :value="unit"
          :disabled="disabled"
          title="How the two boxes are read. The stored value is dB either way."
          @change="changeUnit"
        >
          <option value="Db">dB</option>
          <option value="Times">×</option>
        </select>
      </label>
    </div>

    <p v-if="disabled" class="m-0 px-3.5 pb-3 text-[12px] text-amber-700 dark:text-amber-400">
      Not while a measurement is running.
    </p>

    <!-- Which bench this is, because the figures are per analyzer and switching swaps them: a
         number edited here does not follow you to the other one. -->
    <div
      class="flex items-center gap-2 rounded-b-xl border-t border-neutral-200 px-3.5 py-2.5 text-[12px] text-neutral-500 dark:border-neutral-800 dark:text-neutral-400"
      :style="{ backgroundColor: 'var(--surface-ground)' }"
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <rect x="3" y="6" width="18" height="12" rx="2" />
        <path d="M7 10h.01M11 10h.01M7 14h10" />
      </svg>
      <span>Stored with</span>
      <span
        class="inline-flex items-center gap-1.5 rounded-full border border-neutral-200 px-2 py-0.5 font-semibold text-neutral-900 dark:border-neutral-700 dark:text-neutral-100"
        :style="{ backgroundColor: 'var(--surface-panel)' }"
      >
        <span class="h-1.5 w-1.5 rounded-full bg-green-500"></span>{{ benchLabel }}
      </span>
    </div>
  </div>
</template>

<script setup lang="ts" generic="T extends string">
/**
 * A one-of-these control whose options carry a figure — the Trigger panel's six.
 *
 * <p><b>Why not a <code>&lt;select&gt;</code>, which the rest of the app uses.</b> A native
 * <code>&lt;option&gt;</code> can carry text and nothing else, and the Trigger panel is the one
 * place on this screen where the name is the worst part of the option: <em>Edge</em>,
 * <em>Pulse width</em>, <em>Window</em>, <em>Dropout</em> and <em>Runt</em> are five words a reader
 * has to parse, where five pictures of what each fires on are scanned. That is the argument
 * <code>scope/measurementIcons.ts</code> already makes about thirty measurement names.</p>
 *
 * <p><b>And not a <code>SegmentedToggle</code></b>, which would be the cheaper answer: five
 * segments of a 300 px rail is 60 px each, and these are settings a reader comes back to rather
 * than flips between — so the selected one deserves its name beside its figure, which only a
 * button-plus-list has room for.</p>
 *
 * <p>Generic over the value type like <code>SegmentedToggle</code>, so <code>v-model</code> keeps a
 * union at the call site rather than widening to string. The index arithmetic is in
 * <code>panels/iconSelect.ts</code>, because nothing in a <code>.vue</code> is testable here and
 * wrapping past a disabled option is exactly the part that is got wrong.</p>
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId } from 'vue'
import ScopeToggleIcon from './ScopeToggleIcon.vue'
import {
  firstEnabledIconSelectIndex,
  lastEnabledIconSelectIndex,
  moveIconSelectIndex,
  openIconSelectIndex,
  type IconSelectOption,
} from '../panels/iconSelect'

const props = defineProps<{
  options: readonly IconSelectOption<T>[]
  /** Names the control for a screen reader. Spelled `groupLabel`, not `ariaLabel`, which collides
   *  with the native attribute when written in kebab case at a call site. */
  groupLabel: string
  disabled?: boolean
}>()

const model = defineModel<T>({ required: true })

const rootRef = ref<HTMLElement | null>(null)
const buttonRef = ref<HTMLButtonElement | null>(null)
const optionRefs = ref<HTMLElement[]>([])
const open = ref(false)
const activeIndex = ref(-1)
const listId = useId()

const selected = computed(() => props.options.find((option) => option.value === model.value) ?? null)

function openList(index: number) {
  if (props.disabled) return
  open.value = true
  activeIndex.value = index
  void nextTick(() => optionRefs.value[activeIndex.value]?.focus())
}

function closeList(focusButton = false) {
  if (!open.value) return
  open.value = false
  activeIndex.value = -1
  if (focusButton) buttonRef.value?.focus()
}

function choose(option: IconSelectOption<T>) {
  if (option.disabled) return
  model.value = option.value
  closeList(true)
}

function onButtonKeydown(event: KeyboardEvent) {
  switch (event.key) {
    case 'ArrowDown':
    case 'ArrowUp':
      event.preventDefault()
      openList(openIconSelectIndex(props.options, model.value))
      break
    case 'Enter':
    case ' ':
      event.preventDefault()
      openList(openIconSelectIndex(props.options, model.value))
      break
  }
}

function move(step: number) {
  const next = moveIconSelectIndex(props.options, activeIndex.value, step)
  if (next < 0) return
  activeIndex.value = next
  optionRefs.value[next]?.focus()
}

function onListKeydown(event: KeyboardEvent) {
  switch (event.key) {
    case 'ArrowDown':
      event.preventDefault()
      move(1)
      break
    case 'ArrowUp':
      event.preventDefault()
      move(-1)
      break
    case 'Home':
      event.preventDefault()
      activeIndex.value = firstEnabledIconSelectIndex(props.options)
      optionRefs.value[activeIndex.value]?.focus()
      break
    case 'End':
      event.preventDefault()
      activeIndex.value = lastEnabledIconSelectIndex(props.options)
      optionRefs.value[activeIndex.value]?.focus()
      break
    case 'Escape':
      event.preventDefault()
      closeList(true)
      break
    case 'Tab':
      closeList()
      break
  }
}

/**
 * Closing when focus leaves the whole control, which is what makes Tab out of the last option
 * behave: the list is a sibling of the button inside one root, so `relatedTarget` still inside it
 * means the control is being moved around in, not left.
 */
function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (!next || !rootRef.value?.contains(next)) closeList()
}

function onDocumentPointerDown(event: PointerEvent) {
  if (!rootRef.value?.contains(event.target as Node)) closeList()
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown))
</script>

<template>
  <div ref="rootRef" class="relative" @focusout="onFocusOut">
    <button
      ref="buttonRef"
      type="button"
      class="tb-input flex items-center gap-2 text-left"
      :class="open ? 'ring-1 ring-blue-500' : ''"
      role="combobox"
      :aria-label="groupLabel"
      :aria-expanded="open"
      :aria-controls="listId"
      :disabled="disabled"
      :title="selected?.title"
      @click="open ? closeList() : openList(openIconSelectIndex(options, model))"
      @keydown="onButtonKeydown"
    >
      <ScopeToggleIcon v-if="selected?.icon" :name="selected.icon" />
      <span
        v-else-if="selected?.glyph"
        class="grid size-4 shrink-0 place-items-center text-[12px] font-bold leading-none"
        aria-hidden="true"
      >
        {{ selected.glyph }}
      </span>
      <span class="min-w-0 flex-1 truncate">{{ selected?.label ?? '—' }}</span>
      <svg
        width="12"
        height="12"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        class="shrink-0 opacity-60"
        aria-hidden="true"
      >
        <path d="m6 9 6 6 6-6" />
      </svg>
    </button>

    <ul
      v-if="open"
      :id="listId"
      role="listbox"
      :aria-label="groupLabel"
      class="absolute left-0 top-full z-30 mt-1 max-h-72 w-full min-w-max overflow-y-auto rounded-lg border border-neutral-200 bg-[var(--surface-panel)] py-1 shadow-lg dark:border-neutral-700"
      @keydown="onListKeydown"
    >
      <li v-for="(option, index) in options" :key="String(option.value)" role="none">
        <button
          :ref="(el) => { if (el) optionRefs[index] = el as HTMLElement }"
          type="button"
          role="option"
          class="flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-[12px] hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:hover:bg-neutral-800"
          :class="option.value === model ? 'font-semibold text-blue-700 dark:text-blue-300' : ''"
          :aria-selected="option.value === model"
          :disabled="option.disabled"
          :title="option.title"
          :tabindex="activeIndex === index ? 0 : -1"
          @click="choose(option)"
          @mouseenter="activeIndex = index"
        >
          <ScopeToggleIcon v-if="option.icon" :name="option.icon" />
          <span
            v-else-if="option.glyph"
            class="grid size-4 shrink-0 place-items-center text-[12px] font-bold leading-none"
            aria-hidden="true"
          >
            {{ option.glyph }}
          </span>
          <span class="flex-1 whitespace-nowrap">{{ option.label }}</span>
        </button>
      </li>
    </ul>
  </div>
</template>

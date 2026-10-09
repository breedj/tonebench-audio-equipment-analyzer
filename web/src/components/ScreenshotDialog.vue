<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import Modal from './Modal.vue'
import { useDateTimeFormat } from '../composables/useDateTimeFormat'
import { useScreenshotSettings } from '../composables/useScreenshotSettings'
import {
  SCREENSHOT_ASPECT_RATIOS,
  SCREENSHOT_DESTINATIONS,
  SCREENSHOT_SIZES,
  screenshotFrameLayout,
  type ScreenshotSettings,
} from '../screenshot'

/**
 * The Screenshot button's questions: a title to print above the chart, whether to print the date
 * below it, the picture's size and aspect ratio, where it goes (clipboard, file, or both), and
 * colour or black and white. Every answer is remembered (useScreenshotSettings — stored with the
 * app's configuration), so a series of screenshots for one document needs them once; the title is
 * kept only while **Save text** is ticked. What the answers do to the picture is `screenshot.ts`'s.
 */
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: []; confirm: [settings: ScreenshotSettings] }>()

const { settings: stored, load } = useScreenshotSettings()
const { format: formatDate, load: loadDateFormat } = useDateTimeFormat()

// Staged: the stored answers are copied in when the dialog opens and written back on OK, so
// Cancel leaves them as they were.
const form = reactive<ScreenshotSettings>({ ...stored.value })

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    await Promise.all([load(), loadDateFormat()])
    Object.assign(form, stored.value)
  },
  { immediate: true },
)

const layout = computed(() => screenshotFrameLayout(form.widthPx, form.aspectRatio, form.title, form.addDate))
const dateExample = computed(() => formatDate(new Date()))

function confirm() {
  emit('confirm', { ...form, title: form.title.trim() })
}
</script>

<template>
  <Modal :open="props.open" title="Screenshot" size="lg" @close="emit('close')">
    <div class="flex flex-col gap-4 text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        The chart on screen as a PNG in the light colour scheme, at the zoom shown —
        {{ layout.width }} × {{ layout.height }} pixels with these settings.
      </p>

      <label class="flex flex-col gap-1">
        <span>Title</span>
        <input
          v-model="form.title"
          type="text"
          placeholder="Printed above the chart; leave empty for none"
          class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @keyup.enter="confirm"
        />
      </label>

      <div class="flex flex-wrap items-center gap-x-6 gap-y-2">
        <label
          class="flex items-center gap-2"
          title="Keeps the title for the next screenshot, so it need not be retyped for every chart of a session."
        >
          <input v-model="form.saveText" type="checkbox" />
          <span>Save text</span>
        </label>
        <label
          class="flex items-center gap-2"
          :title="`Prints the date and time below the chart, as ${dateExample} — the format is chosen in Configuration ▸ General.`"
        >
          <input v-model="form.addDate" type="checkbox" />
          <span>Add date</span>
        </label>
      </div>

      <div class="grid grid-cols-[auto_1fr] items-center gap-x-4 gap-y-3 sm:grid-cols-[auto_1fr_auto_1fr]">
        <span class="whitespace-nowrap">Image size</span>
        <select
          v-model.number="form.widthPx"
          class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        >
          <option v-for="size in SCREENSHOT_SIZES" :key="size.widthPx" :value="size.widthPx">
            {{ size.label }} ({{ size.widthPx }} px)
          </option>
        </select>

        <span class="whitespace-nowrap">Destination</span>
        <select
          v-model="form.destination"
          class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        >
          <option v-for="d in SCREENSHOT_DESTINATIONS" :key="d.id" :value="d.id">{{ d.label }}</option>
        </select>

        <span class="whitespace-nowrap">Aspect ratio</span>
        <select
          v-model="form.aspectRatio"
          class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        >
          <option v-for="r in SCREENSHOT_ASPECT_RATIOS" :key="r.id" :value="r.id">{{ r.label }}</option>
        </select>

        <span class="whitespace-nowrap">Colours</span>
        <div class="flex flex-wrap items-center gap-x-4 gap-y-1">
          <label class="flex items-center gap-2">
            <input v-model="form.monochrome" type="radio" :value="false" />
            <span>Colour</span>
          </label>
          <label
            class="flex items-center gap-2"
            title="For a monochrome printer — line styles are kept, hues become greys."
          >
            <input v-model="form.monochrome" type="radio" :value="true" />
            <span>Black and white</span>
          </label>
        </div>
      </div>

      <div class="flex items-center justify-end gap-2 border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="confirm"
        >
          OK
        </button>
      </div>
    </div>
  </Modal>
</template>

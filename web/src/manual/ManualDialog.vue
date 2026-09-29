<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import {
  MANUAL_EDITIONS,
  manualCounterpart,
  manualEdition,
  manualPageById,
  manualPagesIn,
  manualPagesInGroup,
  type ManualGroup,
} from './pages'
import { useManual } from './useManual'

// Its own <dialog> rather than Modal.vue: the manual needs a two-column layout and far more width
// than that component's max-w-lg card, and it scrolls its content column independently of the
// index beside it. The native element still gives Escape-to-close, a backdrop and focus
// containment for free, exactly as Modal.vue explains.

const { open, pageId, edition, closeManual, showPage, showEdition } = useManual()

const dialogRef = ref<HTMLDialogElement | null>(null)
const contentRef = ref<HTMLElement | null>(null)

const page = computed(() => manualPageById(pageId.value))

// The two manuals are two books, so the sidebar, the contents and the ← → links all stay inside one
// edition; without that, "next" off the last page of the guide walks into the full manual's Basics.
const editionInfo = computed(() => manualEdition(edition.value))
const editionPages = computed(() => manualPagesIn(edition.value))

const pageIndex = computed(() => (page.value ? editionPages.value.indexOf(page.value) : -1))
const previousPage = computed(() => (pageIndex.value > 0 ? editionPages.value[pageIndex.value - 1] : null))
const nextPage = computed(() =>
  pageIndex.value >= 0 && pageIndex.value < editionPages.value.length - 1
    ? editionPages.value[pageIndex.value + 1]
    : null,
)

/** The same page in the other manual, when there is one. Rendered as the banner above every page. */
const counterpart = computed(() => manualCounterpart(page.value))

function pagesIn(group: ManualGroup) {
  return manualPagesInGroup(group)
}

watch(
  () => open.value,
  (isOpen) => {
    const dialog = dialogRef.value
    if (!dialog) return
    if (isOpen && !dialog.open) dialog.showModal()
    else if (!isOpen && dialog.open) dialog.close()
  },
  { immediate: true },
)

// Every navigation starts at the top of the page — without this, jumping from the bottom of a long
// page to a short one lands mid-text with nothing above it. Opening counts as a navigation for the
// same reason, and specifically because a measurement's "?" button can reopen the page that is
// already selected: with only pageId watched, nothing changes and the manual comes back scrolled
// to wherever it was left, which reads as the wrong page having opened.
watch([open, pageId], async ([isOpen]) => {
  if (!isOpen) return
  await nextTick()
  contentRef.value?.scrollTo({ top: 0 })
})

function onCancel(e: Event) {
  // Same reasoning as Modal.vue: Escape is prevented so every dismissal routes through one handler.
  e.preventDefault()
  closeManual()
}

function onBackdropClick(e: MouseEvent) {
  if (e.target === dialogRef.value) closeManual()
}
</script>

<template>
  <dialog
    ref="dialogRef"
    class="m-auto h-[85vh] w-[min(64rem,92vw)] max-w-none rounded-lg border border-neutral-200 bg-[var(--surface-panel)] p-0 text-neutral-900 backdrop:bg-black/40 dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-100"
    @cancel="onCancel"
    @click="onBackdropClick"
  >
    <div class="flex h-full flex-col">
      <div
        class="flex shrink-0 items-center justify-between gap-3 border-b border-neutral-200 px-4 py-3 dark:border-neutral-800"
      >
        <h2 class="flex min-w-0 items-center gap-2 text-sm font-semibold">
          <button
            type="button"
            class="rounded text-neutral-500 hover:text-neutral-900 dark:text-neutral-400 dark:hover:text-neutral-100"
            :class="{ 'pointer-events-none': page === null }"
            @click="showPage(null)"
          >
            {{ editionInfo.title }}
          </button>
          <template v-if="page">
            <span class="text-neutral-300 dark:text-neutral-700">/</span>
            <span class="truncate">{{ page.title }}</span>
          </template>
        </h2>
        <button
          type="button"
          class="shrink-0 text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100"
          aria-label="Close"
          @click="closeManual"
        >
          ✕
        </button>
      </div>

      <div class="flex min-h-0 flex-1">
        <!-- Index. Always present, so a page is never a dead end and the whole manual stays one
             click away from wherever a measurement's own help button dropped you. -->
        <nav class="hidden w-56 shrink-0 overflow-y-auto border-r border-neutral-200 p-3 md:block dark:border-neutral-800">
          <!-- Which manual. Always both, always visible: the simple guide is only useful if it is
               found, and the reader who has outgrown it needs the way back to be just as obvious. -->
          <div class="mb-3 flex rounded border border-neutral-200 p-0.5 text-xs dark:border-neutral-700">
            <button
              v-for="e in MANUAL_EDITIONS"
              :key="e.id"
              type="button"
              class="flex-1 rounded px-1.5 py-1 text-center"
              :class="
                e.id === edition
                  ? 'bg-blue-600 font-medium text-white'
                  : 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800'
              "
              :title="e.blurb"
              @click="showEdition(e.id)"
            >
              {{ e.title }}
            </button>
          </div>
          <button
            type="button"
            class="mb-2 w-full rounded px-2 py-1 text-left text-xs font-medium"
            :class="
              page === null
                ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
                : 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800'
            "
            @click="showPage(null)"
          >
            Contents
          </button>
          <template v-for="group in editionInfo.groups" :key="group">
            <p
              class="mt-3 mb-1 px-2 text-[10px] font-semibold tracking-wide text-neutral-400 uppercase dark:text-neutral-600"
            >
              {{ group }}
            </p>
            <button
              v-for="p in pagesIn(group)"
              :key="p.id"
              type="button"
              class="block w-full rounded px-2 py-1 text-left text-xs"
              :class="
                p.id === pageId
                  ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
                  : 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800'
              "
              @click="showPage(p.id)"
            >
              {{ p.title }}
            </button>
          </template>
        </nav>

        <div ref="contentRef" class="manual-prose min-w-0 flex-1 overflow-y-auto px-6 py-5">
          <!-- The index page. The sidebar carries the same links, but it is hidden on a narrow
               window and it has no room for the one-line summaries. -->
          <template v-if="page === null">
            <h1>QA403 audio analyzer — {{ editionInfo.title.toLowerCase() }}</h1>
            <p>{{ editionInfo.blurb }}</p>
            <p v-if="edition === 'reference'">
              One chapter per measurement, plus the parts every measurement shares. Each measurement
              screen also carries its own <strong>?</strong> button, which opens that measurement's
              chapter directly.
            </p>
            <p v-else>
              Thirteen short pages, in the order it is worth reading them. Nothing here needs any
              maths, and nothing here is a different app — every page names the chapter in the full
              manual that goes further on the same subject.
            </p>
            <p class="manual-edition-invite">
              <template v-if="edition === 'reference'">
                New to audio measurement? The
                <button type="button" class="manual-link" @click="showEdition('guide')">
                  simple guide
                </button>
                covers the same bench in plain language, without the conventions and the measured
                figures.
              </template>
              <template v-else>
                Everything in more detail — every control, every setting, every convention — is in the
                <button type="button" class="manual-link" @click="showEdition('reference')">
                  full manual</button
                >.
              </template>
            </p>
            <template v-for="group in editionInfo.groups" :key="group">
              <h2>{{ group }}</h2>
              <ul class="manual-index">
                <li v-for="p in pagesIn(group)" :key="p.id">
                  <button type="button" class="manual-link" @click="showPage(p.id)">{{ p.title }}</button>
                  <span class="manual-index-summary">{{ p.summary }}</span>
                </li>
              </ul>
            </template>
          </template>

          <template v-else>
            <!-- The cross-edition link. Rendered by the shell rather than written into each page, so
                 both directions exist wherever a counterpart is declared and neither can rot: the
                 page bodies never mention the other manual's page titles. -->
            <p v-if="counterpart" class="manual-edition-switch">
              <template v-if="page.edition === 'reference'">
                Simpler version of this page:
                <button type="button" class="manual-link" @click="showPage(counterpart.id)">
                  {{ counterpart.title }}
                </button>
                <span class="manual-edition-switch-tag">simple guide</span>
              </template>
              <template v-else>
                In full detail:
                <button type="button" class="manual-link" @click="showPage(counterpart.id)">
                  {{ counterpart.title }}
                </button>
                <span class="manual-edition-switch-tag">full manual</span>
              </template>
            </p>

            <component :is="page.component" />

            <div
              class="mt-8 flex items-center justify-between gap-3 border-t border-neutral-200 pt-3 text-xs dark:border-neutral-800"
            >
              <button v-if="previousPage" type="button" class="manual-link" @click="showPage(previousPage.id)">
                ← {{ previousPage.title }}
              </button>
              <span v-else></span>
              <button v-if="nextPage" type="button" class="manual-link" @click="showPage(nextPage.id)">
                {{ nextPage.title }} →
              </button>
            </div>
          </template>
        </div>
      </div>
    </div>
  </dialog>
</template>

<style>
/* Unscoped on purpose: the page bodies are separate components, so a scoped rule would not reach
   their markup and every page would have to carry its own typography classes. Plain CSS rather
   than Tailwind utilities for the same reason — the pages stay readable as prose. */
.manual-prose {
  font-size: 0.875rem;
  line-height: 1.6;
  color: rgb(38 38 38);
}
.dark .manual-prose {
  color: rgb(212 212 212);
}
.manual-prose h1 {
  font-size: 1.25rem;
  font-weight: 600;
  margin-bottom: 0.75rem;
  color: rgb(23 23 23);
}
.manual-prose h2 {
  font-size: 1rem;
  font-weight: 600;
  margin-top: 1.75rem;
  margin-bottom: 0.5rem;
  color: rgb(23 23 23);
}
.manual-prose h3 {
  font-size: 0.875rem;
  font-weight: 600;
  margin-top: 1.25rem;
  margin-bottom: 0.35rem;
  color: rgb(23 23 23);
}
.dark .manual-prose :is(h1, h2, h3) {
  color: rgb(245 245 245);
}
.manual-prose p {
  margin-bottom: 0.75rem;
}
.manual-prose ul,
.manual-prose ol {
  margin-bottom: 0.75rem;
  padding-left: 1.15rem;
}
.manual-prose ul {
  list-style: disc;
}
.manual-prose ol {
  list-style: decimal;
}
.manual-prose li {
  margin-bottom: 0.3rem;
}
.manual-prose strong {
  font-weight: 600;
  color: rgb(23 23 23);
}
.dark .manual-prose strong {
  color: rgb(245 245 245);
}
.manual-prose code {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.8125rem;
  background: rgb(245 245 245);
  border-radius: 0.25rem;
  padding: 0.05rem 0.3rem;
}
.dark .manual-prose code {
  background: rgb(38 38 38);
}
.manual-prose table {
  width: 100%;
  margin-bottom: 0.9rem;
  border-collapse: collapse;
  font-size: 0.8125rem;
}
.manual-prose th {
  text-align: left;
  font-weight: 600;
  padding: 0.3rem 0.75rem 0.3rem 0;
  border-bottom: 1px solid rgb(229 229 229);
  vertical-align: top;
}
.manual-prose td {
  padding: 0.3rem 0.75rem 0.3rem 0;
  border-bottom: 1px solid rgb(245 245 245);
  vertical-align: top;
}
.dark .manual-prose th {
  border-bottom-color: rgb(64 64 64);
}
.dark .manual-prose td {
  border-bottom-color: rgb(38 38 38);
}
/* First column of a control table is the control's own label — same weight as it has on screen. */
.manual-prose td:first-child {
  white-space: nowrap;
  font-weight: 500;
  color: rgb(23 23 23);
}
.dark .manual-prose td:first-child {
  color: rgb(245 245 245);
}
/* A table whose first column is a phrase rather than a label — "what you see" against "what to do",
   which is most of the simple guide's tables. Left to the rule above, a sentence there is held on one
   line: it takes two thirds of the width on a wide window and overflows the dialog on a narrow one. */
.manual-prose table.manual-wrap td:first-child {
  white-space: normal;
  width: 40%;
}
.manual-prose .manual-note {
  border-left: 3px solid rgb(217 119 6);
  background: rgb(255 251 235);
  padding: 0.5rem 0.75rem;
  border-radius: 0 0.25rem 0.25rem 0;
  margin-bottom: 0.9rem;
}
.dark .manual-prose .manual-note {
  background: rgb(69 26 3 / 0.35);
  border-left-color: rgb(251 191 36);
}
.manual-prose .manual-note p:last-child {
  margin-bottom: 0;
}
/* A hand-drawn schematic: strokes in the prose colour so it follows the theme, labels a step
   quieter. Distinct from .manual-chart, which is a plotted curve with axes. */
.manual-prose .manual-figure {
  margin: 0.5rem 0 1rem;
}
.manual-prose .manual-figure svg {
  display: block;
  width: 100%;
  max-width: 34rem;
  height: auto;
  font-family: ui-sans-serif, system-ui, sans-serif;
  font-size: 11px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.4;
  stroke-linecap: round;
}
.manual-prose .manual-figure svg text {
  fill: currentColor;
  stroke: none;
}
.manual-prose .manual-figure svg .muted {
  fill: rgb(115 115 115);
}
.dark .manual-prose .manual-figure svg .muted {
  fill: rgb(163 163 163);
}
.manual-prose .manual-figure svg .node {
  fill: currentColor;
}
.manual-prose .manual-figure svg .dashed {
  stroke-dasharray: 4 3;
}
.manual-prose .manual-figure svg .tap {
  stroke-width: 1;
}
.manual-prose .manual-figure figcaption {
  font-size: 0.75rem;
  color: rgb(115 115 115);
  margin-top: 0.35rem;
}
.dark .manual-prose .manual-figure figcaption {
  color: rgb(163 163 163);
}
.manual-prose .manual-link {
  color: rgb(29 78 216);
  text-decoration: underline;
  text-underline-offset: 2px;
}
.dark .manual-prose .manual-link {
  color: rgb(147 197 253);
}
.manual-prose .manual-wiring-links {
  font-size: 0.8125rem;
  border-left: 3px solid rgb(212 212 212);
  padding-left: 0.6rem;
  color: rgb(82 82 82);
}
.dark .manual-prose .manual-wiring-links {
  border-left-color: rgb(64 64 64);
  color: rgb(163 163 163);
}
/* The cross-edition banner above a page, and the same invitation on a contents page. Quiet on
   purpose: it is a signpost to the other manual, not part of the page it sits on. */
.manual-prose .manual-edition-switch {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.8125rem;
  color: rgb(115 115 115);
  border-bottom: 1px solid rgb(229 229 229);
  padding-bottom: 0.5rem;
  margin-bottom: 1rem;
}
.dark .manual-prose .manual-edition-switch {
  color: rgb(163 163 163);
  border-bottom-color: rgb(64 64 64);
}
.manual-prose .manual-edition-switch-tag {
  border: 1px solid rgb(229 229 229);
  border-radius: 0.25rem;
  padding: 0 0.3rem;
  font-size: 0.6875rem;
  text-transform: uppercase;
  letter-spacing: 0.02em;
  color: rgb(140 140 140);
}
.dark .manual-prose .manual-edition-switch-tag {
  border-color: rgb(64 64 64);
  color: rgb(140 140 140);
}
.manual-prose .manual-edition-invite {
  border-left: 3px solid rgb(191 219 254);
  background: rgb(239 246 255);
  padding: 0.5rem 0.75rem;
  border-radius: 0 0.25rem 0.25rem 0;
}
.dark .manual-prose .manual-edition-invite {
  border-left-color: rgb(30 64 175);
  background: rgb(23 37 84 / 0.4);
}
.manual-prose .manual-index {
  list-style: none;
  padding-left: 0;
}
.manual-prose .manual-index li {
  margin-bottom: 0.4rem;
}
.manual-prose .manual-index-summary {
  display: block;
  font-size: 0.8125rem;
  color: rgb(115 115 115);
}
.dark .manual-prose .manual-index-summary {
  color: rgb(163 163 163);
}
</style>

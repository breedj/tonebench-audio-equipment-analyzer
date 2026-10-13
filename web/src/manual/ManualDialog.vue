<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import {
  MANUAL_EDITIONS,
  MANUAL_EDITION_TONES,
  MANUAL_GROUP_TONES,
  manualCounterpart,
  manualEdition,
  manualPageById,
  manualPagesIn,
  manualPagesInGroup,
  type ManualGroup,
} from './pages'
import { useManual } from './useManual'
import PanelIcon from '../components/PanelIcon.vue'

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

// No backdrop-click handler, for the reason Modal.vue gives: a dialog here closes by its own X or
// by Escape, never by a click that lands beside it. The manual is the one it was worst on — a page
// read for a while, dismissed by a click meant for the text.
</script>

<template>
  <dialog
    ref="dialogRef"
    class="m-auto h-[85vh] w-[min(65rem,92vw)] max-w-none rounded-2xl border border-neutral-200 bg-[var(--surface-panel)] p-0 text-neutral-900 backdrop:bg-black/40 dark:border-neutral-800 dark:text-neutral-100"
    @cancel="onCancel"
  >
    <div class="flex h-full flex-col">
      <!-- The 2026-10-13 chrome, hand-written here rather than taken from Modal: this dialog is a
           book, so the header carries the edition switch and the breadcrumb back to the contents. -->
      <div
        class="flex shrink-0 items-center gap-3 border-b border-neutral-200 py-3 pr-3 pl-5 dark:border-neutral-800"
      >
        <PanelIcon icon="book" tone="amber" :size="32" />
        <h2 class="flex min-w-0 flex-1 items-center gap-2 text-[17px] font-semibold">
          <button
            type="button"
            class="rounded hover:text-neutral-500 dark:hover:text-neutral-400"
            :class="{ 'pointer-events-none': page === null }"
            @click="showPage(null)"
          >
            {{ editionInfo.title }}
          </button>
          <template v-if="page">
            <span class="text-neutral-300 dark:text-neutral-700">/</span>
            <span class="truncate text-[15px] font-normal text-neutral-600 dark:text-neutral-300">
              {{ page.title }}
            </span>
          </template>
        </h2>

        <!-- Which manual. Always both, always visible: the simple guide is only useful if it is
             found, and the reader who has outgrown it needs the way back to be just as obvious. In
             the header rather than at the top of the index, where it was easy to miss. -->
        <div class="tb-segment-track hidden grid-cols-2 sm:grid" role="group" aria-label="Manual edition">
          <button
            v-for="e in MANUAL_EDITIONS"
            :key="e.id"
            type="button"
            class="tb-segment px-3.5"
            :class="e.id === edition ? 'tb-segment-on' : ''"
            :style="e.id === edition ? { color: `var(--kind-${MANUAL_EDITION_TONES[e.id]}-chip-fg)` } : undefined"
            :aria-pressed="e.id === edition"
            :title="e.blurb"
            @click="showEdition(e.id)"
          >
            {{ e.title }}
          </button>
        </div>

        <button
          type="button"
          class="flex size-10 shrink-0 items-center justify-center rounded-lg text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
          aria-label="Close"
          @click="closeManual"
        >
          <svg
            width="18"
            height="18"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.8"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <path d="M6 6l12 12M18 6L6 18" />
          </svg>
        </button>
      </div>

      <div class="flex min-h-0 flex-1">
        <!-- Index. Always present, so a page is never a dead end and the whole manual stays one
             click away from wherever a measurement's own help button dropped you. On the ground
             tone, so the page beside it reads as the paper. -->
        <nav
          class="hidden w-[250px] shrink-0 flex-col gap-0.5 overflow-y-auto border-r border-neutral-200 p-2.5 md:flex dark:border-neutral-800"
          :style="{ backgroundColor: 'var(--surface-ground)' }"
          aria-label="Manual contents"
        >
          <button
            type="button"
            class="flex items-center gap-2 rounded-lg px-2.5 py-[7px] text-left text-[12.5px] font-semibold"
            :class="
              page === null
                ? 'border border-neutral-200 bg-[var(--surface-panel)] dark:border-neutral-700'
                : 'border border-transparent text-neutral-600 hover:bg-neutral-200/60 dark:text-neutral-300 dark:hover:bg-neutral-800'
            "
            @click="showPage(null)"
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              class="shrink-0"
              :style="{ color: `var(--kind-${MANUAL_EDITION_TONES[edition]}-chip-fg)` }"
              aria-hidden="true"
            >
              <path d="M4 6h16M4 12h16M4 18h10" />
            </svg>
            Contents
          </button>

          <template v-for="group in editionInfo.groups" :key="group">
            <div
              class="flex items-center gap-2 px-2.5 pt-3.5 pb-1 text-[11px] font-semibold tracking-[0.06em] uppercase"
              :style="{ color: `var(--kind-${MANUAL_GROUP_TONES[group]}-chip-fg)` }"
            >
              <span
                class="size-1.5 rounded-full"
                :style="{ backgroundColor: `var(--kind-${MANUAL_GROUP_TONES[group]}-chip-fg)` }"
              ></span>
              {{ group }}
            </div>
            <button
              v-for="p in pagesIn(group)"
              :key="p.id"
              type="button"
              class="flex items-center gap-2 rounded-lg px-2.5 py-[5px] text-left text-[12.5px] leading-[1.35]"
              :class="
                p.id === pageId
                  ? 'bg-[var(--surface-panel)] font-semibold'
                  : 'text-neutral-600 hover:bg-neutral-200/60 dark:text-neutral-300 dark:hover:bg-neutral-800'
              "
              @click="showPage(p.id)"
            >
              <span
                class="size-2 shrink-0 rounded-full"
                :style="{ backgroundColor: `var(--kind-${MANUAL_GROUP_TONES[group]}-chip-bg)` }"
              ></span>
              {{ p.title }}
            </button>
          </template>
        </nav>

        <div ref="contentRef" class="min-w-0 flex-1 overflow-y-auto">
          <!-- The contents page. Outside `.manual-prose` on purpose: its rules set the type for a
               chapter, and this page is a set of cards rather than prose. The index beside it
               carries the same links but has no room for the one-line summaries, and is hidden on a
               narrow window. -->
          <div v-if="page === null" class="flex flex-col gap-4 px-9 py-7">
            <div class="flex flex-col gap-2">
              <span
                class="self-start rounded-full px-2.5 py-0.5 text-[11px] font-semibold tracking-[0.06em] uppercase"
                :style="{
                  backgroundColor: `var(--kind-${MANUAL_EDITION_TONES[edition]}-chip-bg)`,
                  color: `var(--kind-${MANUAL_EDITION_TONES[edition]}-chip-fg)`,
                }"
              >
                {{ editionInfo.title }}
              </span>
              <h1 class="text-[26px] leading-tight font-semibold tracking-tight">
                Tonebench — {{ editionInfo.title.toLowerCase() }}
              </h1>
              <p class="text-[14px] leading-[1.55] text-neutral-600 dark:text-neutral-300">
                {{ editionInfo.blurb }}
              </p>
              <p v-if="edition === 'reference'" class="text-[14px] leading-[1.55] text-neutral-600 dark:text-neutral-300">
                One chapter per measurement, plus the parts every measurement shares. Each measurement
                screen also carries its own
                <span
                  class="inline-flex size-[18px] items-center justify-center rounded-full border border-neutral-300 align-[1px] text-[11px] text-neutral-600 dark:border-neutral-600 dark:text-neutral-300"
                  >?</span
                >
                button, which opens that measurement's chapter directly.
              </p>
              <p v-else class="text-[14px] leading-[1.55] text-neutral-600 dark:text-neutral-300">
                A short page per job, in the order it is worth reading them. Nothing here needs any
                maths, and nothing here is a different app — every page names the chapter in the full
                manual that goes further on the same subject.
              </p>
            </div>

            <!-- The invitation to the other edition. A card rather than a quoted aside: it is the
                 one thing on this page that is about a different book. -->
            <div class="dialog-card flex items-center gap-3 px-3.5 py-3">
              <PanelIcon icon="book" :tone="MANUAL_EDITION_TONES[edition === 'reference' ? 'guide' : 'reference']" :size="34" />
              <p class="flex-1 text-[13px] leading-[1.5] text-neutral-600 dark:text-neutral-300">
                <template v-if="edition === 'reference'">
                  <strong class="font-semibold text-neutral-900 dark:text-neutral-100">
                    New to audio measurement?
                  </strong>
                  The
                  <button type="button" class="manual-link" @click="showEdition('guide')">simple guide</button>
                  covers the same bench in plain language, without the conventions and the measured
                  figures.
                </template>
                <template v-else>
                  <strong class="font-semibold text-neutral-900 dark:text-neutral-100">
                    Want the whole of it?
                  </strong>
                  Every control, every setting and every convention is in the
                  <button type="button" class="manual-link" @click="showEdition('reference')">
                    full manual</button
                  >.
                </template>
              </p>
            </div>

            <section v-for="group in editionInfo.groups" :key="group" class="mt-1.5 flex flex-col gap-2.5">
              <div class="flex items-center gap-2">
                <h2
                  class="rounded-full px-2.5 py-[3px] text-[11px] font-semibold tracking-[0.06em] uppercase"
                  :style="{
                    backgroundColor: `var(--kind-${MANUAL_GROUP_TONES[group]}-chip-bg)`,
                    color: `var(--kind-${MANUAL_GROUP_TONES[group]}-chip-fg)`,
                  }"
                >
                  {{ group }}
                </h2>
                <span class="text-[12px] text-neutral-500 dark:text-neutral-400">
                  {{ pagesIn(group).length }} page{{ pagesIn(group).length === 1 ? '' : 's' }}
                </span>
                <span class="h-px flex-1 bg-neutral-200 dark:bg-neutral-800"></span>
              </div>

              <div class="grid grid-cols-1 gap-2.5 lg:grid-cols-2">
                <button
                  v-for="p in pagesIn(group)"
                  :key="p.id"
                  type="button"
                  class="dialog-card flex flex-col gap-1 rounded-[10px] px-3.5 py-3 text-left hover:border-neutral-300 dark:hover:border-neutral-700"
                  @click="showPage(p.id)"
                >
                  <span class="flex items-center justify-between gap-2 text-[13.5px] font-semibold">
                    {{ p.title }}
                    <svg
                      width="14"
                      height="14"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      class="shrink-0 text-neutral-400"
                      aria-hidden="true"
                    >
                      <path d="M9 6l6 6-6 6" />
                    </svg>
                  </span>
                  <span class="text-[12.5px] leading-[1.45] text-neutral-500 dark:text-neutral-400">
                    {{ p.summary }}
                  </span>
                </button>
              </div>
            </section>
          </div>

          <div v-else class="manual-prose px-9 py-6">
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
          </div>
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
/* Not scoped to `.manual-prose`: the contents page is cards rather than prose and still links. */
.manual-link {
  color: rgb(29 78 216);
  text-decoration: underline;
  text-underline-offset: 2px;
}
.dark .manual-link {
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

<script setup lang="ts">
/**
 * The measurement status bar from Joost's 2026-10-12 mockups: a card at the top of the chart column
 * carrying the run's state as a coloured dot and word, a progress track, and whatever two or three
 * figures that measurement has to say about itself. It replaces the loose grey mono line each screen
 * used to print there ("Point 7/31 · 1.26 kHz · gen −20.00 dBV").
 *
 * **It is `h-11`, the same height as the RUN row across the gutter, and that is the point of it.**
 * The two sit on the same line, so the settings panel under the one and the chart card under the
 * other start at the same height — which is what the frequency-response mockup shows and what the
 * old bare text line could not do, being one line of 12 px text against a 56 px button.
 *
 * A third shape since 2026-11-21: `badge`, which puts a word where the track would be. The
 * oscilloscope asks for it, because a scope's run does not *progress* — Normal and Auto go round
 * the same loop for as long as they are left, so a track that fills says nothing, where
 * Armed / Ready / Trig’d / Auto says whether the analyzer is waiting for a trigger it is never
 * going to get. The dot stays beside it: it is the run, and the badge is the trigger.
 *
 * Two shapes otherwise, chosen by whether `caption` is given. With it, the track carries a caption row above
 * it (the frequency-response mockup: "Record 1 of 1" on the left, "2 Hz → 96 kHz · 2.1 s" on the
 * right). Without it, the track is a plain bar on the one line with the figures after it (the THD
 * mockup: "4 / 50", "gen −20.00 dBV"). Both are this component because they are the same bar — a
 * screen with a caption to give gives one.
 */
import { computed } from 'vue'
import { progressFraction, runToneLook, type RunTone } from '../measurements/runStatus'

const props = withDefaults(
  defineProps<{
    tone: RunTone
    /** The word beside the dot: "Finished", "Cancelled", "Point 7 of 31". */
    label: string
    /** How many of `total` are done. Ignored when `total` is 0 — see progressFraction. */
    done?: number
    total?: number
    /** Left caption above the track; its presence is what picks the stacked shape. */
    caption?: string | null
    /** Right caption above the track, set in the mono face. */
    captionRight?: string | null
    /**
     * A word in place of the progress track — the oscilloscope's trigger state.
     *
     * <p>It takes the track's place rather than standing beside it: a bar that cannot fill is
     * furniture, and the two would be read as one thing contradicting the other.</p>
     */
    badge?: string | null
    /** What that word means, on the badge's own tooltip. */
    badgeDetail?: string | null
    /** How it is tinted — the same three tones the dot uses, so one state is one colour. */
    badgeTone?: RunTone
  }>(),
  { done: 0, total: 0, caption: null, captionRight: null, badge: null, badgeDetail: null, badgeTone: 'idle' },
)

const look = computed(() => runToneLook(props.tone))
const badgeLook = computed(() => runToneLook(props.badgeTone))
const fraction = computed(() => progressFraction(props.tone, props.done, props.total))
const stacked = computed(() => props.caption !== null || props.captionRight !== null)
</script>

<template>
  <div
    class="panel-surface flex h-11 shrink-0 items-center gap-3 rounded-[10px] border border-neutral-200 px-3 text-xs dark:border-neutral-800"
  >
    <span class="inline-flex shrink-0 items-center gap-2 font-semibold" :class="look.label">
      <span class="h-[7px] w-[7px] shrink-0 rounded-full" :class="[look.dot, tone === 'busy' ? 'animate-pulse' : '']"></span>
      <span class="truncate">{{ label }}</span>
    </span>

    <!-- Stacked: caption row over the track. The progressbar role carries the real counts, so a
         screen reader is told "4 of 50" rather than "8 percent". -->
    <div v-if="stacked" class="flex min-w-0 flex-1 flex-col gap-1">
      <div class="flex min-w-0 items-baseline justify-between gap-3 text-neutral-500 dark:text-neutral-400">
        <span class="truncate">{{ caption }}</span>
        <span class="shrink-0 font-mono">{{ captionRight }}</span>
      </div>
      <div
        class="tb-progress"
        role="progressbar"
        :aria-label="label"
        :aria-valuemin="0"
        :aria-valuemax="total || 100"
        :aria-valuenow="total ? done : Math.round((fraction ?? 0) * 100)"
      >
        <div class="h-full transition-[width] duration-200" :class="look.fill" :style="{ width: `${(fraction ?? 0) * 100}%` }"></div>
      </div>
    </div>

    <template v-else>
      <!-- The trigger state, where the track would be. `role=status` so a screen reader is told
           when it changes, which is the whole of what this readout is for. -->
      <span
        v-if="badge !== null"
        class="inline-flex min-w-0 shrink-0 items-center rounded-md px-2 py-0.5 font-mono text-[12px] font-semibold tabular-nums"
        :class="[badgeLook.label, badgeLook.badge]"
        role="status"
        :title="badgeDetail ?? undefined"
        >{{ badge }}</span
      >
      <div v-if="badge !== null" class="min-w-0 flex-1"></div>
      <div
        v-else
        class="tb-progress min-w-0 flex-1"
        role="progressbar"
        :aria-label="label"
        :aria-valuemin="0"
        :aria-valuemax="total || 100"
        :aria-valuenow="total ? done : Math.round((fraction ?? 0) * 100)"
      >
        <div class="h-full transition-[width] duration-200" :class="look.fill" :style="{ width: `${(fraction ?? 0) * 100}%` }"></div>
      </div>
      <!-- Whatever this measurement has to say: the point count, the generator level, the bin
           width. Hidden below `sm` so a narrow window keeps the dot, the word and the bar. -->
      <div class="hidden shrink-0 items-center gap-3 text-neutral-500 sm:flex dark:text-neutral-400">
        <slot />
      </div>
    </template>

    <div v-if="stacked" class="hidden shrink-0 items-center gap-3 text-neutral-500 sm:flex dark:text-neutral-400">
      <slot />
    </div>
  </div>
</template>

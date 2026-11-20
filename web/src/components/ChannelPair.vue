<script setup lang="ts">
/**
 * The two inputs as a pair of lit segments — a scope's CH1 and CH2 buttons.
 *
 * <p><b>One control for the three places that mean the same thing.</b> The display rail's pair of
 * <em>which is drawn</em>, the Acquisition panel's <em>which is captured</em> and the Generator's
 * <em>which plays</em> were a segmented pair and two rows of tick boxes, which read as three
 * different kinds of setting when they are one: left, right, or both.</p>
 *
 * <p><b>Why not <code>SegmentedToggle</code>.</b> Two reasons, both visible in the markup it would
 * have to grow: these are two independent toggles rather than one choice of several — both on and
 * both off are ordinary states — and the lit state carries a per-button colour, because the other
 * thing a reader matches a channel by is the colour of its trace.</p>
 *
 * <p><b>Letters rather than figures</b>, where the Drawing and Show groups beside it carry figures:
 * <b>L</b> and <b>R</b> are what a scope has always called its inputs, and a drawing of a channel
 * would be a drawing of nothing.</p>
 */

/** Each channel's own trace ink, so a lit button says which trace it is about. */
const CHANNEL_INK = ['#3b82f6', '#f59e0b'] as const

const left = defineModel<boolean>('left', { required: true })
const right = defineModel<boolean>('right', { required: true })

const props = withDefaults(
  defineProps<{
    /** Names the pair for a screen reader — "Draw", "Capture", "Play". */
    groupLabel: string
    /** What each button means, as its tooltip and its accessible name. */
    leftLabel?: string
    rightLabel?: string
    /**
     * Off where the channel is not available at all.
     *
     * <p>Only the display rail passes these: a channel that is not being <em>captured</em> has no
     * samples to hide, so its draw button says where to turn it on rather than pretending to
     * work. Nothing gates what is captured or what plays.</p>
     */
    leftDisabled?: boolean
    rightDisabled?: boolean
    /** Why a disabled button is disabled, since a dimmed control with no reason is a dead end. */
    leftDisabledTitle?: string
    rightDisabledTitle?: string
  }>(),
  {
    leftLabel: 'Left',
    rightLabel: 'Right',
    leftDisabled: false,
    rightDisabled: false,
    leftDisabledTitle: '',
    rightDisabledTitle: '',
  },
)

/**
 * Lit only when it is both on and available.
 *
 * <p>An unavailable channel's own setting is left alone rather than cleared — ticking it back under
 * Acquisition brings the trace back with it — so what is drawn is the pair of them.</p>
 */
function lit(on: boolean, disabled: boolean): boolean {
  return on && !disabled
}
</script>

<template>
  <div class="tb-segment-track grid grid-cols-2" role="group" :aria-label="props.groupLabel">
    <button
      type="button"
      class="tb-segment font-semibold"
      :class="lit(left, props.leftDisabled) ? 'tb-segment-on' : ''"
      :style="lit(left, props.leftDisabled) ? { color: CHANNEL_INK[0] } : undefined"
      :disabled="props.leftDisabled"
      :aria-pressed="lit(left, props.leftDisabled)"
      :aria-label="`${props.groupLabel} ${props.leftLabel.toLowerCase()}`"
      :title="props.leftDisabled ? props.leftDisabledTitle : `${props.groupLabel} ${props.leftLabel.toLowerCase()}.`"
      @click="left = !left"
    >
      L
    </button>
    <button
      type="button"
      class="tb-segment font-semibold"
      :class="lit(right, props.rightDisabled) ? 'tb-segment-on' : ''"
      :style="lit(right, props.rightDisabled) ? { color: CHANNEL_INK[1] } : undefined"
      :disabled="props.rightDisabled"
      :aria-pressed="lit(right, props.rightDisabled)"
      :aria-label="`${props.groupLabel} ${props.rightLabel.toLowerCase()}`"
      :title="props.rightDisabled ? props.rightDisabledTitle : `${props.groupLabel} ${props.rightLabel.toLowerCase()}.`"
      @click="right = !right"
    >
      R
    </button>
  </div>
</template>

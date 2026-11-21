/**
 * The headed, coloured sections the three oscilloscope pickers are laid out in — Joost's designs of
 * 2026-11-20 (`docs/design/Math · grouped`, `Stimulus · grouped`, `M2`/`M6 · Grouped by kind`).
 *
 * <p><b>Why a section at all.</b> Each of those dialogs is a grid of tiles that had grown past what
 * a grid can be scanned as: seven operations, ten stimuli, fifteen readings on one tab. Grouping
 * them under a heading turns one long list into four or five short ones, and the heading says what
 * the group has in common — which is the thing a reader is choosing by, and which no tile can say
 * about itself.</p>
 *
 * <p><b>Why the colour is carried here and not in the tiles.</b> A section owns two hues: the one
 * its badge and its figures' wells are drawn in, and the one the <em>marks</em> on those figures
 * take. Both are named rather than spelled, so neither needs a `dark:` twin beside it in three
 * templates — the same argument style.css makes for the kind palette, and the reason a section
 * reaches for that palette instead of defining a third one.</p>
 *
 * <p><b>The mark hue is a second tone rather than a variable pair of its own</b>, which is what
 * makes the whole scheme free: the ten `--kind-*-chip-fg` hues already carry both themes, so
 * "blue trace, orange marks" is two names and no new CSS. The pairings are chosen for contrast at
 * figure size (blue/orange, teal/pink, violet/amber, green/red, red/cyan) and are the design's own
 * to within the palette's values.</p>
 *
 * <p>In a module rather than in the components for this project's usual reason: nothing in a
 * `.vue` is testable here, and what breaks silently is a tile left out of every section — which
 * renders as a tile that has simply vanished from the dialog.</p>
 */
import type { MeasurementGroupTone, MeasurementKindTone } from '../measurements/kinds'

/** What every picker section carries, whatever it holds. */
export interface PickerSection {
  /** Stable, for tests and for `v-for` keys. Never stored. */
  id: string
  /** The badge's own text, written as prose and shown uppercase by the heading. */
  label: string
  /** The line beside the badge: what the group has in common, in a handful of words. */
  note: string
  /** The badge, the square dot in it, and the tint behind every figure in the section. */
  tone: MeasurementGroupTone
  /** The hue the marks on those figures take — the level lines, the arrows, the dots. */
  mark: MeasurementKindTone
}

/** A section with whatever it holds, which is what a dialog iterates. */
export interface PickerSectionOf<T> {
  section: PickerSection
  items: T[]
}

/**
 * The tinted well a figure sits in.
 *
 * <p>Both halves matter: the background is the section's tint, and the <b>colour</b> is its accent,
 * which the figure inherits through `currentColor` — so the trace is drawn in the section's own hue
 * without the icon components learning anything about sections.</p>
 */
export function sectionWellStyle(tone: MeasurementGroupTone): Record<string, string> {
  return {
    backgroundColor: `var(--kind-${tone}-chip-bg)`,
    color: `var(--kind-${tone}-chip-fg)`,
  }
}

/** The colour the marks on a figure are drawn in, for the icon components' `mark` prop. */
export function sectionMarkColor(mark: MeasurementKindTone): string {
  return `var(--kind-${mark}-chip-fg)`
}

/**
 * The badge's own three colours.
 *
 * <p>The border is transparent rather than absent in the light theme, so the pill is the same size
 * in both — style.css's own rule, repeated here only because this is where it is read.</p>
 */
export function sectionBadgeStyle(tone: MeasurementGroupTone): Record<string, string> {
  return {
    color: `var(--kind-${tone}-badge-fg)`,
    backgroundColor: `var(--kind-${tone}-badge-bg)`,
    borderColor: `var(--kind-${tone}-badge-border)`,
  }
}

/**
 * Sections with their members, empty ones dropped.
 *
 * <p>A function rather than a shape kept by hand, and exported so it can be tested: the failure
 * worth catching is an entry whose section nothing defines, which drops it out of the dialog
 * silently. Every caller's own test asserts that what goes in comes back out exactly once.</p>
 */
export function groupIntoSections<T>(
  sections: readonly PickerSection[],
  items: readonly T[],
  sectionOf: (item: T) => string,
): PickerSectionOf<T>[] {
  return sections
    .map((section) => ({ section, items: items.filter((item) => sectionOf(item) === section.id) }))
    .filter((entry) => entry.items.length > 0)
}

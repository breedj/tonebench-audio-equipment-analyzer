/**
 * One option of a SegmentedToggle. In its own module rather than exported from the component,
 * because `<script setup>` cannot export and the component is generic over the value type — a call
 * site that annotates its own option array needs the interface by name.
 */
export interface SegmentedOption<T> {
  value: T
  label: string
  /** Columns of the track's grid this option occupies. Default 1. */
  span?: number
  /** Per-option tooltip; the group's own `title` covers the rest. */
  title?: string
  disabled?: boolean
  /**
   * A CSS colour for a dot before the label.
   *
   * <p>For the one thing a segmented control is used for here that a reader matches by colour
   * rather than by name: which <em>trace</em> is being talked about. The dot is the trace's own
   * ink, so the control agrees with the chart beside it.</p>
   */
  dot?: string
  /**
   * A figure before the label, by name from <code>scope/displayIcons.ts</code>.
   *
   * <p>For the controls whose options differ in a way a picture says faster than a word — the
   * oscilloscope's coupling is the first, where DC and AC are two names for "where the wave sits
   * against zero", which is a shape. The figure is drawn in <code>currentColor</code>, so it takes
   * the option's own <code>dot</code> hue when it is the chosen one and the track's grey when it is
   * not; an option carrying a figure draws no dot, the two being the same claim twice.</p>
   */
  icon?: string
  /**
   * A count after the label — how many of something this option already holds.
   *
   * <p>Shown only where it is worth saying, so an option with nothing to report carries no badge
   * rather than a zero: a row of zeroes is noise, and the one number that is not zero is what the
   * badge exists to find.</p>
   */
  badge?: number
}

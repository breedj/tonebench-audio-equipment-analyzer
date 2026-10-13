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
}

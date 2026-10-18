<script setup lang="ts">
/**
 * The small icon that lights one set of annotations and darkens every other.
 *
 * <p>One component for both places the group appears — a row of the History checklist, and the
 * header of a display rail's Annotations panel — because the two are members of the same radio
 * group and an icon that read differently in the two lists would hide that. See
 * {@link useAnnotations} for the group itself; this knows only how to look lit.</p>
 *
 * <p>No emit: the root is the `button`, so a `@click` at the call site binds to it through
 * attribute fallthrough.</p>
 */

const props = withDefaults(
  defineProps<{
    /** Whether this is the set on the charts. At most one toggle in the group is. */
    active: boolean
    /**
     * Whether the set is empty. Drawn faintly, and still clickable — selecting an empty set is the
     * only way to put a first note on a stored result, so this dims and never disables.
     */
    empty?: boolean
    title: string
  }>(),
  { empty: false },
)
</script>

<template>
  <button
    type="button"
    class="shrink-0 transition-colors"
    :class="
      props.active
        ? 'text-violet-600 dark:text-violet-400'
        : props.empty
          ? 'text-neutral-300 hover:text-neutral-500 dark:text-neutral-700 dark:hover:text-neutral-500'
          : 'text-neutral-500 hover:text-neutral-900 dark:text-neutral-400 dark:hover:text-neutral-100'
    "
    :aria-pressed="props.active"
    :title="props.title"
  >
    <!-- A note balloon rather than the panel icon's three rules, which at 16 px reads as a menu.
         Filled while lit, so the state survives a monochrome screenshot and does not rest on colour
         alone. -->
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      :fill="props.active ? 'currentColor' : 'none'"
      :fill-opacity="props.active ? 0.15 : 1"
      stroke="currentColor"
      stroke-width="2"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      <path d="M20 4H4a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3v4l4-4h9a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1Z" />
    </svg>
  </button>
</template>

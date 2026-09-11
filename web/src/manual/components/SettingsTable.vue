<script setup lang="ts">
/**
 * One example measurement's settings, control by on-screen label, as the simulator pages list
 * them. A component only so sixteen pages do not each spell out the same table markup; the
 * ManualDialog's .manual-prose rules style it like every other table in the manual.
 */
defineProps<{
  rows: readonly (readonly [label: string, value: string])[]
  /** Column headings; the defaults suit a settings table, a readings table overrides them. */
  headings?: readonly [string, string]
}>()
</script>

<template>
  <table>
    <thead>
      <tr>
        <th>{{ headings?.[0] ?? 'Control' }}</th>
        <th>{{ headings?.[1] ?? 'Setting' }}</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="[label, value] in rows" :key="label">
        <td>{{ label }}</td>
        <td>{{ value }}</td>
      </tr>
    </tbody>
  </table>
</template>

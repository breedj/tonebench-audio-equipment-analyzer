<script setup lang="ts">
defineProps<{ kind: string }>()
</script>

<template>
  <svg v-if="kind === 'FFTSpectrum'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
    <path d="M3 21V10M9 21V6M15 21V13M21 21V3" />
  </svg>
  <!-- A rising-then-falling curve on a baseline: distortion against frequency. -->
  <svg v-else-if="kind === 'ThdVsFrequency'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 3v18h18" />
    <path d="M6 15c3 0 3-7 6-7s3 5 6 5" />
  </svg>
  <!-- A curve that stays flat and then knees upward: distortion against level, up to clipping. -->
  <svg v-else-if="kind === 'ThdVsAmplitude'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 3v18h18" />
    <path d="M6 17h8c2 0 3-4 4-8" />
  </svg>
  <svg v-else width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
    <circle cx="12" cy="12" r="9" />
  </svg>
</template>

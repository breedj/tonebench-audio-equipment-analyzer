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
  <!-- The amplitude knee again, with two tone marks standing on the baseline: a two-tone pair stepped through the levels. -->
  <svg v-else-if="kind === 'ImdVsAmplitude'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 3v18h18" />
    <path d="M6 17h8c2 0 3-4 4-8" />
    <path d="M8 17v-4M11 17v-6" />
  </svg>
  <!-- A flat passband rolling off at both ends: a frequency response with its bandwidth. -->
  <svg v-else-if="kind === 'FrequencyResponse'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 3v18h18" />
    <path d="M6 18c1.5 0 2-9 4-9h5c2 0 2.5 6 4 6" />
  </svg>
  <!-- The same passband shape as the chirp's, drawn as discrete points: one tone at a time. -->
  <svg v-else-if="kind === 'SteppedResponse'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 3v18h18" />
    <circle cx="6.5" cy="17" r="1.1" fill="currentColor" stroke="none" />
    <circle cx="9.5" cy="10" r="1.1" fill="currentColor" stroke="none" />
    <circle cx="13" cy="9" r="1.1" fill="currentColor" stroke="none" />
    <circle cx="16.5" cy="9.5" r="1.1" fill="currentColor" stroke="none" />
    <circle cx="20" cy="15" r="1.1" fill="currentColor" stroke="none" />
  </svg>
  <!-- A spike with a decaying tail on a baseline: an impulse response. -->
  <!-- Impedance: a resonance peak over a baseline, which is what a driver's impedance curve looks
       like and the shape this measurement exists to show. -->
  <svg v-else-if="kind === 'Impedance'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 17h3l2-9 3 12 2-9 2 6h6" />
  </svg>
  <svg v-else-if="kind === 'ImpulseResponse'" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 12h4" />
    <path d="M7 12 8.5 4 10 19l1.5-11 1.5 8 1.5-5 1.5 3.5 1.5-2L21 12" />
  </svg>
  <svg v-else width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
    <circle cx="12" cy="12" r="9" />
  </svg>
</template>

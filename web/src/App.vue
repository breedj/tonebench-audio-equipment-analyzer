<script setup lang="ts">
import { api, type GeneratorChannelRequest, type SimulatedProfileRequest } from './api/client'
import { useDevice } from './composables/useDevice'
import { useSpectrumStream } from './composables/useSpectrumStream'
import DeviceStatusBar from './components/DeviceStatusBar.vue'
import GeneratorPanel from './components/GeneratorPanel.vue'
import AcquisitionPanel from './components/AcquisitionPanel.vue'
import SpectrumConfigPanel from './components/SpectrumConfigPanel.vue'
import SpectrumChart from './components/SpectrumChart.vue'
import SystemMetricsPanel from './components/SystemMetricsPanel.vue'
import MetricsPanel from './components/MetricsPanel.vue'
import HarmonicsTable from './components/HarmonicsTable.vue'
import OtherPeaksTable from './components/OtherPeaksTable.vue'

const { status, ranges, error, refreshStatus } = useDevice()
const { spectrum, meters } = useSpectrumStream()

// Every handler below talks to the backend and must not fail silently — an uncaught rejection
// from a template @click handler is swallowed by Vue (logged to console, nothing shown), which
// reads to a user as "the button did nothing."
async function withErrorReporting(action: () => Promise<void>) {
  try {
    await action()
    error.value = null
  } catch (e) {
    error.value = (e as Error).message
  }
}

function setInputRange(dbv: number) {
  return withErrorReporting(async () => {
    status.value = await api.setInputRange(dbv)
  })
}

function setOutputRange(dbv: number) {
  return withErrorReporting(async () => {
    status.value = await api.setOutputRange(dbv)
  })
}

function setSimulatedProfile(patch: SimulatedProfileRequest) {
  return withErrorReporting(async () => {
    status.value = await api.setSimulatedProfile(patch)
  })
}

function applyGenerator(left: GeneratorChannelRequest, right: GeneratorChannelRequest) {
  return withErrorReporting(async () => {
    status.value = await api.setGenerator({ left, right })
  })
}

function startAcquisition(sampleRateHz: number, channelCount: number, blockFrameCount: number) {
  return withErrorReporting(async () => {
    status.value = await api.startAcquisition({ sampleRateHz, channelCount, blockFrameCount })
  })
}

function stopAcquisition() {
  return withErrorReporting(async () => {
    await api.stopAcquisition()
    await refreshStatus()
  })
}

function reportError(message: string) {
  error.value = message
}
</script>

<template>
  <div class="flex min-h-screen flex-col bg-neutral-50 text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
    <DeviceStatusBar
      :status="status"
      :ranges="ranges"
      :error="error"
      @set-input-range="setInputRange"
      @set-output-range="setOutputRange"
      @set-simulated-profile="setSimulatedProfile"
    />

    <main class="flex flex-1 flex-col gap-4 p-4">
      <div class="grid grid-cols-1 gap-4 md:grid-cols-3">
        <GeneratorPanel :status="status" @apply="applyGenerator" />
        <AcquisitionPanel :status="status" :ranges="ranges" @start="startAcquisition" @stop="stopAcquisition" />
        <SpectrumConfigPanel @error="reportError" />
      </div>

      <SpectrumChart :spectrum="spectrum" :meters="meters" />
      <SystemMetricsPanel :status="status" :meters="meters[0] ?? meters[1] ?? null" />

      <div class="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <div class="flex flex-col gap-4">
          <MetricsPanel channel-label="L" :meters="meters[0] ?? null" :generator-tones="status?.generatorLeft.tones ?? []" />
          <HarmonicsTable :meters="meters" :channel="0" channel-label="L" />
          <OtherPeaksTable :meters="meters" :channel="0" channel-label="L" />
        </div>
        <div class="flex flex-col gap-4">
          <MetricsPanel channel-label="R" :meters="meters[1] ?? null" :generator-tones="status?.generatorRight.tones ?? []" />
          <HarmonicsTable :meters="meters" :channel="1" channel-label="R" />
          <OtherPeaksTable :meters="meters" :channel="1" channel-label="R" />
        </div>
      </div>
    </main>
  </div>
</template>

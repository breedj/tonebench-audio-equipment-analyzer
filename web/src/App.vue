<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, type SimulatedProfileRequest, type StagedMeasurementConfig } from './api/client'
import { useDevice } from './composables/useDevice'
import { useSpectrumStream } from './composables/useSpectrumStream'
import { useMeasurements } from './composables/useMeasurements'
import { useProjects } from './composables/useProjects'
import { useSpectrumConfig } from './composables/useSpectrumConfig'
import { determineAttenuation, nearestInputRange } from './attenuation'
import { stopRun } from './deviceControl'
import DeviceStatusBar from './components/DeviceStatusBar.vue'
import MeasurementSidebar from './components/MeasurementSidebar.vue'
import ProjectPanel from './components/ProjectPanel.vue'
import GeneratorPanel from './components/GeneratorPanel.vue'
import SpectrumConfigPanel from './components/SpectrumConfigPanel.vue'
import SpectrumChart from './components/SpectrumChart.vue'
import OscilloscopeChart from './components/OscilloscopeChart.vue'
import SystemMetricsPanel from './components/SystemMetricsPanel.vue'
import MetricsPanel from './components/MetricsPanel.vue'
import HarmonicsTable from './components/HarmonicsTable.vue'
import OtherPeaksTable from './components/OtherPeaksTable.vue'
import MeasurementHistoryPanel from './components/MeasurementHistoryPanel.vue'
import MeasurementChecklist from './components/MeasurementChecklist.vue'
import ChartDisplayPanel from './components/ChartDisplayPanel.vue'
import SaveMeasurementDialog from './components/SaveMeasurementDialog.vue'
import SaveAsDialog from './components/SaveAsDialog.vue'
import OverlayList from './components/OverlayList.vue'

const { status, ranges, error, refreshStatus } = useDevice()
const { spectrum, meters, scope, setViewport } = useSpectrumStream()
const { overlays } = useMeasurements()
const { config: spectrumConfig } = useSpectrumConfig()
const {
  projects,
  types,
  currentProjectId,
  currentTypeId,
  dirtyTypeIds,
  markDirty,
  saveMeasurementType,
  saveMeasurementTypeAs,
  saveProject,
  saveProjectAs,
} = useProjects()

interface RunConfig {
  sampleRateHz: number
  channelCount: number
}

const generatorPanel = ref<InstanceType<typeof GeneratorPanel> | null>(null)
const spectrumConfigPanel = ref<InstanceType<typeof SpectrumConfigPanel> | null>(null)
const saveDialogOpen = ref(false)
const saveTypeAsOpen = ref(false)
const saveProjectAsOpen = ref(false)
// Placeholder for the real "Projects" dialog — for now the button just expands the existing
// project list/create/delete panel in place, until that gets its own proper dialog.
const projectPanelOpen = ref(false)

const currentProject = computed(() => projects.value.find((p) => p.id === currentProjectId.value) ?? null)
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)
const isCurrentTypeDirty = computed(() => currentTypeId.value !== null && dirtyTypeIds.value.has(currentTypeId.value))
const hasAnyDirtyInProject = computed(() =>
  types.value.some((t) => t.projectId === currentProjectId.value && dirtyTypeIds.value.has(t.id)),
)

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

function setSimulatedProfile(patch: SimulatedProfileRequest) {
  return withErrorReporting(async () => {
    status.value = await api.setSimulatedProfile(patch)
  })
}

// The Run button's whole job: push everything staged in the FFT settings and Generator panels'
// forms — auto-ranging, generator config, then start acquisition — in one explicit action.
// Nothing in either panel touches the device on its own before this: on real hardware, setting
// auto-range steps the attenuator relay, which is audible the instant it happens, not just when
// the generator plays, so even that has to wait for Run. Input range itself is never sent as a
// specific value here — auto-ranging (always on, where the device supports it) decides it, either
// the device's own hardware ranger or the live monitor below for devices that don't (the
// simulator) — see DeviceStatusBar's read-only display.
// Block size isn't user-configurable — omitted from both calls below, the server defaults it to 4096.
function runMeasurement(runConfig: RunConfig) {
  return withErrorReporting(async () => {
    const { sampleRateHz, channelCount } = runConfig

    // Gated on the *device* actually having auto-ranging (status.autoRangeEnabled null means it
    // doesn't — the simulator, currently) rather than unconditionally requesting it: the server
    // 400s a device with no such capability, same reasoning as the live monitor's own
    // hardwareAutoActive check below.
    if (status.value?.autoRangeEnabled !== null && status.value?.autoRangeEnabled !== undefined)
      status.value = await api.setAutoRange(true)

    const generator = generatorPanel.value?.buildRequest()
    if (generator) {
      // sampleRateHz/channelCount are a hint the backend only uses if it needs to bring up a
      // generator-only stream on its own — see DeviceSessionService.ConfigureGeneratorAsync.
      status.value = await api.setGenerator({
        left: generator.left,
        right: generator.right,
        sampleRateHz,
        channelCount,
        snapToBin: generator.snapToBin,
      })
      generatorPanel.value?.resyncTones(status.value)
    }

    const newStatus = await api.startAcquisition({ sampleRateHz, channelCount })
    status.value = newStatus
    // A sample-rate change re-snaps the generator server-side (see DeviceSessionService.StartAcquisitionAsync);
    // newStatus already carries the new frequencies, this just pulls them into the form fields.
    generatorPanel.value?.resyncTones(newStatus)
  })
}

// Shared by the RUN button and any live setting that needs a restart (currently just FFT
// settings' sample rate) — builds the RunConfig fresh from whatever's currently staged each time,
// rather than the button owning a fixed one.
function runFromCurrentSettings() {
  return runMeasurement({
    sampleRateHz: spectrumConfigPanel.value?.getSampleRateHz() ?? 192000,
    channelCount: ranges.value?.maxChannelCount ?? 2,
  })
}

// The Stop button's counterpart — same "stop everything" used automatically when switching
// measurements (see useProjects.ts's selectType), just with a snappy refresh here since this one
// has a button sitting right there waiting on it, unlike the switch-measurement case.
function stopMeasurement() {
  return withErrorReporting(async () => {
    await stopRun()
    await refreshStatus()
  })
}

// Generator field edits (frequency, level, kind, enabled, snap-to-bin — see GeneratorPanel's
// `change` emit) only need to reach the device at all if something's already live: pushing while
// idle would just be pointless traffic, since the values are already staged locally and Run reads
// them fresh anyway. `ConfigureGeneratorAsync` applies live changes to an already-running stream on
// its own (no restart), so unlike Run this never touches acquisition — only the generator.
function applyGeneratorLive() {
  return withErrorReporting(async () => {
    markDirty()
    const live = status.value?.acquiring || status.value?.generatorLeft.enabled || status.value?.generatorRight.enabled
    if (!live) return

    const generator = generatorPanel.value?.buildRequest()
    if (!generator) return
    status.value = await api.setGenerator({ left: generator.left, right: generator.right, snapToBin: generator.snapToBin })
    generatorPanel.value?.resyncTones(status.value)
  })
}

// Sample rate (see SpectrumConfigPanel's `sample-rate-change` emit) changes the actual acquisition
// stream, so unlike FFT size/window/etc. (which apply live on their own via useSpectrumConfig) it
// needs a full restart — same "no-op while idle, since Run pushes it anyway" reasoning as
// applyGeneratorLive.
function onSampleRateChange() {
  if (status.value?.acquiring) return runFromCurrentSettings()
}

// Live input-range monitoring: while acquiring, continuously reads the peak level off whichever
// channel(s) are enabled and runs it through determineAttenuation() (ported from the previous
// app's QaLibrary — see attenuation.ts) to keep the input range matched to the actual signal.
// Deliberately stays out of the way of real hardware's own auto-ranger rather than fighting it —
// this is what gives the simulator equivalent auto-ranging, since it has no hardware ranger of its
// own. Never marks the measurement dirty: this is the software tracking the live signal, not the
// user changing a setting they'd want saved — same as hardware auto-ranging never marking it dirty
// either (that stepping happens entirely server-side, invisible to the client).
//
// Requires the same determined target 3 frames running before acting — this is a coarse (6 dB step)
// decision already, but a peak sitting right on a step boundary could still ping-pong frame to
// frame; on real hardware every step is an audible relay click, so this is not just "avoid churn."
// Same 3-frame threshold Qa40xAutoRanger.cs uses for its own under-range hysteresis.
const RANGE_STABILITY_FRAMES = 3
let pendingRangeTarget: number | null = null
let pendingRangeCount = 0

watch(meters, (m) => {
  const hardwareAutoActive = status.value?.autoRangeEnabled !== null && status.value?.autoRangeEnabled !== undefined
  if (!status.value?.acquiring || hardwareAutoActive) {
    pendingRangeTarget = null
    pendingRangeCount = 0
    return
  }

  const peaks: number[] = []
  if ((spectrumConfig.value?.processLeft ?? true) && m[0]) peaks.push(m[0].peakDbv)
  if ((spectrumConfig.value?.processRight ?? true) && m[1]) peaks.push(m[1].peakDbv)
  if (peaks.length === 0) return

  const options = ranges.value?.inputRangesDbv ?? [0, 6, 12, 18, 24, 30, 36, 42]
  const target = nearestInputRange(determineAttenuation(Math.max(...peaks)), options)

  if (target === pendingRangeTarget) pendingRangeCount++
  else {
    pendingRangeTarget = target
    pendingRangeCount = 1
  }

  const currentRangeDbv = status.value?.inputRangeDbv
  if (pendingRangeCount >= RANGE_STABILITY_FRAMES && currentRangeDbv != null && target !== currentRangeDbv) {
    pendingRangeCount = 0
    withErrorReporting(async () => {
      status.value = await api.setInputRange(target)
    })
  }
})

function reportError(message: string) {
  error.value = message
}

function openSaveDialog() {
  saveDialogOpen.value = true
}

// The FFT settings/Generator panels' current on-screen values, whether or not they've ever reached
// the device — same values runMeasurement uses to gather what Run would send, just packaged for a
// measurement-type save (params_json) instead of pushed live. Undefined when nothing's loaded yet,
// in which case the server falls back to whatever the device last actually did. inputRangeDbv/
// autoRangeEnabled are no longer user-staged values — auto-ranging is always on (see
// runFromCurrentSettings), so 0/true are just the placeholder Run itself uses.
function buildStagedConfig(): StagedMeasurementConfig | undefined {
  const sampleRateHz = spectrumConfigPanel.value?.getSampleRateHz()
  const generator = generatorPanel.value?.buildRequest()
  if (sampleRateHz === undefined || !generator) return undefined
  return {
    sampleRateHz,
    channelCount: ranges.value?.maxChannelCount ?? 2,
    inputRangeDbv: 0,
    autoRangeEnabled: true,
    generatorLeft: generator.left,
    generatorRight: generator.right,
    snapToBin: generator.snapToBin,
  }
}

function saveCurrentType() {
  return withErrorReporting(async () => {
    if (currentTypeId.value === null) return
    await saveMeasurementType(currentTypeId.value, buildStagedConfig())
  })
}

function confirmSaveTypeAs(name: string) {
  return withErrorReporting(async () => {
    if (currentTypeId.value === null) return
    await saveMeasurementTypeAs(currentTypeId.value, name, buildStagedConfig())
    saveTypeAsOpen.value = false
  })
}

function saveCurrentProject() {
  return withErrorReporting(async () => {
    if (currentProjectId.value === null) return
    await saveProject(currentProjectId.value, buildStagedConfig())
  })
}

function confirmSaveProjectAs(name: string) {
  return withErrorReporting(async () => {
    await saveProjectAs(name)
    saveProjectAsOpen.value = false
  })
}
</script>

<template>
  <div class="flex h-screen flex-col bg-neutral-50 text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
    <header class="flex items-center gap-3 border-b border-neutral-200 px-4 py-2 dark:border-neutral-800">
      <span class="text-sm font-semibold">QA403 audio analyzer</span>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        @click="projectPanelOpen = !projectPanelOpen"
      >
        {{ currentProject?.name ?? 'Select project' }}
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>
      <button
        v-if="currentProjectId !== null"
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 disabled:opacity-40 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        :disabled="!hasAnyDirtyInProject"
        title="Save every changed measurement in this project at once."
        @click="saveCurrentProject"
      >
        Save project
      </button>
      <button
        v-if="currentProjectId !== null"
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        title="Save this project and its measurements as a new project, leaving this one untouched."
        @click="saveProjectAsOpen = true"
      >
        Save project as…
      </button>
      <span class="flex-1"></span>
      <!-- Not wired up yet — app-wide configuration doesn't exist as a concept in this app yet. -->
      <button
        type="button"
        class="text-neutral-400 dark:text-neutral-600"
        aria-label="Configuration"
        title="Configuration (coming soon)"
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <circle cx="12" cy="12" r="3" />
          <path
            d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z"
          />
        </svg>
      </button>
    </header>

    <div v-if="projectPanelOpen" class="border-b border-neutral-200 p-4 dark:border-neutral-800">
      <ProjectPanel />
    </div>

    <div class="flex flex-1 overflow-hidden">
      <MeasurementSidebar />

      <main class="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
        <template v-if="currentTypeId !== null">
          <div class="flex items-center gap-2 text-sm">
            <span class="font-medium">{{ currentType?.name }}</span>
            <span
              v-if="isCurrentTypeDirty"
              class="h-2 w-2 shrink-0 rounded-full bg-red-500"
              title="Unsaved changes"
            ></span>
            <button
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="!isCurrentTypeDirty"
              title="Save the current settings into this measurement."
              @click="saveCurrentType"
            >
              Save
            </button>
            <button
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
              title="Save the current settings as a new measurement in this project; this one reverts to its own last-saved settings."
              @click="saveTypeAsOpen = true"
            >
              Save as…
            </button>
            <button
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="status?.acquiring"
              title="Saves the currently displayed (stopped) spectrum as a new measurement result — stop acquisition first."
              @click="openSaveDialog"
            >
              Save measurement result
            </button>
          </div>

          <!-- FFT Spectrum screen, matching the wireframe: settings rail on the left, chart in the
               center, display options + history checklist on the right. -->
          <div class="flex flex-col gap-4 lg:flex-row lg:items-start">
            <div class="flex w-full flex-col gap-3 lg:w-72 lg:flex-none">
              <div class="flex gap-3">
                <button
                  type="button"
                  class="flex flex-1 items-center justify-center gap-2 rounded-lg py-4 text-base font-bold tracking-wide text-white transition disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0"
                  :class="status?.acquiring
                    ? 'bg-green-800'
                    : 'bg-green-600 hover:bg-green-500 ring-2 ring-green-400 ring-offset-2 dark:ring-offset-neutral-950'"
                  :disabled="status?.acquiring"
                  title="Starts the generator(s) and acquisition using whatever is currently configured in the Generator and FFT settings panels."
                  @click="runFromCurrentSettings"
                >
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                  RUN
                </button>
                <button
                  type="button"
                  class="flex flex-1 items-center justify-center gap-2 rounded-lg py-4 text-base font-bold tracking-wide text-white transition disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0"
                  :class="status?.acquiring
                    ? 'bg-red-600 hover:bg-red-500 ring-2 ring-red-400 ring-offset-2 animate-pulse dark:ring-offset-neutral-950'
                    : 'bg-red-800'"
                  :disabled="!status?.acquiring && !status?.generatorLeft.enabled && !status?.generatorRight.enabled"
                  title="Stops acquisition and silences both generator channels."
                  @click="stopMeasurement"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                  STOP
                </button>
              </div>

              <GeneratorPanel ref="generatorPanel" @change="applyGeneratorLive" />
              <SpectrumConfigPanel
                ref="spectrumConfigPanel"
                :ranges="ranges"
                @error="reportError"
                @sample-rate-change="onSampleRateChange"
              />
            </div>

            <div class="min-w-0 flex-1">
              <SpectrumChart
                :spectrum="spectrum"
                :meters="meters"
                :overlays="overlays"
                @viewport-change="setViewport"
              />
              <OscilloscopeChart :scope="scope" :status="status" />
            </div>

            <ChartDisplayPanel>
              <MeasurementChecklist @error="reportError" />
            </ChartDisplayPanel>
          </div>

          <!-- Everything below isn't part of the new FFT Spectrum design — kept here for now, to be
               repositioned later. -->
          <OverlayList />
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

          <MeasurementHistoryPanel @error="reportError" />
        </template>

        <p v-else class="text-sm text-neutral-400 dark:text-neutral-600">
          Select a measurement from the sidebar, or add a new one to get started.
        </p>
      </main>
    </div>

    <DeviceStatusBar
      :status="status"
      :error="error"
      @set-simulated-profile="setSimulatedProfile"
    />

    <SaveMeasurementDialog :open="saveDialogOpen" @close="saveDialogOpen = false" />
    <SaveAsDialog
      :open="saveTypeAsOpen"
      title="Save measurement as…"
      label="New measurement name"
      @close="saveTypeAsOpen = false"
      @confirm="confirmSaveTypeAs"
    />
    <SaveAsDialog
      :open="saveProjectAsOpen"
      title="Save project as…"
      label="New project name"
      @close="saveProjectAsOpen = false"
      @confirm="confirmSaveProjectAs"
    />
  </div>
</template>

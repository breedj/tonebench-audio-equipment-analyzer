<script setup lang="ts">
import { computed, watch } from 'vue'
import { useProgrammableLoad } from '../composables/useProgrammableLoad'
import { useManual } from '../manual/useManual'
import { loadControlState } from '../programmableLoad/controls'
import { loadReadout } from '../programmableLoad/readout'
import BusyNote from './BusyNote.vue'
import HeaderIcon from './HeaderIcon.vue'
import Modal from './Modal.vue'
import ReadingValue from './ReadingValue.vue'

/**
 * The programmable load's controls, reached by clicking its box on the status bar — the same place
 * and the same idea as the QA403's and the sound card's own dialogs.
 *
 * <p><b>What it shows is the hardware's answer, never a staged value.</b> Every button writes and
 * then the app-wide poll reports what the relays actually did, which is the only honest arrangement
 * for this device: a QA451 can accept a command and decline to apply it (see
 * docs/qa451-protocol.md), and a staged selection would show the user's intention over the
 * device's state. The same reason the bar's Attenuation readout reads the device's range rather
 * than the last one asked for.</p>
 *
 * <p><b>Which load this bench has is not chosen here.</b> It lives in Configuration ▸ Acquisition,
 * because it is the switch that makes this whole feature exist, and a setting that hides the dialog
 * it is set in would be a trap. There is a button below that goes there.</p>
 */
const props = defineProps<{
  open: boolean
  /** True while a measurement runner owns the session; the controls stand down rather than be refused. */
  runnerActive: boolean
}>()
const emit = defineEmits<{ close: []; configuration: [] }>()

const { status, error, pending, refresh, connect, setImpedance, setDutPower, safeState } =
  useProgrammableLoad()
const { openManual } = useManual()

const controls = computed(() => loadControlState(status.value, props.runnerActive, pending.value))
const readout = computed(() => loadReadout(status.value))

/**
 * One read on open, and no interval of its own: the app-wide poll already owns the cadence, and this
 * status is not the dialog's alone — the status bar draws it too.
 */
watch(
  () => props.open,
  (open) => {
    if (open) void refresh()
  },
)

/** Swallowed: the composable has already put the sentence on `error`, which is rendered below. */
function press(action: () => Promise<unknown>) {
  void action().catch(() => {})
}
</script>

<template>
  <Modal
    :open="props.open"
    title="Programmable load"
    subtitle="A QA451 across the DUT's output, and the switch for its supply"
    icon="resistor"
    tone="teal"
    ground
    flush
    @close="emit('close')"
  >
    <div class="flex flex-col gap-3.5 p-4">
      <!-- A latched fault first, above everything: it is the only state on this device that nothing
           on screen can clear, so it has to be the first thing read. -->
      <p
        v-if="controls.replug"
        class="m-0 rounded-lg border border-red-300 bg-red-50 p-3 text-[12.5px] leading-relaxed text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200"
      >
        {{ controls.replug }}
      </p>

      <section class="dialog-card">
        <div class="flex flex-wrap items-center justify-between gap-3 p-3.5">
          <span class="flex flex-col gap-1">
            <span class="text-sm font-semibold">{{ readout.model }}</span>
            <span class="text-[12.5px] text-neutral-600 dark:text-neutral-400">
              <template v-if="status?.connected">
                FW {{ status.firmwareVersion }}
                <template v-if="status.requiredFirmwareVersion">
                  (needs {{ status.requiredFirmwareVersion }})</template
                >
                · {{ status.usbSerial ?? 'no serial reported' }}
              </template>
              <template v-else>Not connected</template>
            </span>
          </span>
          <button
            v-if="controls.showConnect"
            type="button"
            class="btn-secondary"
            :disabled="pending === 'connect'"
            title="Look for the selected load on USB and open it."
            @click="press(connect)"
          >
            {{ pending === 'connect' ? 'Connecting…' : 'Connect' }}
          </button>
        </div>

        <p
          v-if="controls.firmware"
          class="m-0 border-t border-neutral-100 p-3.5 text-[12.5px] leading-relaxed text-amber-800 dark:border-neutral-800 dark:text-amber-300"
        >
          {{ controls.firmware }}
        </p>

        <p class="m-0 border-t border-neutral-100 p-3.5 text-[12.5px] leading-relaxed text-neutral-600 dark:border-neutral-800 dark:text-neutral-400">
          Which load this bench has is set in
          <button
            type="button"
            class="font-semibold text-blue-700 underline underline-offset-2 dark:text-blue-300"
            @click="emit('configuration')"
          >
            Configuration ▸ Acquisition</button
          >, along with turning it off again.
        </p>
      </section>

      <section class="dialog-card">
        <div class="flex flex-wrap items-center justify-between gap-3 p-3.5">
          <span class="flex flex-col gap-1">
            <span class="text-sm font-semibold">Load</span>
            <span class="text-[12.5px] text-neutral-600 dark:text-neutral-400">
              What the amplifier's output is driving.
            </span>
          </span>
          <span class="tb-segment-track shrink-0">
            <button
              v-for="choice in controls.impedances"
              :key="choice.ohms"
              type="button"
              class="tb-segment"
              :class="{ 'tb-segment-on': status?.impedanceOhms === choice.ohms }"
              :disabled="!controls.enabled"
              @click="press(() => setImpedance(choice.ohms))"
            >
              {{ choice.label }}
            </button>
          </span>
        </div>
        <p
          v-if="status?.connected && status.impedanceOhms === null"
          class="m-0 border-t border-neutral-100 p-3.5 text-[12.5px] text-neutral-500 dark:border-neutral-800 dark:text-neutral-400"
        >
          Nothing is selected above because the device has not been asked what its relays are doing
          since it was opened — press one and it will say.
        </p>
        <BusyNote
          v-if="pending === 'impedance'"
          class="border-t border-neutral-100 p-3.5 dark:border-neutral-800"
          label="Switching the load relays."
        />
      </section>

      <section class="dialog-card">
        <div class="flex flex-wrap items-center justify-between gap-3 p-3.5">
          <span class="flex flex-col gap-1">
            <span class="text-sm font-semibold">DUT power</span>
            <span class="text-[12.5px] text-neutral-600 dark:text-neutral-400">
              The amplifier's own supply, through the load's high-side switch.
            </span>
          </span>
          <span class="tb-segment-track shrink-0">
            <button
              type="button"
              class="tb-segment"
              :class="{ 'tb-segment-on': status?.dutPowerOn === false }"
              :disabled="!controls.enabled"
              @click="press(() => setDutPower(false))"
            >
              Off
            </button>
            <button
              type="button"
              class="tb-segment"
              :class="{ 'tb-segment-on': status?.dutPowerOn === true }"
              :disabled="!controls.enabled"
              @click="press(() => setDutPower(true))"
            >
              On
            </button>
          </span>
        </div>
        <!-- The whole answer to a request that takes a second and a half: say what is happening and
             why it is slow, rather than leave a button looking dead. -->
        <BusyNote
          v-if="pending === 'dutPower'"
          class="border-t border-neutral-100 p-3.5 dark:border-neutral-800"
          label="Switching the DUT supply — the load runs a soft start, about a second and a half."
        />
      </section>

      <section v-if="status?.reading" class="dialog-card">
        <div class="flex flex-wrap items-center gap-6 p-3.5">
          <span class="flex flex-col gap-1">
            <span class="text-[12.5px] text-neutral-500 dark:text-neutral-400">Heatsinks</span>
            <ReadingValue :text="readout.temperature ?? '—'" />
          </span>
          <span class="flex flex-col gap-1">
            <span class="text-[12.5px] text-neutral-500 dark:text-neutral-400">DUT current</span>
            <ReadingValue :text="readout.current ?? '—'" />
          </span>
        </div>
        <p class="m-0 border-t border-neutral-100 p-3.5 text-[12.5px] leading-relaxed text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
          Whole degrees, and a current good to about 50 mA — the reading is quantised far more
          coarsely than its unit suggests, so average several for anything quantitative. Above 60 °C
          or below 5 °C the device disconnects its loads by itself.
        </p>
      </section>

      <p v-if="controls.reason" class="m-0 text-[12.5px] leading-relaxed text-neutral-500 dark:text-neutral-400">
        {{ controls.reason }}
      </p>
      <p v-if="error" class="m-0 text-[12.5px] text-red-600 dark:text-red-400">{{ error }}</p>
    </div>

    <template #footer>
      <button
        type="button"
        class="btn-secondary mr-auto"
        title="What each control does, the readings' real resolution, and what a latched fault means."
        @click="openManual('programmable-load')"
      >
        <HeaderIcon name="manual" :size="16" />
        Manual
      </button>
      <button
        type="button"
        class="btn-secondary"
        :disabled="!controls.enabled"
        title="No load, DUT power off — the state to leave the bench in."
        @click="press(safeState)"
      >
        Safe state
      </button>
      <button type="button" class="btn-primary" @click="emit('close')">Close</button>
    </template>
  </Modal>
</template>

import { computed, ref } from 'vue'
import {
  api,
  type SoundCardCalibrationResult,
  type SoundCardConfig,
  type SoundCardInventory,
  type SoundCardLevels,
  type SoundCardProbe,
  type VoltageEntryUnitName,
} from '../api/client'
import type { GainUnit } from '../soundcard/gainUnits'

/**
 * The sound card the analyzer is, and its calibration.
 *
 * Module-level like the other shared stores, because two components read it: the status bar, whose
 * Gain boxes edit the front-end, and the sound-card dialog, which picks the card and calibrates it.
 *
 * Server-side rather than `localStorage`, for the same reason as the impedance fixture: this
 * describes physical hardware on the machine the card is plugged into, has to read the same in
 * every tab, and has to survive a cache clear. It is also what the device is *built* from, so the
 * server is the only place it can live.
 */
const config = ref<SoundCardConfig | null>(null)
const inventory = ref<SoundCardInventory | null>(null)
const probe = ref<SoundCardProbe | null>(null)
const calibrationResult = ref<SoundCardCalibrationResult | null>(null)
const playingDbFs = ref<number | null>(null)
const levels = ref<SoundCardLevels | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)
let loadPromise: Promise<void> | null = null
const controlPanelOpen = ref(false)

/** Both steps done. Until then every ratio is still exact, but no level is a voltage. */
const isCalibrated = computed(
  () =>
    config.value !== null &&
    config.value.outputFullScalePeakMv !== null &&
    config.value.inputFullScalePeakMvLeft !== null &&
    config.value.inputFullScalePeakMvRight !== null,
)

const isConfigured = computed(
  () => !!config.value?.inputDeviceName && !!config.value?.outputDeviceName,
)

export function useSoundCard() {
  async function load(force = false) {
    if (loadPromise && !force) return loadPromise

    loadPromise = (async () => {
      try {
        config.value = await api.getSoundCardConfig()
        error.value = null
      } catch (e) {
        error.value = (e as Error).message
      } finally {
        loadPromise = null
      }
    })()

    return loadPromise
  }

  /** Re-reads the machine's devices. `refresh` also asks WASAPI to rescan; see the endpoint for its limits. */
  async function loadDevices(refresh = false) {
    busy.value = true
    try {
      inventory.value = await api.getSoundCardDevices(refresh)
      error.value = inventory.value.problem
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      busy.value = false
    }
  }

  /** Asks whether the chosen pairing works, and at which rates. */
  async function probePairing(next: SoundCardConfig) {
    if (!next.inputDeviceName || !next.outputDeviceName) {
      probe.value = null
      return
    }

    try {
      probe.value = await api.probeSoundCard({
        inputHostApi: next.inputHostApi,
        inputDevice: next.inputDeviceName,
        outputHostApi: next.outputHostApi,
        outputDevice: next.outputDeviceName,
        inputChannelOffset: next.inputChannelOffset,
        outputChannelOffset: next.outputChannelOffset,
      })
    } catch (e) {
      probe.value = null
      error.value = (e as Error).message
    }
  }

  /**
   * Writes the document. The server reopens the device when the sound card is the analyzer in use,
   * because both range rungs come from the calibration and capabilities are only read at connect.
   */
  async function save(next: SoundCardConfig) {
    busy.value = true
    try {
      config.value = await api.setSoundCardConfig(next)
      error.value = null
      return true
    } catch (e) {
      error.value = (e as Error).message
      return false
    } finally {
      busy.value = false
    }
  }

  /** Just the two Gain boxes, so the status bar does not have to hold the whole document. */
  async function setFrontEndGain(leftDb: number, rightDb: number, unit: GainUnit) {
    if (!config.value) await load()
    if (!config.value) return false

    return save({
      ...config.value,
      frontEndGainLeftDb: leftDb,
      frontEndGainRightDb: rightDb,
      gainDisplayUnit: unit,
    })
  }

  async function startTone(dbFs: number) {
    busy.value = true
    try {
      const state = await api.startSoundCardTone(dbFs)
      playingDbFs.value = state.playingDbFs
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      await syncCalibrationState()
    } finally {
      busy.value = false
    }
  }

  async function stopTone() {
    try {
      const state = await api.cancelSoundCardCalibration()
      playingDbFs.value = state.playingDbFs
    } catch {
      // Nothing useful to say: the dialog is closing, and the server silences the tone on its own
      // teardown paths too.
      playingDbFs.value = null
    }
  }

  /**
   * Re-reads whether a tone is up, from the server rather than from what we last asked for.
   *
   * Every path that could have silenced it ends here, including the failing ones. A failed apply
   * silences the tone server-side before it throws, and without this the button went on saying
   * 'Stop tone' over a card that had stopped -- or, worse on a path that did not silence, said
   * nothing while a 400 Hz tone was still going into whatever is patched to the outputs. Heard on
   * the bench, not found in a test.
   */
  async function syncCalibrationState() {
    try {
      playingDbFs.value = (await api.getSoundCardCalibration()).playingDbFs
    } catch {
      playingDbFs.value = null
    }
  }

  async function applyOutput(value: number, unit: VoltageEntryUnitName) {
    busy.value = true
    try {
      calibrationResult.value = await api.applySoundCardOutput(value, unit)
      error.value = null
      await load(true)
      return true
    } catch (e) {
      error.value = (e as Error).message
      return false
    } finally {
      await syncCalibrationState()
      busy.value = false
    }
  }

  /**
   * Both inputs at once through patch cables, with the known voltage coming from the output
   * calibration. Both together because a stereo loopback makes it possible: one press, both figures
   * measured under identical conditions, and the difference between them available immediately.
   */
  async function measureInputViaLoopback(dbFs: number) {
    busy.value = true
    try {
      calibrationResult.value = await api.measureSoundCardInputViaLoopback(dbFs)
      error.value = calibrationResult.value.warning
      await load(true)
      return true
    } catch (e) {
      error.value = (e as Error).message
      return false
    } finally {
      await syncCalibrationState()
      busy.value = false
    }
  }

  async function measureInput(value: number, unit: VoltageEntryUnitName, channel: 'Left' | 'Right') {
    busy.value = true
    try {
      calibrationResult.value = await api.measureSoundCardInput(value, unit, channel)
      error.value = calibrationResult.value.warning
      await load(true)
      return true
    } catch (e) {
      error.value = (e as Error).message
      return false
    } finally {
      busy.value = false
    }
  }

  /**
   * Starts polling what the inputs are hearing, for the level bars, and returns a stop function.
   *
   * A poll rather than the spectrum socket: useSpectrumStream is not a singleton, so reading it here
   * would open a second WebSocket, and the levels wanted are the calibration's own derivation rather
   * than anything that stream already carries.
   */
  function watchLevels(intervalMs = 120) {
    let stopped = false
    const tick = async () => {
      if (stopped) return
      try {
        levels.value = await api.getSoundCardLevels()
      } catch {
        levels.value = null
      }
    }

    void tick()
    const handle = setInterval(() => void tick(), intervalMs)
    return () => {
      stopped = true
      clearInterval(handle)
      levels.value = null
    }
  }

  /**
   * Raises the card's own driver panel and waits for it to be closed.
   *
   * <p>`controlPanelOpen` is separate from `busy` because this wait is unbounded — the panel stays
   * up until the user dismisses it, which may be minutes — and a dialog that merely looked busy
   * for minutes reads as a hang. The server refuses it while acquiring, and that refusal arrives
   * as an ordinary error.</p>
   */
  async function showControlPanel(hostApi: string, deviceName: string) {
    if (controlPanelOpen.value) return
    controlPanelOpen.value = true
    error.value = null
    try {
      await api.showSoundCardControlPanel(hostApi, deviceName)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      controlPanelOpen.value = false
    }
  }

  return {
    controlPanelOpen,
    showControlPanel,
    levels,
    watchLevels,
    config,
    inventory,
    probe,
    calibrationResult,
    playingDbFs,
    busy,
    error,
    isCalibrated,
    isConfigured,
    load,
    loadDevices,
    probePairing,
    save,
    setFrontEndGain,
    startTone,
    stopTone,
    syncCalibrationState,
    applyOutput,
    measureInput,
    measureInputViaLoopback,
  }
}

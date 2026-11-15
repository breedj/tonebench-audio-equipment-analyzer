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
import type { OpenedSides } from '../soundcard/probeSides'

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
/**
 * The pairing's probe, and <b>null whenever the bench does not open one stream across both
 * sides</b> — see `probeSides.ts` for the three shapes and what asking the wrong one costs.
 * The two refs below are its counterparts: one side, probed as that side will be opened.
 *
 * At most one of these arrangements is populated at a time, which is what lets a reader tell
 * "the pairing was asked" from "each card was asked" without a flag beside them.
 */
const probe = ref<SoundCardProbe | null>(null)
const inputProbe = ref<SoundCardProbe | null>(null)
const outputProbe = ref<SoundCardProbe | null>(null)
const calibrationResult = ref<SoundCardCalibrationResult | null>(null)
const playingDbFs = ref<number | null>(null)

/**
 * The calibration tone's frequency, read from the server rather than written out here — it is
 * SoundCardCalibrationRunner.ToneHz, and the dialog's own button names it. Seeded with that
 * constant's value so the label is right on the first paint, before syncCalibrationState has
 * answered; every path that opens the dialog refreshes it.
 */
const calibrationToneHz = ref(100)
const levels = ref<SoundCardLevels | null>(null)
const busy = ref(false)

/**
 * Re-enumerating, which is its own flag rather than part of `busy`.
 *
 * Refresh re-reads the machine's device table and nothing else, so disabling the calibration
 * buttons while it ran was simply wrong — and it was reported that way. The two states are
 * genuinely independent: a listing being rebuilt says nothing about whether a tone is playing.
 */
const devicesLoading = ref(false)
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
    devicesLoading.value = true
    try {
      inventory.value = await api.getSoundCardDevices(refresh)
      error.value = inventory.value.problem
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      devicesLoading.value = false
    }
  }

  /**
   * Asks whether the chosen devices open, and at which rates — <b>probing the streams this bench
   * will actually open</b> rather than always asking about a pairing.
   *
   * <p>`opens` says which sides are carried and whether one stream carries both; `openedSides`
   * derives it, and `probeSides.ts` holds the reasoning and the two faults that came of not having
   * it. The server has taken a one-direction probe since 2026-11-11 — a blank name there means
   * "this bench does not carry that direction", not a device that is missing — so the whole
   * change is which questions are asked.</p>
   *
   * <p>The default is a duplex pairing, which is what every caller wanted while a sound-card bench
   * was necessarily one card carrying both directions.</p>
   */
  async function probePairing(
    next: SoundCardConfig,
    opens: OpenedSides = { input: true, output: true, oneStream: true },
  ) {
    const wantsInput = opens.input && !!next.inputDeviceName
    const wantsOutput = opens.output && !!next.outputDeviceName

    if (!wantsInput && !wantsOutput) {
      probe.value = null
      inputProbe.value = null
      outputProbe.value = null
      return
    }

    // Every field spelled on both calls, empty where the direction is not carried: `toQuery` drops
    // only `undefined`, and the server binds these as required strings — so an omitted one is a
    // 400 rather than a blank.
    const blank = { inputHostApi: '', inputDevice: '', outputHostApi: '', outputDevice: '' }
    const inputSide = {
      ...blank,
      inputHostApi: next.inputHostApi,
      inputDevice: next.inputDeviceName,
      inputChannelOffset: next.inputChannelOffset,
    }
    const outputSide = {
      ...blank,
      outputHostApi: next.outputHostApi,
      outputDevice: next.outputDeviceName,
      outputChannelOffset: next.outputChannelOffset,
    }

    try {
      if (wantsInput && wantsOutput && opens.oneStream) {
        probe.value = await api.probeSoundCard({
          inputHostApi: next.inputHostApi,
          inputDevice: next.inputDeviceName,
          outputHostApi: next.outputHostApi,
          outputDevice: next.outputDeviceName,
          inputChannelOffset: next.inputChannelOffset,
          outputChannelOffset: next.outputChannelOffset,
        })
        inputProbe.value = null
        outputProbe.value = null
        return
      }

      // Both at once rather than one after the other: each is a native call on the one STA thread
      // PortAudio is driven from, so they serialize there anyway, and awaiting them in sequence
      // only adds a round trip to every change of a dropdown.
      const [asInput, asOutput] = await Promise.all([
        wantsInput ? api.probeSoundCard(inputSide) : Promise.resolve(null),
        wantsOutput ? api.probeSoundCard(outputSide) : Promise.resolve(null),
      ])
      probe.value = null
      inputProbe.value = asInput
      outputProbe.value = asOutput
    } catch (e) {
      probe.value = null
      inputProbe.value = null
      outputProbe.value = null
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
   * nothing while the calibration tone was still going into whatever is patched to the outputs. Heard on
   * the bench, not found in a test.
   */
  async function syncCalibrationState() {
    try {
      const state = await api.getSoundCardCalibration()
      playingDbFs.value = state.playingDbFs
      calibrationToneHz.value = state.toneHz
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
  /**
   * The output step measured by the analyzer rather than read off a voltmeter — the mirror of
   * {@link measureInputViaLoopback}, and the same shape.
   */
  async function measureOutputViaAnalyzer(dbFs: number) {
    busy.value = true
    try {
      calibrationResult.value = await api.measureSoundCardOutputViaAnalyzer(dbFs)
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

        // And take the tone's state from the same answer. Tracking it only through the requests
        // this dialog makes left it stale whenever anything else silenced the tone — a failed
        // step, a device switch, a second browser tab — and a stale "playing" keeps "Estimate
        // peak output mV" enabled over a card that has stopped. Reported 2026-10-01.
        playingDbFs.value = levels.value.playingDbFs
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
    if (controlPanelOpen.value) return null
    controlPanelOpen.value = true
    error.value = null
    try {
      return await api.showSoundCardControlPanel(hostApi, deviceName)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
      return null
    } finally {
      controlPanelOpen.value = false
    }
  }

  return {
    devicesLoading,
    /**
     * Whether the sound card is doing something that owns the device: a step running, a tone
     * playing, or the driver's panel up.
     *
     * Read by App.vue's ranging watcher, which must stand down for all three — the server refuses
     * an input range under any of them, and the refusal would otherwise surface as an error
     * banner. Enumerating is deliberately not in it; that touches no stream.
     */
    active: computed(() => busy.value || playingDbFs.value !== null || controlPanelOpen.value),
    controlPanelOpen,
    showControlPanel,
    levels,
    watchLevels,
    config,
    inventory,
    probe,
    inputProbe,
    outputProbe,
    calibrationResult,
    playingDbFs,
    calibrationToneHz,
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
    measureOutputViaAnalyzer,
  }
}

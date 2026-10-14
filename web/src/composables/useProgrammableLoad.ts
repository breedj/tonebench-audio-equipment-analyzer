import { ref } from 'vue'
import { ApiError, api, type ProgrammableLoadKindName, type ProgrammableLoadStatus } from '../api/client'
import type { LoadCommand } from '../programmableLoad/controls'

/**
 * The programmable load: one status document, the six commands, and the poll behind them.
 *
 * <p>Module-level refs, outside the exported function, because two components read them — the status
 * bar's box and the load dialog — and a command's result must be visible to both at once rather
 * than up to a poll later. The same arrangement <c>useSoundCard</c> has, and for the same reason;
 * <c>useDevice</c>'s per-caller refs work only because exactly one thing instantiates it.</p>
 *
 * <p><b>Nothing here runs on a bench without the device.</b> Few benches have a QA451, so the app
 * asks once at startup and, if no load is selected, never asks again: no interval, no box, no
 * footprint. Choosing one in Configuration ▸ Acquisition is what starts the poll — see
 * <see cref="startPolling"/>.</p>
 */

/**
 * The vendor application's own cadence. The server throttles its hardware read to 500 ms and serves
 * the cached reading in between, so this can never outrun the device.
 */
const POLL_INTERVAL_MS = 1000

const status = ref<ProgrammableLoadStatus | null>(null)
const error = ref<string | null>(null)
/** Which command is in flight, so only the pressed control has to say it is busy. */
const pending = ref<LoadCommand | null>(null)

/**
 * Whether the message in `error` is the poll's own. The poll shares its error with the dialog's
 * actions, and clearing it unconditionally on a successful tick wiped whatever the last press had
 * reported within a second — `useDevice` carries the same flag for the same reason.
 */
let pollFailed = false

/** Whether a schedule is wanted at all; false once the only consumer has unmounted. */
let polling = false
let timer: ReturnType<typeof setTimeout> | undefined

function schedule() {
  if (timer !== undefined) clearTimeout(timer)
  timer = undefined

  // A load that is not selected is not polled. This is the whole of the "no footprint when off"
  // promise: the chain simply stops rescheduling, and setKind below starts it again.
  if (!polling || status.value === null || status.value.kind === 'None') return

  timer = setTimeout(() => void tick(), POLL_INTERVAL_MS)
}

async function tick() {
  if (!polling) return
  await refresh()
  schedule()
}

async function refresh() {
  try {
    status.value = await api.getLoad()
    if (pollFailed) {
      error.value = null
      pollFailed = false
    }
  } catch (e) {
    error.value = (e as Error).message
    pollFailed = true
  }
}

/**
 * Runs a command, keeping `status` the hardware's own answer: it is assigned only from a successful
 * response, so a refused change visibly snaps back instead of leaving the screen claiming something
 * the device never did.
 */
async function run(command: LoadCommand, call: () => Promise<ProgrammableLoadStatus>) {
  pending.value = command
  try {
    status.value = await call()
    error.value = null
    pollFailed = false
  } catch (e) {
    error.value = (e as Error).message
    pollFailed = false
    // Re-read, because a refused command says nothing about what the relays are now doing.
    await refresh()
    throw e
  } finally {
    pending.value = null
    // A command may have just turned the load on or off, which starts or stops the poll.
    schedule()
  }
}

export function useProgrammableLoad() {
  /**
   * Starts the poll and returns the closure that stops it. Called once, by App.vue, rather than from
   * `onMounted` inside here: the dialog uses this same composable, and a second mount would start a
   * second poll — which is what App.vue's "the status's own poll feeds this" rule exists to prevent.
   */
  function startPolling(): () => void {
    polling = true
    void tick()

    return () => {
      polling = false
      if (timer !== undefined) clearTimeout(timer)
      timer = undefined
    }
  }

  /**
   * Retries the open. The 503 body is the whole status document, so unlike the analyzer's Connect
   * this can show the device's *own* sentence rather than the HTTP failure — and can fill the box in
   * at the same time.
   */
  async function connect() {
    pending.value = 'connect'
    try {
      status.value = await api.connectLoad()
      error.value = null
      pollFailed = false
    } catch (e) {
      if (e instanceof ApiError && e.status === 503 && e.body) {
        try {
          status.value = JSON.parse(e.body) as ProgrammableLoadStatus
          error.value = status.value.error ?? e.message
          pollFailed = false
          return
        } catch {
          // Not the document after all; fall through to the plain message.
        }
      }

      error.value = (e as Error).message
      pollFailed = false
    } finally {
      pending.value = null
      schedule()
    }
  }

  /** The bench's choice, and the gate for the whole feature. Persisted server-side. */
  const setKind = (kind: ProgrammableLoadKindName) => run('kind', () => api.setLoadKind(kind))

  const setImpedance = (ohms: number) => run('impedance', () => api.setLoadImpedance(ohms))

  /** Returns once the DUT supply is actually up — about 1.5 s, the device's soft start. */
  const setDutPower = (on: boolean) => run('dutPower', () => api.setLoadDutPower(on))

  const safeState = () => run('safeState', () => api.setLoadSafeState())

  return { status, error, pending, refresh, startPolling, connect, setKind, setImpedance, setDutPower, safeState }
}

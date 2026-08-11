import { onUnmounted, ref, shallowRef } from 'vue'
import {
  decodeHeader,
  decodeMetersFrame,
  decodeScopeFrame,
  decodeSpectrumFrame,
  encodeSetViewport,
  StreamMsgType,
  type MetersFrame,
  type ScopeFrame,
  type SpectrumFrame,
} from '../streaming/protocol'

const RECONNECT_DELAY_MS = 1000

/**
 * Owns the single /ws/stream WebSocket connection and decodes incoming binary frames straight
 * into typed views (no JSON.parse anywhere in this path). Keyed by channel (0=L, 1=R) so both
 * channels can be displayed from one connection.
 */
export function useSpectrumStream() {
  const spectrum = shallowRef<Record<number, SpectrumFrame>>({})
  const meters = shallowRef<Record<number, MetersFrame>>({})
  const scope = shallowRef<Record<number, ScopeFrame>>({})
  const connected = ref(false)

  let socket: WebSocket | null = null
  let reconnectHandle: ReturnType<typeof setTimeout> | undefined
  let stopped = false

  // Last zoom window reported to the server, kept so a reconnect can restore it — otherwise a
  // dropped connection would silently demote a zoomed-in view back to the coarse overview.
  let viewport: { loFreqHz: number; hiFreqHz: number } | null = null

  function sendViewport() {
    if (socket?.readyState !== WebSocket.OPEN) return
    // A degenerate range is how the server is told "not zoomed, send me the overview".
    socket.send(viewport ? encodeSetViewport(viewport.loFreqHz, viewport.hiFreqHz) : encodeSetViewport(0, 0))
  }

  /**
   * Tells the server which frequency range this client is displaying, so it can spend its whole
   * column budget on that range instead of the full spectrum. Pass null when zoomed all the way
   * out. Cheap to call repeatedly — the server only acts on it past its own zoom threshold.
   */
  function setViewport(next: { loFreqHz: number; hiFreqHz: number } | null) {
    viewport = next
    sendViewport()
  }

  function connect() {
    if (stopped) return

    const protocol = location.protocol === 'https:' ? 'wss' : 'ws'
    socket = new WebSocket(`${protocol}://${location.host}/ws/stream`)
    socket.binaryType = 'arraybuffer'

    socket.onopen = () => {
      connected.value = true
      if (viewport) sendViewport()
    }

    socket.onmessage = (event: MessageEvent<ArrayBuffer>) => {
      const header = decodeHeader(event.data)
      if (header.msgType === StreamMsgType.Spectrum) {
        const frame = decodeSpectrumFrame(event.data)
        spectrum.value = { ...spectrum.value, [header.channel]: frame }
      } else if (header.msgType === StreamMsgType.Meters) {
        const frame = decodeMetersFrame(event.data)
        meters.value = { ...meters.value, [header.channel]: frame }
      } else if (header.msgType === StreamMsgType.Scope) {
        const frame = decodeScopeFrame(event.data)
        scope.value = { ...scope.value, [header.channel]: frame }
      }
    }

    socket.onclose = () => {
      connected.value = false
      if (!stopped)
        reconnectHandle = setTimeout(connect, RECONNECT_DELAY_MS)
    }

    socket.onerror = () => socket?.close()
  }

  connect()

  onUnmounted(() => {
    stopped = true
    if (reconnectHandle) clearTimeout(reconnectHandle)
    socket?.close()
  })

  return { spectrum, meters, scope, connected, setViewport }
}

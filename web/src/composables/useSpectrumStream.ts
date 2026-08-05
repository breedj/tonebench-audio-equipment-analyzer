import { onUnmounted, ref, shallowRef } from 'vue'
import {
  decodeHeader,
  decodeMetersFrame,
  decodeSpectrumFrame,
  StreamMsgType,
  type MetersFrame,
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
  const connected = ref(false)

  let socket: WebSocket | null = null
  let reconnectHandle: ReturnType<typeof setTimeout> | undefined
  let stopped = false

  function connect() {
    if (stopped) return

    const protocol = location.protocol === 'https:' ? 'wss' : 'ws'
    socket = new WebSocket(`${protocol}://${location.host}/ws/stream`)
    socket.binaryType = 'arraybuffer'

    socket.onopen = () => {
      connected.value = true
    }

    socket.onmessage = (event: MessageEvent<ArrayBuffer>) => {
      const header = decodeHeader(event.data)
      if (header.msgType === StreamMsgType.Spectrum) {
        const frame = decodeSpectrumFrame(event.data)
        spectrum.value = { ...spectrum.value, [header.channel]: frame }
      } else if (header.msgType === StreamMsgType.Meters) {
        const frame = decodeMetersFrame(event.data)
        meters.value = { ...meters.value, [header.channel]: frame }
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

  return { spectrum, meters, connected }
}

import { onUnmounted, ref, shallowRef } from 'vue'
import {
  decodeHeader,
  decodeImdSweepPointFrame,
  decodeIntermodulationFrame,
  decodeMetersFrame,
  decodeNoiseFiguresFrame,
  decodeScopeFrame,
  decodeSpectrumFrame,
  decodeSweepPointFrame,
  decodeSweepProgressFrame,
  decodeFrequencyResponseFrame,
  decodeFrequencyResponseProgressFrame,
  decodeResponsePreviewFrame,
  decodeImpedanceFrame,
  decodeImpedanceProgressFrame,
  decodeMultitoneFrame,
  encodeSetViewport,
  STREAM_FLAG_IMD_SWEEP,
  STREAM_FLAG_IMD_UNRESOLVED,
  STREAM_FLAG_SCOPE_RESIDUAL,
  STREAM_FLAG_OUTPUT_IMPEDANCE,
  StreamMsgType,
  type IntermodulationFrame,
  type MetersFrame,
  type MultitoneFrame,
  type NoiseFiguresFrame,
  type ScopeFrame,
  type SpectrumFrame,
} from '../streaming/protocol'
import { useFrequencyResponse } from './useFrequencyResponse'
import { useImdSweep } from './useImdSweep'
import { useImpedance } from './useImpedance'
import { useOutputImpedance } from './useOutputImpedance'
import { useSweep } from './useSweep'

const RECONNECT_DELAY_MS = 1000

/**
 * Owns the single /ws/stream WebSocket connection and decodes incoming binary frames straight
 * into typed views (no JSON.parse anywhere in this path). Keyed by channel (0=L, 1=R) so both
 * channels can be displayed from one connection.
 */
export function useSpectrumStream() {
  const spectrum = shallowRef<Record<number, SpectrumFrame>>({})
  const meters = shallowRef<Record<number, MetersFrame>>({})
  // Present only while the pipeline has a two-tone IMD spec; keyed by channel like `meters`. The
  // owner clears it together with `meters` (App.vue's run/clear paths) — a stale IMD tile after
  // the tones changed would read as a measurement of the new stimulus.
  const intermodulation = shallowRef<Record<number, IntermodulationFrame>>({})
  // Per channel: an IMD spec is set but the pipeline could not resolve it this tick — see
  // STREAM_FLAG_IMD_UNRESOLVED. What the Intermodulation table's "FFT too small" message keys on.
  const imdUnresolved = shallowRef<Record<number, boolean>>({})
  // Present only while the generator plays a multitone comb (the server derives the spec from it);
  // keyed by channel like `meters`, and cleared together with it by the owner.
  const multitone = shallowRef<Record<number, MultitoneFrame>>({})
  // Every tick, fundamental or not: the band RMS, the noise density, the window's ENBW and the
  // weighting in effect. Cleared with `meters` by the owner for the same reason.
  const noise = shallowRef<Record<number, NoiseFiguresFrame>>({})
  const scope = shallowRef<Record<number, ScopeFrame>>({})
  // The same tick's scope tails with the fundamental removed (STREAM_FLAG_SCOPE_RESIDUAL); empty
  // unless the pipeline's ScopeResidual option is on and a fundamental was found. Published as one
  // group with `scope` so the chart can trigger on the raw left and slice these at the same offset.
  const scopeResidual = shallowRef<Record<number, ScopeFrame>>({})
  const connected = ref(false)
  const sweep = useSweep()
  const imdSweep = useImdSweep()
  const response = useFrequencyResponse()
const impedance = useImpedance()
const outputImpedance = useOutputImpedance()

  let socket: WebSocket | null = null
  let reconnectHandle: ReturnType<typeof setTimeout> | undefined
  let stopped = false

  // Last zoom window reported to the server, kept so a reconnect can restore it — otherwise a
  // dropped connection would silently demote a zoomed-in view back to the coarse overview.
  let viewport: { loFreqHz: number; hiFreqHz: number } | null = null

  // Scope frames are published a whole tick at a time, never channel by channel. The server sends
  // L and R as two separate WebSocket messages, so assigning each straight into `scope.value` (what
  // this used to do) left a window — one message long, but a real one the browser can paint in —
  // where L was the current tick's and R was still the previous tick's. The oscilloscope triggers
  // off L's zero-crossing and applies that same sample offset to R, and an R window captured ~50 ms
  // earlier has an unrelated phase at that offset, so the pair rendered as a phase error that
  // appeared and vanished frame to frame. Both channels of a tick now share the header's `seq` (see
  // SpectrumPipeline.ProcessTick), which is what makes a group identifiable at all.
  //
  // A group is published when the *next* one starts rather than when it looks complete, because
  // "complete" isn't knowable here — how many channels a tick sends depends on server-side state
  // (ProcessLeft/ProcessRight, single-shot hold, the averaging hop gate). Publishing on the next
  // frame's arrival cannot deadlock the way waiting for an expected partner could, and costs one
  // tick (~50 ms) of latency on a live display, which is not perceptible.
  //
  // ...except that there is not always a next tick. A SINGLE capture holds the channel as soon as
  // its record completes (SpectrumPipeline.ProcessTick skips a held channel), so the very frames the
  // user asked for were the last to arrive and sat in `pendingScope` for ever: the oscilloscope
  // stayed on "Waiting for data…" after a SINGLE with Averages = 1, while the spectrum, the meters
  // and the harmonics — none of which are grouped — all filled in. With 8 averages it *looked* right
  // and was not: what stood on screen was the second-to-last tick, the last one still pending.
  // Reported 2026-09-27. So a group also publishes itself after a short idle, which is the "looks
  // complete" test the comment above says cannot be made exact — but it does not have to be: it only
  // has to outlast the gap between one tick's L and R, which are two adjacent sends on one socket.
  // Well under 40 ms, and a live stream never reaches the timer anyway because the next tick's first
  // frame publishes the group first.
  const SCOPE_GROUP_IDLE_MS = 40
  let pendingScope: Record<number, ScopeFrame> = {}
  let pendingScopeResidual: Record<number, ScopeFrame> = {}
  let pendingScopeSeq: number | null = null
  let pendingScopeTimer: ReturnType<typeof setTimeout> | undefined
  let publishedScopeSeq: number | null = null

  function publishPendingScope() {
    if (pendingScopeTimer !== undefined) {
      clearTimeout(pendingScopeTimer)
      pendingScopeTimer = undefined
    }
    if (pendingScopeSeq === null) return
    scope.value = pendingScope
    scopeResidual.value = pendingScopeResidual
    publishedScopeSeq = pendingScopeSeq
    pendingScope = {}
    pendingScopeResidual = {}
    pendingScopeSeq = null
  }

  function ingestScopeFrame(seq: number, channel: number, frame: ScopeFrame, residual: boolean) {
    // A residual frame for a tick that has already been published joins it, rather than opening a
    // group of its own. Without this a residual that misses the deadline is not merely late, it is
    // destructive: the group it starts has no *raw* frame, so the next publish replaces a complete
    // tick with a residual-only one, OscilloscopeChart finds nothing to trigger on and draws an
    // empty plot, and the display alternates between that and a raw frame whose missing residual is
    // announced as "no fundamental to remove". The server no longer lets the two drift that far
    // apart (SpectrumPipeline takes the sine fit before either frame goes out), so this is a
    // backstop for a slow client rather than the fix; a residual older than the last published tick
    // still falls through to be dropped as stale.
    if (residual && pendingScopeSeq === null && seq === publishedScopeSeq) {
      scopeResidual.value = { ...scopeResidual.value, [channel]: frame }
      return
    }
    if (pendingScopeSeq !== null && seq !== pendingScopeSeq) publishPendingScope()
    pendingScopeSeq = seq
    if (residual) pendingScopeResidual = { ...pendingScopeResidual, [channel]: frame }
    else pendingScope = { ...pendingScope, [channel]: frame }
    // Armed on the frame that opens a group and left alone afterwards, so the deadline is measured
    // from the group's own start rather than being pushed out by each channel that joins it.
    if (pendingScopeTimer === undefined) pendingScopeTimer = setTimeout(publishPendingScope, SCOPE_GROUP_IDLE_MS)
  }

  /**
   * Resets the oscilloscope display — use this rather than assigning `scope.value = {}`, which
   * would clear only what is on screen and leave the half-built group above intact, so the next
   * frame to arrive would publish a tick captured before the clear. The idle timer goes with it, or
   * a group abandoned by the clear would publish itself a moment later.
   */
  function clearScope() {
    if (pendingScopeTimer !== undefined) {
      clearTimeout(pendingScopeTimer)
      pendingScopeTimer = undefined
    }
    pendingScope = {}
    pendingScopeResidual = {}
    pendingScopeSeq = null
    // Or a residual frame still in flight for the tick that was on screen would merge itself back
    // into the display this just cleared.
    publishedScopeSeq = null
    scope.value = {}
    scopeResidual.value = {}
  }

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
      // A half-built group from before the drop would otherwise be published as this connection's
      // first tick, and the server restarts its sequence numbering on Start() anyway.
      pendingScope = {}
      pendingScopeSeq = null
      publishedScopeSeq = null
      if (viewport) sendViewport()
    }

    socket.onmessage = (event: MessageEvent<ArrayBuffer>) => {
      const header = decodeHeader(event.data)
      if (header.msgType === StreamMsgType.Spectrum) {
        const frame = decodeSpectrumFrame(event.data)
        // The noise frame of the same tick arrives first (the server sends it ahead of the
        // spectrum), so its ENBW is the one that describes this frame's window.
        const noiseFrame = noise.value[header.channel]
        if (noiseFrame) frame.enbwHz = noiseFrame.enbwHz
        spectrum.value = { ...spectrum.value, [header.channel]: frame }
      } else if (header.msgType === StreamMsgType.NoiseFigures) {
        noise.value = { ...noise.value, [header.channel]: decodeNoiseFiguresFrame(event.data) }
      } else if (header.msgType === StreamMsgType.Meters) {
        const frame = decodeMetersFrame(event.data)
        meters.value = { ...meters.value, [header.channel]: frame }
      } else if (header.msgType === StreamMsgType.Intermodulation) {
        if (header.flags & STREAM_FLAG_IMD_UNRESOLVED) {
          const { [header.channel]: _dropped, ...rest } = intermodulation.value
          intermodulation.value = rest
          imdUnresolved.value = { ...imdUnresolved.value, [header.channel]: true }
        } else {
          intermodulation.value = { ...intermodulation.value, [header.channel]: decodeIntermodulationFrame(event.data) }
          imdUnresolved.value = { ...imdUnresolved.value, [header.channel]: false }
        }
      } else if (header.msgType === StreamMsgType.Multitone) {
        multitone.value = { ...multitone.value, [header.channel]: decodeMultitoneFrame(event.data) }
      } else if (header.msgType === StreamMsgType.Scope) {
        ingestScopeFrame(header.seq, header.channel, decodeScopeFrame(event.data), (header.flags & STREAM_FLAG_SCOPE_RESIDUAL) !== 0)
      } else if (header.msgType === StreamMsgType.SweepPoint) {
        // Sweep frames go straight into useSweep's singleton store rather than being returned
        // from here: a sweep is one server-side run shared by every component looking at it, not
        // per-subscriber display state like the three refs above.
        sweep.ingestPoint(decodeSweepPointFrame(event.data))
      } else if (header.msgType === StreamMsgType.SweepProgress) {
        // Two runners share the progress frame; the flag says whose it is (see
        // StreamFlags.IntermodulationSweep), so a running IMD sweep never paints the THD screen.
        if (header.flags & STREAM_FLAG_IMD_SWEEP) imdSweep.ingestProgress(decodeSweepProgressFrame(event.data))
        else sweep.ingestProgress(decodeSweepProgressFrame(event.data))
      } else if (header.msgType === StreamMsgType.ImdSweepPoint) {
        imdSweep.ingestPoint(decodeImdSweepPointFrame(event.data))
      } else if (header.msgType === StreamMsgType.FrequencyResponse) {
        // Same reasoning as the sweep frames above: one server-side run shared by every component
        // looking at it, so it goes into useFrequencyResponse's singleton rather than a local ref.
        response.ingestTrace(decodeFrequencyResponseFrame(event.data))
      } else if (header.msgType === StreamMsgType.FrequencyResponseProgress) {
        response.ingestProgress(decodeFrequencyResponseProgressFrame(event.data))
      } else if (header.msgType === StreamMsgType.ResponsePreview) {
        // One point's capture for the preview rail. Routed by the same origin flag the trace and
        // progress frames carry, so it lands in the run it belongs to and not the screen on top.
        response.ingestPreview(decodeResponsePreviewFrame(event.data))
      } else if (header.msgType === StreamMsgType.Impedance) {
        // Its own message type rather than a flagged frequency-response frame: the payload really is
        // different, since an impedance curve has no bandwidth blocks. See StreamMsgType.Impedance.
        // The output-impedance runner speaks the same frame with its own origin bit, and its curve
        // belongs to its own store — the response frame's stepped/crosstalk arrangement.
        if ((header.flags & STREAM_FLAG_OUTPUT_IMPEDANCE) !== 0) outputImpedance.ingestTrace(decodeImpedanceFrame(event.data))
        else impedance.ingestTrace(decodeImpedanceFrame(event.data))
      } else if (header.msgType === StreamMsgType.ImpedanceProgress) {
        if ((header.flags & STREAM_FLAG_OUTPUT_IMPEDANCE) !== 0) outputImpedance.ingestProgress(decodeImpedanceProgressFrame(event.data))
        else impedance.ingestProgress(decodeImpedanceProgressFrame(event.data))
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
    if (pendingScopeTimer !== undefined) clearTimeout(pendingScopeTimer)
    socket?.close()
  })

  return { spectrum, meters, intermodulation, imdUnresolved, multitone, noise, scope, scopeResidual, connected, setViewport, clearScope }
}

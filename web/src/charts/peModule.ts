import { Enums, attachApi } from 'proessentials/dist/pe-api-3d.js'
// Vite emits the engine beside the bundle under a content hash and hands back its URL. The import
// is mandatory rather than tidy: the engine resolves `proessentials.wasm` against
// `document.currentScript.src`, which is null inside an ES module, so without `locateFile` the
// fetch falls back to the page URL and 404s. The package's own types say so.
import wasmUrl from 'proessentials/dist/proessentials.wasm?url'

/**
 * <p>The one ProEssentials engine instance for the page, and the only module that names the
 * package.</p>
 *
 * <p><b>Memoised at module scope deliberately.</b> <c>report/renderChart.ts</c> mounts a chart
 * through a <em>separate</em> <c>createApp</c>, and its own remarks say a headless chart works
 * because that app shares the real one's module-level singletons. One module backs several
 * controls, which is what lets the export build a second control at the report's size while the
 * live one stays on screen.</p>
 *
 * <p>Everything here is reached only from <c>DecaySurface3dChart.vue</c>, which <c>App.vue</c>
 * loads with <c>defineAsyncComponent</c> — so a bench that never opens an impulse measurement
 * never fetches the 2.8 MB engine.</p>
 */

export interface PeRuntime {
  /** The instantiated wasm engine, passed to every control. */
  module: unknown
  /** Binds the 3D property API to a control's handle. */
  attachApi: typeof attachApi
  /** The 3D enum tables. */
  Enums: typeof Enums
}

let runtime: Promise<PeRuntime> | null = null

export function peRuntime(): Promise<PeRuntime> {
  runtime ??= load()
  return runtime
}

async function load(): Promise<PeRuntime> {
  // The package's entry point is a UMD bundle: importing it both returns the factory and installs
  // `PeControl` and its friends on the window, which is where the control class is read from.
  const factory = (await import('proessentials')).default
  const module = await factory({
    locateFile: (path: string) => (path.endsWith('.wasm') ? wasmUrl : path),
    // Nothing in the app reads the engine's stdout, and its stderr is worth a console line rather
    // than a thrown error: a chart that draws with a grumble is better than one that does not.
    print: () => {},
    printErr: (text: string) => console.warn('[proessentials]', text),
  })
  return { module, attachApi, Enums }
}

/**
 * Options every control is built with.
 *
 * <p><c>builtins: false</c> stops the engine fetching its QuickStyle desk bitmaps
 * (<c>dist/bmps/1700.png</c>…<c>1710.png</c>, about 350 kB) — they would paint a desk texture
 * under a flat themed app, and they are the only thing the engine fetches beyond the wasm.</p>
 *
 * <p><c>autoResize: false</c> because <c>useResultChartHeight</c> is the authority on height in
 * this app. Two observers writing a size back at each other is how "ResizeObserver loop completed
 * with undelivered notifications" starts.</p>
 *
 * <p><c>polyMode: 0</c> is what the vendor's own 3D demo passes; it is the polygon mode handed to
 * the engine's create call, before any property write can reach it.</p>
 */
export const PE_CONTROL_DEFAULTS = {
  kind: 'sgraph3d',
  polyMode: 0,
  builtins: false,
  autoResize: false,
} as const

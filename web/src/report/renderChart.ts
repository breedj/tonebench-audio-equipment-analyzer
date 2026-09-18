import { createApp, nextTick, type Component, type InjectionKey } from 'vue'
import type { ChartExportOptions } from '../charts/exportChart'

/** What a chart component has to expose (`defineExpose({ exportPng })`) to be rendered for a report. */
export interface ExportableChart {
  exportPng(options: ChartExportOptions): Promise<Blob>
}

/** A value to `provide` to the headless app — how a chart that reads a shared store is handed a stored result's state instead. */
export interface ChartProvision<T = unknown> {
  key: InjectionKey<T>
  value: T
}

/**
 * Renders a chart component headless — mounted into a host nobody sees, asked for its PNG, unmounted
 * — so a report can carry the chart of a stored measurement that is not on screen, and the chart of
 * the live one without touching the visible instance.
 *
 * The component's own `exportPng` builds the offscreen plot from its props; the mounted instance only
 * exists to *hold* those props, so its visible plot inside the host is never used. Two things make
 * this safe: `useResultChartHeight` measures nothing when its root is not inside a `<main>` (it
 * simply keeps its default), and a separate `createApp` shares the module-level singletons
 * (`useAppTheme`, the expanded-chart flag) with the real app, which is what the chart expects. A
 * chart that reads a shared store for its *data* or display state (the spectrum and sweep charts)
 * takes a `provide` instead, under the key it injects — see SPECTRUM_DISPLAY_KEY, SWEEP_POINTS_KEY.
 * Unmounted in a `finally` so a failed export cannot leave a hidden chart observing resizes forever.
 */
export async function renderChartPng(
  component: Component,
  props: Record<string, unknown>,
  size: ChartExportOptions,
  provisions: ChartProvision[] = [],
): Promise<Blob> {
  const host = document.createElement('div')
  host.style.cssText = `position:fixed;left:-100000px;top:0;width:${size.width}px;height:${size.height}px;pointer-events:none`
  document.body.appendChild(host)

  const app = createApp(component, props)
  for (const provision of provisions) app.provide(provision.key, provision.value)
  try {
    const instance = app.mount(host) as unknown as Partial<ExportableChart>
    if (typeof instance.exportPng !== 'function') {
      throw new Error('This chart cannot be exported: it exposes no exportPng.')
    }
    // One tick so the component's computed state settles from the props before the export reads it.
    await nextTick()
    return await instance.exportPng({ ...size, captureZoom: false })
  } finally {
    app.unmount()
    host.remove()
  }
}

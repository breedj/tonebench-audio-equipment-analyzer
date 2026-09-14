/**
 * The drawing height, in CSS pixels, a full-width result chart starts at before it has measured
 * the view it sits in — see `useResultChartHeight`, which fits every result chart to the window
 * less the header, the status bar and the chart's own furniture. This is what a chart is built
 * with when that measurement is not available (no `<main>` ancestor, a test), and what it falls
 * back to when a box reports no height.
 *
 * One constant rather than a literal in each file: the charts are the same screen furniture seen
 * from different tabs, and a difference between them reads as something being wrong with one of
 * them rather than as a choice. They were 560 and 420 for no reason other than being written
 * months apart.
 */
export const RESULT_CHART_HEIGHT_PX = 560

/**
 * The height a result chart will not shrink below however short the window is. Past this the
 * chart scrolls rather than squashes — a level axis with its ticks on top of each other reads
 * nothing at all.
 */
export const MIN_RESULT_CHART_HEIGHT_PX = 320

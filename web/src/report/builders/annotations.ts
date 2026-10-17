/**
 * The Annotations tables: the notes drawn on a chapter's charts, numbered the way each chart
 * numbers them.
 *
 * <p>One builder for every kind, because an annotation is the same thing on a spectrum, a sweep and
 * a Nyquist plot — and because `numberedAnnotations` is the one rule a chart's badges and these rows
 * both read, which is what lets a reader tie a label on a picture to a row on the page. It was part
 * of the spectrum builder while annotations were an FFT feature.</p>
 *
 * <p><b>One table per printed view.</b> A measurement keeps one flat list across its charts and
 * numbering is per view, so a chapter that prints a Bode plot and a Nyquist plot gets two tables,
 * each starting at 1 and each matching its own picture. One title is "Annotations"; several name
 * their view, because two tables called the same thing is worse than none.</p>
 */

import type { ReportSection } from '../../api/client'
import { annotationQuantity } from '../../charts/annotationQuantities'
import { annotationViewLabel, annotationViewsForKind } from '../../charts/annotationViews'
import { numberedAnnotations, type ChartAnnotation } from '../../measurements/chartAnnotations'
import type { ReportSubject, ReportSubjectData } from '../types'

const CHANNEL_LABELS = ['L', 'R'] as const

/**
 * A subject's annotations, whichever kind it is.
 *
 * <p>The FFT's display is a bag of refs (it is the live screen's own state, injected), every other
 * kind's is a plain object read out of a stored `params_json`. That asymmetry is the whole reason
 * this is a switch rather than one property read.</p>
 */
export function annotationsOf(data: ReportSubjectData): ChartAnnotation[] {
  switch (data.kind) {
    case 'spectrum':
      return data.display.annotations.value
    case 'response':
    case 'sweep':
    case 'impulse':
    case 'impedance':
    case 'outputImpedance':
    case 'crosstalk':
      return data.display.annotations
    default:
      return []
  }
}

/**
 * One table per view this chapter printed a chart of.
 *
 * <p>Silent when there are none — unlike the harmonics table, which says why it is empty. "No
 * annotations" on every un-annotated chapter of a project report is noise rather than
 * information.</p>
 *
 * @param views The views whose charts this chapter prints, so a note on a picture nobody printed is
 *   not tabulated beside one that was. A chapter that prints no chart at all still prints every
 *   table, because then the tables are the only record of the notes.
 */
export function annotationsSections(subject: ReportSubject, views?: readonly string[]): ReportSection[] {
  const all = annotationsOf(subject.data)
  if (all.length === 0) return []

  const ofKind = annotationViewsForKind(subject.kind)
  const printed = views === undefined || views.length === 0 ? ofKind : ofKind.filter((v) => views.includes(v))
  // A view this build does not know -- a note from a newer one -- still gets its table rather than
  // being dropped: the parser kept the row, so the report keeps the user's words.
  const unknown = all.map((a) => a.view).filter((v) => !ofKind.includes(v))
  const order = [...printed, ...unknown.filter((v, i) => unknown.indexOf(v) === i)]

  const tables = order
    .map((view) => ({ view, rows: numberedAnnotations(all, view) }))
    .filter((table) => table.rows.length > 0)

  return tables.map((table) =>
    tableOf(table.rows, tables.length > 1 ? `Annotations — ${annotationViewLabel(table.view)}` : 'Annotations'),
  )
}

function tableOf(rows: ReturnType<typeof numberedAnnotations>, title: string): ReportSection {
  // The column heads come from the quantities the rows are stored in, so a sweep's table says
  // "Output power" over watts where a spectrum's says "Frequency" over hertz. The first row decides,
  // which is safe because a view's rows are all drawn against one pair of axes.
  const first = rows[0]
  const xLabel = annotationQuantity(first.xQuantity).label
  const yLabel = annotationQuantity(first.yQuantity).label

  // The channel column only where some annotation names one: a chart of notes on frequencies alone
  // would otherwise carry a column of dashes.
  const withChannel = rows.some((a) => a.channel !== null)

  return {
    kind: 'table',
    title,
    headers: ['#', xLabel, yLabel, ...(withChannel ? ['Channel'] : []), 'Note'],
    // A region carries its ranges in the same two columns rather than earning two more: a pair of
    // extra columns would widen the table for a few rows and leave every point dashing through them.
    cells: rows.map((a) => [
      `${a.number}`,
      a.style === 'box' && a.x2 !== null
        ? annotationQuantity(a.xQuantity).formatRange(a.x, a.x2)
        : annotationQuantity(a.xQuantity).format(a.x),
      a.y === null
        ? '—'
        : a.style === 'box' && a.y2 !== null
          ? annotationQuantity(a.yQuantity).formatRange(a.y2, a.y)
          : annotationQuantity(a.yQuantity).format(a.y),
      ...(withChannel ? [a.channel === null ? '—' : CHANNEL_LABELS[a.channel]] : []),
      a.text || '—',
    ]),
  }
}

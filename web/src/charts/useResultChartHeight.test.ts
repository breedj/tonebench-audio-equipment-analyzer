import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { CHART_STACK_ATTRIBUTE, fitFrom } from './useResultChartHeight'

/** The two lines of `closest` the node environment does not have. */
interface Node {
  readonly name: string
  readonly stack?: boolean
  readonly parent?: Node
  closest(selector: string): Node | null
}

function node(name: string, parent?: Node, stack = false): Node {
  const self: Node = {
    name,
    stack,
    parent,
    closest(selector) {
      for (let at: Node | undefined = self; at; at = at.parent) {
        if (at.stack && selector === `[${CHART_STACK_ATTRIBUTE}]`) return at
      }
      return null
    },
  }
  return self
}

describe('fitFrom', () => {
  /**
   * The report of 2026-10-27: the second, third and fourth decay charts came out shorter than the
   * first. Each was fitted from its own top, so it reserved every chart above it and landed on the
   * minimum height. Fitted from the column, they all get one number.
   */
  it('fits every chart of a column from the column, so they are all the same height', () => {
    const column = node('column', undefined, true)
    const primary = node('primary', column)
    const second = node('second', node('second-block', column))
    const third = node('third', node('third-block', column))

    expect(fitFrom(primary)).toBe(column)
    expect(fitFrom(second)).toBe(column)
    expect(fitFrom(third)).toBe(column)
  })

  /** The ordinary chart is unchanged, which is the half that must not move while this is fixed. */
  it('fits a chart that is in no column from itself', () => {
    const chart = node('chart', node('page'))
    expect(fitFrom(chart)).toBe(chart)
  })

  /** A column of one is the same thing as no column: its chart is at the column's top anyway. */
  it('is the column even where the column holds one chart', () => {
    const column = node('column', undefined, true)
    const only = node('only', column)
    expect(fitFrom(only)).toBe(column)
  })
})

describe('the decay column', () => {
  /**
   * Half of the fix is an attribute in a template, which nothing else would miss if it were
   * deleted — the charts would simply go back to being shorter one by one. Read as text, since a
   * `.vue` is not testable in the node environment vitest runs in here.
   */
  it('is marked in App.vue, where the stacked decay charts are drawn', () => {
    const app = readFileSync(new URL('../App.vue', import.meta.url), 'utf8')
    expect(app).toContain(CHART_STACK_ATTRIBUTE)
  })

  /** And no decay chart may carry a kept height again, which would bypass the fit entirely. */
  it('passes no page height to the decay charts', () => {
    const app = readFileSync(new URL('../App.vue', import.meta.url), 'utf8')
    expect(app).not.toContain('impulseDecayChartHeight')
  })
})

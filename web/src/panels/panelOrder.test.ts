import { describe, expect, it } from 'vitest'
import { panelOrderAfterDrop, panelOrders, panelSlug, railOrderAfterDrag } from './panelOrder'

/**
 * What a rail's children look like to this module: ids in DOM order, null for anything else.
 * This is the oscilloscope's settings rail -- the run row and the Autoset button, then the panels.
 */
const RAIL = [null, null, 'generator', 'acquisition', 'trigger'] as const

describe('panelSlug', () => {
  it('is a stable id for a fixed heading', () => {
    expect(panelSlug('Time window (ms)')).toBe('time-window-ms')
    expect(panelSlug('Left: Other peaks')).toBe('left-other-peaks')
  })

  /** Which is exactly why a computed title has to declare an id instead. */
  it('moves when the heading does', () => {
    expect(panelSlug('dB range')).not.toBe(panelSlug('Distortion range'))
  })
})

describe('panelOrders', () => {
  it('leaves a rail nobody has dragged in the order the screen gives it', () => {
    expect(panelOrders(RAIL, [])).toEqual([0, 1, 2, 3, 4])
  })

  it('puts the panels in the stored order', () => {
    // Trigger first, then generator, then acquisition.
    expect(panelOrders(RAIL, ['trigger', 'generator', 'acquisition'])).toEqual([0, 1, 3, 4, 2])
  })

  /**
   * The rule that keeps Run/Single/Stop at the top of the settings rail whatever is dragged below
   * it: a child that is not a panel is never part of the permutation.
   */
  it('never moves a child that is not a panel', () => {
    const orders = panelOrders(RAIL, ['trigger', 'acquisition', 'generator'])

    expect(orders[0]).toBe(0)
    expect(orders[1]).toBe(1)
    // And the three panels are still in the three slots they had.
    expect([orders[2], orders[3], orders[4]].slice().sort()).toEqual([2, 3, 4])
  })

  it('keeps a panel the stored list does not know beside the one above it', () => {
    // 'acquisition' is new; stored names only the other two, trigger first.
    const orders = panelOrders(RAIL, ['trigger', 'generator'])
    const drawn = railOrderAfterDrag(RAIL, orders)

    expect(drawn).toEqual(['trigger', 'generator', 'acquisition'])
  })

  it('ignores a stored id the rail is not showing', () => {
    expect(panelOrders(RAIL, ['gone', 'trigger', 'generator', 'acquisition'])).toEqual(
      panelOrders(RAIL, ['trigger', 'generator', 'acquisition']),
    )
  })

  it('answers one order per child, and every slot exactly once', () => {
    const orders = panelOrders(RAIL, ['acquisition', 'trigger', 'generator'])

    expect(orders).toHaveLength(RAIL.length)
    expect(orders.slice().sort((a, b) => a - b)).toEqual([0, 1, 2, 3, 4])
  })
})

describe('railOrderAfterDrag', () => {
  it('reads back the panels, top first, and leaves everything else out', () => {
    expect(railOrderAfterDrag(RAIL, [0, 1, 4, 2, 3])).toEqual(['acquisition', 'trigger', 'generator'])
  })
})

describe('panelOrderAfterDrop', () => {
  const drawn = ['generator', 'acquisition', 'trigger']

  it('drops a panel above the one it was dropped on', () => {
    expect(panelOrderAfterDrop(drawn, 'trigger', 'generator', false)).toEqual([
      'trigger',
      'generator',
      'acquisition',
    ])
  })

  /** The midpoint split is what makes the last position reachable — reorder.ts's own rule. */
  it('drops it below when the pointer is past the midpoint', () => {
    expect(panelOrderAfterDrop(drawn, 'generator', 'trigger', true)).toEqual([
      'acquisition',
      'trigger',
      'generator',
    ])
  })

  it('is a no-op on the panel being dragged', () => {
    expect(panelOrderAfterDrop(drawn, 'acquisition', 'acquisition', false)).toEqual(drawn)
    expect(panelOrderAfterDrop(drawn, 'acquisition', 'acquisition', true)).toEqual(drawn)
  })

  it('answers the list unchanged for an id it does not hold', () => {
    expect(panelOrderAfterDrop(drawn, 'nope', 'trigger', false)).toEqual(drawn)
    expect(panelOrderAfterDrop(drawn, 'trigger', 'nope', false)).toEqual(drawn)
  })
})

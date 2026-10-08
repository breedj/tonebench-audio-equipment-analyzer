import { describe, expect, it } from 'vitest'
import { railAtNewWidth, railCollapsed, railToggled, type RailState } from './railCollapse'

const open: RailState = { stored: false, overrideOpen: false }
const storedClosed: RailState = { stored: true, overrideOpen: false }

describe('railCollapsed', () => {
  it('is the stored preference on a window wide enough for the rail', () => {
    expect(railCollapsed(open, true)).toBe(false)
    expect(railCollapsed(storedClosed, true)).toBe(true)
  })

  it('folds on a narrow window whatever the preference says', () => {
    expect(railCollapsed(open, false)).toBe(true)
    expect(railCollapsed(storedClosed, false)).toBe(true)
  })

  it('stays open on a narrow window once it has been opened by hand', () => {
    expect(railCollapsed({ stored: false, overrideOpen: true }, false)).toBe(false)
    expect(railCollapsed({ stored: true, overrideOpen: true }, false)).toBe(false)
  })
})

describe('railToggled', () => {
  it('writes the preference on a wide window', () => {
    expect(railToggled(open, true)).toEqual({ stored: true, overrideOpen: false })
    expect(railToggled(storedClosed, true)).toEqual({ stored: false, overrideOpen: false })
  })

  /**
   * The button has to do something at a width that folded the rail — a control that writes a
   * preference nothing on screen reflects is worse than no control.
   */
  it('opens and closes a width-folded rail without touching the preference', () => {
    const opened = railToggled(open, false)
    expect(opened).toEqual({ stored: false, overrideOpen: true })
    expect(railCollapsed(opened, false)).toBe(false)

    const closedAgain = railToggled(opened, false)
    expect(closedAgain).toEqual({ stored: false, overrideOpen: false })
    expect(railCollapsed(closedAgain, false)).toBe(true)
  })

  /** A rail the window folded is one the user did not ask to fold, so opening it records nothing. */
  it('leaves a stored "collapsed" alone when opened on a narrow window', () => {
    expect(railToggled(storedClosed, false)).toEqual({ stored: true, overrideOpen: true })
  })
})

describe('railAtNewWidth', () => {
  it('keeps the preference and drops the by-hand override', () => {
    expect(railAtNewWidth({ stored: true, overrideOpen: true })).toEqual({ stored: true, overrideOpen: false })
    expect(railAtNewWidth({ stored: false, overrideOpen: true })).toEqual({ stored: false, overrideOpen: false })
  })

  /**
   * Which is what makes a window dragged narrow, opened by hand, dragged wide and dragged narrow
   * again agree with the same window simply opened at that size.
   */
  it('leaves a rail opened by hand folded again after a round trip through a wide window', () => {
    const openedWhileNarrow = railToggled(open, false)
    const wide = railAtNewWidth(openedWhileNarrow)
    const narrowAgain = railAtNewWidth(wide)

    expect(railCollapsed(narrowAgain, false)).toBe(true)
  })
})

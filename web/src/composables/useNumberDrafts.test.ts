import { describe, expect, it } from 'vitest'
import { useNumberDrafts } from './useNumberDrafts'

/** A stand-in for the input element a handler is handed. */
function typed(value: string): Event {
  return { target: { value } } as unknown as Event
}

describe('useNumberDrafts', () => {
  it('shows the committed figure while nobody is typing', () => {
    const drafts = useNumberDrafts()

    expect(drafts.show('time', 0.25)).toBe('0.25')
  })

  /**
   * The fault, stated: a re-render asks what the box should show, and while one is being typed into
   * the answer must be what was typed. Bound to the committed figure instead, Vue's `value`
   * carve-out re-stamps the element on every render and "0.5" on its way to "0.05" is gone before
   * `change` can fire.
   */
  it('keeps what is being typed, whatever the committed figure does meanwhile', () => {
    const drafts = useNumberDrafts()

    drafts.type('time', typed('0.'))
    expect(drafts.show('time', 0.25)).toBe('0.')

    // The record arrives, the panel re-renders, the committed figure is unchanged — and so is the
    // box. Then it changes under the box, which must not reach it either.
    expect(drafts.show('time', 0.25)).toBe('0.')
    expect(drafts.show('time', 9.99)).toBe('0.')

    drafts.type('time', typed('0.05'))
    expect(drafts.show('time', 0.25)).toBe('0.05')
  })

  it('hands the box back to its committed figure once the edit ends', () => {
    const drafts = useNumberDrafts()

    drafts.type('time', typed('0.05'))
    drafts.end('time')

    expect(drafts.show('time', 0.05)).toBe('0.05')
    // And it is the owner's figure from then on, not the text that produced it: a handler that
    // clamps a rejected value writes that value back, and the next render must agree with it.
    expect(drafts.show('time', 0)).toBe('0')
  })

  it('keeps each box to itself', () => {
    const drafts = useNumberDrafts()

    drafts.type('from', typed('-1'))

    expect(drafts.show('from', 0)).toBe('-1')
    expect(drafts.show('to', 2.25)).toBe('2.25')
  })

  /**
   * An empty box is a state the draft has to be able to express — it is what every retype passes
   * through — and `v-model.number` cannot, which is half of why this exists.
   */
  it('lets a box be empty', () => {
    const drafts = useNumberDrafts()

    drafts.type('time', typed(''))

    expect(drafts.show('time', 0.25)).toBe('')
  })

  it('ignores an end for a box nobody was typing in', () => {
    const drafts = useNumberDrafts()

    expect(() => drafts.end('never-touched')).not.toThrow()
    expect(drafts.show('never-touched', 7)).toBe('7')
  })
})

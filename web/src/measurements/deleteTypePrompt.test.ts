import { describe, expect, it } from 'vitest'
import { deleteTypeConfirmation } from './deleteTypePrompt'

/**
 * The sidebar's ✕ deletes a measurement *and* every result ever saved under it, and the server
 * never refuses — one click used to do all of that with no question at all, which took a project
 * file's "Amp 1 kHz at 1 W" and its five stored results on 2026-10-02. These pin the two things the
 * dialog has to get right: it is marked destructive, and it says how many results go, since the
 * sidebar row itself shows only a name.
 */
describe('deleteTypeConfirmation', () => {
  it('is a destructive, explicitly irreversible question about the named measurement', () => {
    const options = deleteTypeConfirmation('Amp 1 kHz at 1 W', 5)

    expect(options.danger).toBe(true)
    expect(options.confirmLabel).toBe('Delete')
    expect(options.message).toContain('"Amp 1 kHz at 1 W"')
    expect(options.message).toContain('cannot be undone')
  })

  it('names the number of stored results that go with the type', () => {
    expect(deleteTypeConfirmation('Amp', 5).message).toContain('All 5 results')
    expect(deleteTypeConfirmation('Amp', 137).message).toContain('All 137 results')
  })

  it('does not say "1 results"', () => {
    const message = deleteTypeConfirmation('Amp', 1).message

    expect(message).toContain('The 1 result saved under it')
    expect(message).not.toContain('1 results')
  })

  it('says there is nothing stored only when the count really is zero', () => {
    expect(deleteTypeConfirmation('Never run', 0).message).toContain('no saved results')
  })

  /**
   * The count is a request of its own, so it can fail (or be refused with the project already
   * closed). Reporting that as zero is the one wording here that would talk someone into the
   * delete, so an unknown count must warn instead — this is the assertion that pins it.
   */
  it('warns rather than claiming there is nothing to lose when the count could not be read', () => {
    const message = deleteTypeConfirmation('Amp', null).message

    expect(message).not.toContain('no saved results')
    expect(message).toContain('could not be read')
    expect(message).toContain('there may be some')
  })

  /** A negative count is a bug upstream, not a reason to promise results that are not there. */
  it('treats a nonsensical negative count as none rather than printing it', () => {
    expect(deleteTypeConfirmation('Amp', -1).message).toContain('no saved results')
    expect(deleteTypeConfirmation('Amp', -1).message).not.toContain('-1')
  })
})

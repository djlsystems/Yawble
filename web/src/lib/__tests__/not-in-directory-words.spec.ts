import { describe, expect, it } from 'vitest'
import { AdminApprovalSentence, NotInDirectorySentence, refusalWords } from '../connections'

describe('refusalWords for an account not in the directory', () => {
  it.each(['AADSTS50020', 'AADSTS50034', 'AADSTS90072'])('reads %s as the sentence wherever it sits in the text', (code) => {
    expect(refusalWords(code)).toBe(NotInDirectorySentence)
    expect(refusalWords(`Microsoft refused the sign-in: invalid_grant: ${code}: words. Trace ID: 0000`)).toBe(NotInDirectorySentence)
    expect(refusalWords(`(${code})`)).toBe(NotInDirectorySentence)
  })

  it.each(['AADSTS500200', 'AADSTS500341', 'AADSTS900720', 'AADSTS150020', 'XAADSTS50034', 'AADSTS50177', 'AADSTS51004'])(
    'leaves %s, which is not a confirmed code, as it was said',
    (code) => {
      const text = `Microsoft refused the sign-in: ${code}: words.`
      expect(refusalWords(text)).toBe(text)
    },
  )

  it.each(['AADSTS65001', 'AADSTS90094', 'AADSTS90095', 'AADSTS900941', 'AADSTS90099', 'admin_consent_required'])(
    'says the admin sentence for %s, even beside a not-in-the-directory code',
    (admin) => {
      expect(refusalWords(admin)).toBe(AdminApprovalSentence)
      expect(refusalWords(`${admin} AADSTS50020`)).toBe(AdminApprovalSentence)
    },
  )

  it('leaves null and other refusals alone', () => {
    expect(refusalWords(null)).toBeNull()
    expect(refusalWords('The sign-in was declined at Microsoft.')).toBe('The sign-in was declined at Microsoft.')
  })
})

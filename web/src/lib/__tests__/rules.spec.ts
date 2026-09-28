import { describe, expect, it } from 'vitest'
import type { ValidationRule } from 'quasar'
import {
  agentNameTaken,
  backlogTitle,
  email,
  firstProblem,
  httpUrl,
  MAXIMUM_BACKLOG_TITLE_LENGTH,
  optional,
  repoUrlList,
  repoUrlListProblem,
  teamNameTaken,
  envLines,
  envName,
  envNameRules,
  envValue,
  identifier,
  memberName,
  parseEnvLines,
  password,
  positiveInt,
  repoFolderName,
  repoUrl,
  repoUrlRules,
  localRepoName,
  required,
  skillName,
  teamLabel,
  timezone,
  timezoneOptions,
  uniqueIn,
  usageFormat,
  usageFormatOptions,
  type Rule,
} from '../rules'

const all = (rules: Rule[], value: string) =>
  rules.map((rule) => rule(value)).find((result) => result !== true) ?? true

describe('rules', () => {
  it('are assignable to Quasar ValidationRule', () => {
    const rules: ValidationRule[] = [teamLabel, identifier, ...repoUrlRules([], 0)]
    expect(rules).toHaveLength(4)
  })

  describe('required', () => {
    it('accepts text and refuses empty or blank', () => {
      expect(required()('x')).toBe(true)
      expect(required()('')).toBe('This is required.')
      expect(required('Pick a member.')('   ')).toBe('Pick a member.')
      expect(required()(null)).toBe('This is required.')
    })
  })

  describe('teamLabel and memberName', () => {
    it('accept 1 to 60 characters after trimming', () => {
      expect(teamLabel('Data Ingest')).toBe(true)
      expect(teamLabel(`  ${'a'.repeat(60)}  `)).toBe(true)
      expect(memberName('A')).toBe(true)
    })

    it('refuse empty and over-long names', () => {
      expect(teamLabel('  ')).toBe('A team needs a name.')
      expect(teamLabel('a'.repeat(61))).toBe('A team name cannot be longer than 60 characters.')
      expect(memberName('')).toBe('A member needs a name.')
      expect(memberName('a'.repeat(61))).toBe('A member name cannot be longer than 60 characters.')
    })
  })

  describe('identifier', () => {
    it('accepts letters, digits, - and _ up to 32 characters', () => {
      expect(identifier('claude')).toBe(true)
      expect(identifier('9lives_x-y')).toBe(true)
      expect(identifier('a'.repeat(32))).toBe(true)
    })

    it('refuses bad starts, bad characters and over-long names', () => {
      expect(identifier('')).toBe('A name is required.')
      expect(identifier('-lead')).toBe('Names start with a letter or digit.')
      expect(identifier('has space')).toBe('Names use letters, digits, - and _, up to 32 characters.')
      expect(identifier('a'.repeat(33))).toBe('Names use letters, digits, - and _, up to 32 characters.')
    })
  })

  describe('skillName', () => {
    it('accepts lowercase kebab-case up to 48 characters', () => {
      expect(skillName('worktrees')).toBe(true)
      expect(skillName('test-credentials')).toBe(true)
      expect(skillName('9' + 'a'.repeat(47))).toBe(true)
    })

    it('refuses what the skill store refuses, including names identifier accepts', () => {
      expect(skillName('')).toBe('A skill needs a name.')
      expect(skillName('Worktrees')).not.toBe(true)
      expect(skillName('test_credentials')).not.toBe(true)
      expect(skillName('-lead')).not.toBe(true)
      expect(skillName('a'.repeat(49))).not.toBe(true)
      expect(identifier('test_credentials')).toBe(true)
    })
  })

  describe('repoUrl', () => {
    it('accepts absolute http and https URLs', () => {
      expect(repoUrl('https://github.com/owner/repo.git')).toBe(true)
      expect(repoUrl(' http://git.local/team/Widget-core ')).toBe(true)
    })

    it('derives the folder name as the server does', () => {
      expect(repoFolderName('https://github.com/owner/Repo.GIT')).toBe('Repo')
      expect(repoFolderName('https://host/a/my%20repo')).toBe('my repo')
      expect(repoFolderName('git@github.com:owner/repo.git')).toBeNull()
    })

    it('refuses a non-http URL', () => {
      expect(repoUrl('git@github.com:owner/repo.git')).toMatch(/^Use an absolute http or https URL/)
      expect(repoUrl('ssh://github.com/owner/repo.git')).toMatch(/^Use an absolute http or https URL/)
      expect(repoUrl('github.com/owner/repo')).toMatch(/^Use an absolute http or https URL/)
      expect(repoUrl('')).toBe('Enter a repository URL, or remove this row.')
    })

    it('takes local:<name> for a local repository, and refuses an illegal name naming it', () => {
      expect(repoUrl('local:widget')).toBe(true)
      expect(repoUrl(' local:Widget-core.v2 ')).toBe(true)
      expect(repoFolderName('local:widget')).toBe('widget')
      expect(repoUrl('local:../keys')).toMatch(/^'local:\.\.\/keys' is not a local repository name/)
      expect(repoUrl('local:')).toMatch(/is not a local repository name/)
      expect(repoUrl('local:a b')).toMatch(/is not a local repository name/)
      expect(repoUrl('local:widget.git')).toMatch(/is not a local repository name/)
      expect(repoUrlRules(['https://github.com/owner/widget.git', 'local:Widget'], 1)
        .map((rule) => rule('local:Widget'))).toContain("Another URL in this list already clones into 'Widget'.")
    })

    it('checks a local repository name as the Host does', () => {
      expect(localRepoName('widget')).toBe(true)
      expect(localRepoName('-x')).toMatch(/^Use 1 to 100 letters/)
      expect(localRepoName('a..b')).toMatch(/^Use 1 to 100 letters/)
      expect(localRepoName('x.lock')).toMatch(/^Use 1 to 100 letters/)
      expect(localRepoName('')).toBe('A local repository needs a name.')
    })

    it('refuses a URL that derives no legal folder name', () => {
      expect(repoUrl('https://github.com/')).toBe('The URL must end in the repository name.')
      expect(repoUrl('https://github.com/owner/.git')).toBe('The URL must end in the repository name.')
      expect(repoUrl('https://host/owner/a%2Fb')).toMatch(/cannot be a folder name/)
      expect(repoUrl('https://host/owner/a%00b.git')).toBe(
        "This URL would clone into 'a\u0000b', which cannot be a folder name.")
      expect(repoUrl('https://host/owner/.GIT.git')).toMatch(/cannot be a folder name/)
    })

    // `RepoUrls.IsLegalFolderName` keeps only Linux and git rules: the host runs only on Linux.
    it('accepts a folder name only Windows would refuse', () => {
      expect(repoUrl('https://host/owner/CON.git')).toBe(true)
      expect(repoUrl('https://host/owner/.hidden')).toBe(true)
      expect(repoUrl('https://host/owner/a:b.git')).toBe(true)
      expect(repoUrl('https://host/owner/name.')).toBe(true)
      expect(repoUrl('https://host/owner/name%20')).toBe(true)
      expect(repoUrl('https://host/owner/back%5Cslash.git')).toBe(true)
    })

    it('refuses a duplicate repo URL, and two URLs cloning into one folder', () => {
      const urls = ['https://github.com/a/repo.git', 'https://github.com/a/repo.git']
      expect(all(repoUrlRules(urls, 1), urls[1]!)).toBe(
        "Another URL in this list already clones into 'repo'.")

      const clash = ['https://github.com/a/Repo', 'https://gitlab.com/b/repo.git']
      expect(all(repoUrlRules(clash, 0), clash[0]!)).toBe(
        "Another URL in this list already clones into 'Repo'.")
    })

    it('accepts distinct URLs in a list', () => {
      const urls = ['https://github.com/a/one.git', 'https://github.com/a/two.git']
      expect(all(repoUrlRules(urls, 0), urls[0]!)).toBe(true)
      expect(all(repoUrlRules(urls, 1), urls[1]!)).toBe(true)
    })

    it('does not call two unparseable rows a clash', () => {
      const urls = ['not a url', 'also not']
      expect(repoUrlRules(urls, 0)[1]!(urls[0])).toBe(true)
    })
  })

  describe('uniqueIn', () => {
    it('skips the entry being edited and checks the value passed in', () => {
      const list = ['a', 'b', 'stale']
      expect(uniqueIn(list, 2)('c')).toBe(true)
      expect(uniqueIn(list, 2)('a')).toBe('This appears twice in the list.')
      expect(uniqueIn(list, 0)('a')).toBe(true)
    })

    it('takes a message function and a key', () => {
      const rule = uniqueIn(['Alpha'], 1, (v) => `${v} again.`, (v) => v.toLowerCase())
      expect(rule('ALPHA')).toBe('ALPHA again.')
    })
  })

  describe('envName', () => {
    it('accepts names TeamEnv accepts', () => {
      expect(envName('API_KEY')).toBe(true)
      expect(envName('_private')).toBe(true)
      expect(envName('TEST_ADMIN_EMAIL')).toBe(true)
    })

    it('refuses illegal names', () => {
      expect(envName('')).toBe('A variable needs a name.')
      expect(envName('1ABC')).toBe('Names use letters, digits and _, starting with a letter or _.')
      expect(envName('MY-KEY')).toBe('Names use letters, digits and _, starting with a letter or _.')
    })

    it('refuses the HARNESS_ prefix with a sentence, in any case', () => {
      const sentence = "Names beginning HARNESS_ are the platform's own and cannot be set by a team."
      expect(envName('HARNESS_TOKEN')).toBe(sentence)
      expect(envName('harness_token')).toBe(sentence)
      expect(envName('HARNESS')).toBe(true)
    })

    it('refuses a name set twice in a list', () => {
      const names = ['FOO', 'BAR', 'FOO']
      expect(all(envNameRules(names, 2), 'FOO')).toBe("'FOO' is set twice.")
      expect(all(envNameRules(names, 1), 'BAR')).toBe(true)
      expect(all(envNameRules(names, 1), 'foo')).toBe(true)
    })
  })

  describe('parseEnvLines', () => {
    it('parses KEY=value lines, skipping blanks', () => {
      const parsed = parseEnvLines('FOO=one\n\n  BAR = a=b  \r\nEMPTY=')
      expect(parsed.valid).toBe(true)
      expect(parsed.entries).toEqual({ FOO: 'one', BAR: ' a=b', EMPTY: '' })
      expect(parsed.rows.map((row) => row.line)).toEqual([1, 3, 4])
      expect(envLines('FOO=one')).toBe(true)
      expect(envLines('')).toBe(true)
    })

    it('returns an error per row', () => {
      const parsed = parseEnvLines('FOO=1\nnoequals\n=value\nHARNESS_X=1\nFOO=2\nOK=fine')
      expect(parsed.valid).toBe(false)
      expect(parsed.rows.map((row) => row.error)).toEqual([
        null,
        'Line 2 needs the form NAME=value.',
        'Line 3 needs the form NAME=value.',
        "Line 4: Names beginning HARNESS_ are the platform's own and cannot be set by a team.",
        "Line 5: 'FOO' is set twice.",
        null,
      ])
      expect(parsed.entries).toEqual({ FOO: '1', OK: 'fine' })
      expect(envLines('FOO=1\nnoequals')).toBe('Line 2 needs the form NAME=value.')
    })

    it('refuses an over-long value and too many entries', () => {
      expect(parseEnvLines(`BIG=${'x'.repeat(4097)}`).rows[0]!.error).toBe(
        'Line 1: the value is longer than 4096 characters.')
      const many = Array.from({ length: 65 }, (_, i) => `K${i}=v`).join('\n')
      const parsed = parseEnvLines(many)
      expect(parsed.error).toBe('A team may hold at most 64 variables; this has 65.')
      expect(parsed.valid).toBe(false)
      expect(envLines(many)).toBe(parsed.error)
    })
  })

  describe('email', () => {
    it('accepts a trimmed address with @', () => {
      expect(email(' someone@example.com ')).toBe(true)
    })

    it('refuses empty and @-less values', () => {
      expect(email('  ')).toBe('An email address is required.')
      expect(email('someone.example.com')).toBe('That is not an email address.')
    })
  })

  describe('password', () => {
    it('accepts 8 or more characters and refuses fewer', () => {
      expect(password('12345678')).toBe(true)
      expect(password('1234567')).toBe('A password needs at least 8 characters.')
      expect(password('')).toBe('A password needs at least 8 characters.')
    })
  })

  describe('usageFormat', () => {
    it('accepts each legal value and empty', () => {
      for (const option of usageFormatOptions) expect(usageFormat(option.value)).toBe(true)
      expect(usageFormatOptions.map((option) => option.value)).toEqual(
        ['', 'claude-json', 'codex-total', 'copilot-usage-file', 'grok-json', 'antigravity-json'])
    })

    it('refuses anything else', () => {
      expect(usageFormat('codex-json')).toMatch(/^Usage format is one of claude-json, /)
    })
  })

  describe('timezone', () => {
    it('accepts known IANA names, UTC and runtime aliases', () => {
      expect(timezoneOptions()[0]).toBe('UTC')
      expect(timezoneOptions()).toContain('Europe/London')
      expect(timezone('UTC')).toBe(true)
      expect(timezone('Europe/London')).toBe(true)
      expect(timezone('America/New_York')).toBe(true)
    })

    it('refuses empty, unknown and offset values', () => {
      expect(timezone('')).toBe('A timezone is required.')
      expect(timezone('Mars/Olympus')).toBe('Use a timezone name such as UTC or Europe/London.')
      expect(timezone('+05:00')).toBe('Use a timezone name such as UTC or Europe/London.')
    })
  })

  describe('positiveInt', () => {
    it('accepts whole numbers from 1, and empty', () => {
      expect(positiveInt('1')).toBe(true)
      expect(positiveInt(900)).toBe(true)
      expect(positiveInt('')).toBe(true)
    })

    it('refuses zero, negatives, fractions and text', () => {
      for (const bad of ['0', '-3', '1.5', 'ten', '1e3']) {
        expect(positiveInt(bad)).toBe('Use a whole number, 1 or more.')
      }
      expect(all([required(), positiveInt], '')).toBe('This is required.')
    })
  })
})

describe('rules added for the dialogs', () => {
  describe('backlogTitle', () => {
    it('accepts a title and refuses blank or overlong', () => {
      expect(backlogTitle('Form validation')).toBe(true)
      expect(backlogTitle('   ')).toBe('An item needs a title.')
      expect(backlogTitle('x'.repeat(MAXIMUM_BACKLOG_TITLE_LENGTH))).toBe(true)
      expect(backlogTitle('x'.repeat(MAXIMUM_BACKLOG_TITLE_LENGTH + 1)))
        .toBe(`A title cannot be longer than ${MAXIMUM_BACKLOG_TITLE_LENGTH} characters.`)
    })
  })

  describe('httpUrl', () => {
    it('accepts absolute http and https URLs only', () => {
      expect(httpUrl('https://docs.example.com/install')).toBe(true)
      expect(httpUrl('http://example.com')).toBe(true)
      expect(httpUrl('')).toBe('Enter a URL.')
      for (const bad of ['example.com', 'ftp://example.com/x', 'https://', 'javascript:alert(1)']) {
        expect(httpUrl(bad)).toBe('Use an absolute http or https URL, such as https://example.com/install.')
      }
    })
  })

  describe('optional', () => {
    it('passes empty and otherwise defers to the rule', () => {
      const [rule] = optional(httpUrl)
      expect(rule!('')).toBe(true)
      expect(rule!('  ')).toBe(true)
      expect(rule!('nope')).not.toBe(true)
      expect(rule!('https://example.com')).toBe(true)
    })
  })

  describe('firstProblem', () => {
    it('returns the first failing sentence, or null', () => {
      expect(firstProblem([required(), identifier], 'ok')).toBeNull()
      expect(firstProblem([required(), identifier], '')).toBe('This is required.')
      expect(firstProblem([identifier], 'bad name')).toMatch(/^Names use/)
    })
  })

  describe('repoUrlListProblem', () => {
    it('names the first bad or duplicate URL in the list', () => {
      expect(repoUrlListProblem([])).toBeNull()
      expect(repoUrlListProblem(['https://github.com/o/a.git', 'https://github.com/o/b'])).toBeNull()
      expect(repoUrlListProblem(['git@github.com:o/a.git'])).toMatch(/^git@github.com:o\/a.git: Use an absolute/)
      expect(repoUrlListProblem(['https://github.com/o/a.git', 'https://gitlab.com/p/A']))
        .toContain("already clones into 'a'")
    })
  })
})

describe('refusals about a name', () => {
  it('recognises the server sentences for a taken team or Agent name', () => {
    expect(teamNameTaken("A team called 'Alpha' already exists.")).toBe(true)
    expect(teamNameTaken('Repository setup failed.')).toBe(false)
    expect(agentNameTaken('Two Agents share a name. Names are compared without regard to case.')).toBe(true)
    expect(agentNameTaken("'x' has no launch, so nothing could ever launch it.")).toBe(false)
  })
})

describe('envValue', () => {
  it('accepts up to the server limit', () => {
    expect(envValue('')).toBe(true)
    expect(envValue('x'.repeat(4096))).toBe(true)
    expect(envValue('x'.repeat(4097))).toBe('A value cannot be longer than 4096 characters.')
  })
})

describe('repoUrlList', () => {
  it('passes an empty or valid list and names the first bad entry', () => {
    expect(repoUrlList([])).toBe(true)
    expect(repoUrlList(null)).toBe(true)
    expect(repoUrlList(['https://github.com/o/a.git'])).toBe(true)
    expect(repoUrlList(['https://github.com/o/a.git', 'https://github.com/p/a'])).toContain("already clones into 'a'")
  })
})

import { productCli } from '../presentation/product'

/**
 * WHETHER A NEWER RELEASE IS OUT, as `GET /api/version/updates` answers it, and the words the
 * version chip and the Updates dialog say about it. Nothing here decides anything: `checked` false
 * is "not known" and is never shown as up to date, and the update itself is a command the person
 * runs on the machine, because the Host never restarts its own container.
 */
export interface PublishedRelease {
  version: string
  prerelease: boolean
  publishedAt: string | null
  url: string
  /** The release's own text. Shown as text, never as markup. */
  notes: string
}

export interface UpdateStatus {
  current: string
  enabled: boolean
  checked: boolean
  checkedAt: string | null
  /** Why nothing is known, in a sentence; null once a read answered. */
  detail: string | null
  latest: string | null
  updateAvailable: boolean
  latestIsPrerelease: boolean | null
  newer: PublishedRelease[]
}

/** The command that moves the CLI and the instance to the newest release this build is offered. */
export function updateCommand(status: Pick<UpdateStatus, 'latestIsPrerelease'>): string {
  return `${productCli} update${status.latestIsPrerelease ? ' --prerelease' : ''}`
}

/** A date in the viewer's locale, or '' when there is none. */
export function releaseDay(at: string | null): string {
  if (!at) return ''
  const when = new Date(at)
  return Number.isNaN(when.getTime()) ? '' : when.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

/** The dialog's first line. */
export function updateHeadline(status: UpdateStatus): string {
  if (!status.enabled) return status.detail ?? 'Checking for updates is turned off.'
  if (!status.checked) return status.detail ?? 'Not checked yet.'
  if (!status.updateAvailable) return `You are on v${status.current}, the newest release offered to this build.`

  const kind = status.latestIsPrerelease ? ' (pre-release)' : ''
  const count = status.newer.length > 1 ? ` ${status.newer.length} releases are newer than yours.` : ''
  return `v${status.latest}${kind} is out. You are on v${status.current}.${count}`
}

/** `Checked at 3:04 PM on Oct 6`, or '' when it never was. */
export function checkedLine(status: Pick<UpdateStatus, 'checkedAt'>): string {
  if (!status.checkedAt) return ''
  const at = new Date(status.checkedAt)
  if (Number.isNaN(at.getTime())) return ''
  return `Checked at ${at.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })} on ${releaseDay(status.checkedAt)}.`
}

/** What a person should know about runs in progress before updating, or '' with none. */
export function runningWarning(running: number): string {
  if (running <= 0) return ''
  const runs = running === 1 ? '1 agent run is' : `${running} agent runs are`
  return `${runs} in progress now. Updating restarts the instance and stops them, so update when nothing is running.`
}

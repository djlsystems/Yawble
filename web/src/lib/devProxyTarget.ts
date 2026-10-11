/**
 * WHICH HOST `npm run dev` PROXIES TO, AND WHERE THAT ANSWER CAME FROM.
 *
 * The dev server needs a proxy target. `HARNESS_URL` is already minted per container by the
 * platform and already names the exact host that container belongs to, so it is the answer - not a
 * new `HARNESS_PORT`, which would be a second spelling of something `HARNESS_URL` already
 * says, and the two would be free to disagree.
 *
 * THIS LIVES IN `lib/` RATHER THAN IN `quasar.config.ts` SO THAT IT CAN BE TESTED. The config file
 * is loaded by the Quasar CLI and cannot be driven from a spec; a pure function over an environment
 * dictionary can. The config keeps the side effects - reading `process.env`, printing, throwing -
 * and this keeps the decision.
 *
 * THE TREE-TO-PORT TABLE IS NOT DUPLICATED HERE, deliberately. `start.bat` owns it. A copy in
 * TypeScript is two stores of one fact, which is the shape this codebase has paid for repeatedly.
 * The environment variable is the seam between them.
 */

/** Where the proxy target came from. Printed, so a member can see what decided its instance. */
export type DevProxySource = 'HARNESS_URL' | 'HARNESS_PORT' | 'fallback'

export interface DevProxyTarget {
  /** The origin to proxy `/api` and `/hub` to. */
  readonly host: string
  readonly source: DevProxySource
}

/**
 * A container whose environment answers neither question.
 *
 * Thrown rather than returned, because the only correct response is for the dev server not to
 * start: the fallback's value is ANOTHER INSTANCE'S LIVE HOST, and a member silently driving
 * somebody else's data and reporting on it as its own is worse than a process that refuses.
 */
export class DevProxyRefusal extends Error {}

/** The fallback target, which is `core`'s port. Only ever reached outside a container. */
export const FallbackHost = 'http://127.0.0.1:8090'

/**
 * Resolve the proxy target from an environment.
 *
 * `HARNESS_URL` first - the platform's own answer, and the only one that is right for a member
 * of any instance. `HARNESS_PORT` second - the operator's manual override, which the printed
 * hint has always told them to set and which must keep working. Then the fallback, which an
 * operator running `npm run dev` by hand in `core\web` is entitled to and a container is not.
 */
export function resolveDevProxyTarget(env: Record<string, string | undefined>): DevProxyTarget {
  const url = (env.HARNESS_URL ?? '').trim()
  if (url.length > 0) {
    // Trailing slashes are stripped so the printed line and the proxy target agree with what the
    // platform minted, rather than differing by a character nobody can see.
    return { host: url.replace(/\/+$/, ''), source: 'HARNESS_URL' }
  }

  const port = (env.HARNESS_PORT ?? '').trim()
  if (port.length > 0) {
    return { host: `http://127.0.0.1:${port}`, source: 'HARNESS_PORT' }
  }

  // `HARNESS_MEMBER` is the platform telling this process it is a container. A container
  // reaching the fallback means its environment is broken, and failing closed is the recoverable
  // direction here - the alternative is a wrong answer nothing reports.
  if ((env.HARNESS_MEMBER ?? '').trim().length > 0) {
    throw new DevProxyRefusal(
      'This is a container (HARNESS_MEMBER is set) and neither HARNESS_URL nor '
      + 'HARNESS_PORT names a host. Refusing to start rather than proxying to '
      + `${FallbackHost}, which is another instance's live host.`,
    )
  }

  return { host: FallbackHost, source: 'fallback' }
}

/**
 * The line the dev server prints. It names the target AND ITS SOURCE.
 *
 * The provenance is the whole diagnostic: the previous line named the target alone, which made a
 * wrong instance indistinguishable from a right one. A member reading its own output can now see
 * which instance it is about to drive and whether anything told it that.
 */
export function describeDevProxyTarget(target: DevProxyTarget): string {
  const provenance = target.source === 'fallback'
    ? 'no HARNESS_URL or HARNESS_PORT set, so this is the built-in fallback'
    : `from ${target.source}`
  return `  proxying /api and /hub to ${target.host}  (${provenance})`
}

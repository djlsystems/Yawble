import { json, send } from './client'

/**
 * EVERY CALL TO THE SITES SURFACE: a team's published sites, their kept versions and their data.
 *
 * A SEPARATE FILE FROM `client.ts`, like `documents.ts` and `kanban.ts`, borrowing `send`/`json` so a
 * 401 is still `Unauthorized` and a refusal still surfaces the server's own `error` sentence.
 *
 * | operation | route |
 * |---|---|
 * | every site | `GET /api/sites` |
 * | one team's sites | `GET /api/teams/{team}/sites` |
 * | one site, its versions and collections | `GET /api/teams/{team}/sites/{site}` |
 * | roll back | `POST /api/teams/{team}/sites/{site}/rollback` |
 * | unpublish | `POST /api/teams/{team}/sites/{site}/unpublish` |
 * | delete | `DELETE /api/teams/{team}/sites/{site}` |
 * | a collection's documents | `GET /api/teams/{team}/sites/{site}/data/{collection}` |
 */

/** One site as every route returns it. `liveVersion` null is a site that is not published. */
export interface Site {
  team: string
  name: string
  liveVersion: number | null
  publishedAt: string | null
  publishedBy: string | null
  createdAt: string
  createdBy: string
  documents: number
  dataBytes: number
  /** `/sites/<team>/<site>/`, same origin. The Host issues the capability itself. */
  url: string
}

/** One kept version, newest first in `SiteDetail.versions`. */
export interface SiteVersion {
  version: number
  live: boolean
  publishedAt: string
  publishedBy: string
  source: string
  files: number
  bytes: number
}

export interface SiteDetail {
  site: Site
  versions: SiteVersion[]
  collections: string[]
  url: string
}

/** One document in a site's collection. `doc` is whatever JSON the site stored. */
export interface SiteDocument {
  collection: string
  id: string
  doc: unknown
  updatedAt: string
  updatedBy: string
}

/**
 * The first DELETE, which the Host answers 409 with a sentence saying what would be lost. The
 * sentence is this error's message; sending again with `confirm` deletes.
 */
export class SiteDeletionConfirmationRequired extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'SiteDeletionConfirmationRequired'
  }
}

const site = (team: string, name: string, suffix = '') =>
  `/api/teams/${encodeURIComponent(team)}/sites/${encodeURIComponent(name)}${suffix}`

/** Every site across every team. A person's. */
export const listSites = () => json<Site[]>('/api/sites')

/** One team's sites, as `GET /api/sites` lists them. */
export const listTeamSites = (team: string) =>
  json<Site[]>(`/api/teams/${encodeURIComponent(team)}/sites`)

export const getSite = (team: string, name: string) => json<SiteDetail>(site(team, name))

/** Makes a kept version live again. 409, with a sentence, when it cannot be. */
export const rollbackSite = (team: string, name: string, version: number) =>
  json<Site>(site(team, name, '/rollback'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ version }),
  })

/** Takes the site down. Its files and data are kept. */
export const unpublishSite = (team: string, name: string) =>
  json<Site>(site(team, name, '/unpublish'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({}),
  })

/**
 * Deletes a site, its versions and its data. Without `confirm` the Host refuses with 409 and the
 * sentence the person is shown before they are asked; that refusal is thrown as
 * `SiteDeletionConfirmationRequired`, every other one as it came.
 */
export const deleteSite = async (team: string, name: string, confirm = false): Promise<void> => {
  try {
    await send(site(team, name, confirm ? '?confirm=true' : ''), { method: 'DELETE' })
  } catch (cause) {
    if (!confirm && (cause as { status?: number }).status === 409) {
      throw new SiteDeletionConfirmationRequired((cause as Error).message)
    }

    throw cause
  }
}

export const listSiteDocuments = (team: string, name: string, collection: string) =>
  json<SiteDocument[]>(site(team, name, `/data/${encodeURIComponent(collection)}`))

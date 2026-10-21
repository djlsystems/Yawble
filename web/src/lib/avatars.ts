import { reactive } from 'vue'

/**
 * THE MEMBERS' AVATARS: one sheet of square tiles, `public/avatars/avatars.jpg`, and each agent
 * member shown as one tile of it on the board, the Kanban and the Monitor.
 *
 * The sheet is replaced by dropping in another image of the same tile size: the columns and rows
 * are read from the image's own width and height (`AvatarTilePx`), so a sheet of any number of
 * tiles works with no change here. A tile's picture is cut in by `AvatarInset` of its side on every
 * edge, which hides the gutter between tiles.
 *
 * A member's tile is derived, never stored: a hash of its team's id and its own id, so it is the
 * same on every view, in every browser and after the Host restarts. A sheet with another number of
 * tiles deals the members out again.
 */
export const AvatarSheetUrl = '/avatars/avatars.jpg'

/** The side of one tile in the sheet, in the image's own pixels. */
export const AvatarTilePx = 102.4

/** How far in from each edge of its tile a picture is cut, as a fraction of the tile's side. */
export const AvatarInset = 0.05

export interface AvatarSheet {
  image: HTMLImageElement | null
  cols: number
  rows: number
}

/** The sheet once it has loaded; `cols` 0 until then, and for a sheet that failed to load. */
export const avatarSheet = reactive<AvatarSheet>({ image: null, cols: 0, rows: 0 })

let loading: Promise<void> | null = null

/** Load the sheet once for the page. A sheet that fails to load leaves every member on initials. */
export function loadAvatarSheet(url = AvatarSheetUrl): Promise<void> {
  if (loading) return loading
  loading = new Promise<void>((resolve) => {
    if (typeof Image === 'undefined') return resolve()
    const image = new Image()
    image.onload = () => {
      setAvatarSheet(image, image.naturalWidth, image.naturalHeight)
      resolve()
    }
    image.onerror = () => resolve()
    image.src = url
  })
  return loading
}

/** Take a loaded sheet: its columns and rows from its size. Exposed for tests. */
export function setAvatarSheet(image: HTMLImageElement | null, width: number, height: number): void {
  avatarSheet.image = image
  avatarSheet.cols = Math.max(0, Math.round(width / AvatarTilePx))
  avatarSheet.rows = Math.max(0, Math.round(height / AvatarTilePx))
}

/** FNV-1a over the text: small, fast and spread well enough for dealing out tiles. */
function hash(text: string): number {
  let h = 0x811c9dc5
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i)
    h = Math.imul(h, 0x01000193)
  }
  return h >>> 0
}

/** The tile a member wears among `count` tiles; ids are matched without case, as the Host does. */
export function avatarIndex(team: string, member: string, count: number): number {
  if (count <= 0) return -1
  return hash(`${team.toLowerCase()}/${member.toLowerCase()}`) % count
}

/** A tile's column and row in a sheet of `cols` columns. */
export function tileOf(index: number, cols: number): { col: number; row: number } {
  return { col: index % cols, row: Math.floor(index / cols) }
}

/**
 * The CSS that shows one tile, cut in by the inset, filling an element of any size: the sheet
 * scaled so one tile's picture is the element's width, placed by percentages (a percentage
 * position is the fraction of the room the background has to move in, so no size is measured).
 */
export function tileStyle(index: number, cols: number, rows: number, url = AvatarSheetUrl): Record<string, string> {
  const { col, row } = tileOf(index, cols)
  const tile = 1 / (1 - 2 * AvatarInset)
  const at = (place: number, count: number) => {
    const room = count * tile - 1
    return room > 0 ? ((place + AvatarInset) * tile / room) * 100 : 0
  }
  return {
    backgroundImage: `url("${url}")`,
    backgroundSize: `${cols * tile * 100}% ${rows * tile * 100}%`,
    backgroundPosition: `${at(col, cols)}% ${at(row, rows)}%`,
  }
}

/** The source rectangle of one tile's picture in the sheet's own pixels, for a canvas to draw. */
export function tileSource(index: number, sheet: { cols: number; rows: number; width: number; height: number }): {
  sx: number
  sy: number
  sw: number
  sh: number
} {
  const { col, row } = tileOf(index, sheet.cols)
  const tw = sheet.width / sheet.cols
  const th = sheet.height / sheet.rows
  return { sx: (col + AvatarInset) * tw, sy: (row + AvatarInset) * th, sw: tw * (1 - 2 * AvatarInset), sh: th * (1 - 2 * AvatarInset) }
}

/** The tile a member wears on the sheet as loaded, or -1 while there is none. */
export function memberAvatar(team: string, member: string): number {
  return avatarIndex(team, member, avatarSheet.cols * avatarSheet.rows)
}

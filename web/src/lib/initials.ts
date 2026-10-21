/**
 * A name's initials, as the Monitor's nodes, the Activity tile's lanes and their legends read them:
 * the first letter of the first and last words ("Developer Ines" is DI), one letter for one word,
 * a camel-case name split at its capitals ("DeveloperHelene" is DH), `?` for an empty name.
 */
export function initials(name: string): string {
  const words = name
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .split(/[\s_\-.]+/)
    .filter((word) => word !== '')
  if (words.length === 0) return '?'
  if (words.length === 1) return words[0]!.slice(0, 1).toUpperCase()
  return (words[0]!.slice(0, 1) + words[words.length - 1]!.slice(0, 1)).toUpperCase()
}

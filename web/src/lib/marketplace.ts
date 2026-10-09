import type { MarketplaceCatalog, MarketplacePackage } from '../api/types';

/**
 * THE WORDS OF SOLUTIONS > GET STARTED, kept out of the component so each can be read and tested on
 * its own: what a package is, whether this instance has it, why the catalog was not read, and where
 * a fetched package landed.
 */

/** A package's kind as a person reads it. */
export function kindWords(kind: MarketplacePackage['kind']): string {
  return kind === 'solution' ? 'Solution' : 'Plugin';
}

/** Whether this instance has the package, and when the catalog's version is newer, that too. */
export function installedWords(pkg: MarketplacePackage): string {
  if (!pkg.installed) return 'Not installed';

  const on = pkg.installedOn.length ? ` on ${pkg.installedOn.join(', ')}` : '';
  const version = pkg.installedVersion ? ` ${pkg.installedVersion}` : '';

  return pkg.updateAvailable
    ? `Installed${version}${on} · Update available: ${pkg.version}`
    : `Installed${version}${on}`;
}

/** A catalog the Host has not read, as a sentence that says why. Empty when it was read. */
export function notCheckedWords(catalog: MarketplaceCatalog): string {
  if (catalog.checked) return '';

  const reason = (catalog.reason ?? '').trim().replace(/\.$/, '');

  return reason
    ? `The package catalog has not been checked: ${reason}.`
    : 'The package catalog has not been checked, and the Host did not say why.';
}

/** A catalog the console could not ask for at all, as a sentence. */
export function unreachableWords(message: string): string {
  return `Could not reach the package catalog: ${message.trim().replace(/\.$/, '')}.`;
}

/** A Get the Host refused, as a sentence naming the package. Nothing was installed. */
export function refusedWords(pkg: Pick<MarketplacePackage, 'name'>, message: string): string {
  return `${pkg.name} was not fetched: ${message.trim().replace(/\.$/, '')}. Nothing was installed.`;
}

/**
 * The absolute folder the install routes take, from the documents root (`/data/documents`) and the
 * documents-relative folder the fetch answered (`Marketplace/mail-1.2.0`).
 */
export function fetchedFolder(root: string, folder: string): string {
  return `${root.replace(/\/+$/, '')}/${folder.replace(/^\/+/, '')}`;
}

import { fetchMarketplacePackage } from '../api/client';
import { listDocumentsRoot } from '../api/documents';
import type { MarketplacePackage } from '../api/types';
import { fetchedFolder, refusedWords } from './marketplace';

/** A fetch that landed, with the absolute folder the install opens on; or why not, as a sentence. */
export type FetchAnswer =
  | { ok: true; kind: MarketplacePackage['kind']; folder: string }
  | { ok: false; text: string };

/**
 * GET, AS BROWSE AND INSTALLED BOTH DO IT: the Host downloads and checks the package and unpacks it
 * into the documents (`POST /api/marketplace/{id}/fetch`), and this answers the absolute folder the
 * install wizard or the plugin install dialog opens on. It never installs.
 */
export async function fetchIntoDocuments(pkg: Pick<MarketplacePackage, 'id' | 'name'>): Promise<FetchAnswer> {
  try {
    const fetched = await fetchMarketplacePackage(pkg.id);
    const { root } = await listDocumentsRoot();
    if (!root) {
      return {
        ok: false,
        text: `${pkg.name} was fetched into Documents › ${fetched.folder.replaceAll('/', ' › ')}, but the Host did not say where its documents are, so the install cannot be opened here.`,
      };
    }
    return { ok: true, kind: fetched.kind, folder: fetchedFolder(root, fetched.folder) };
  } catch (cause) {
    return { ok: false, text: refusedWords(pkg, cause instanceof Error ? cause.message : String(cause)) };
  }
}

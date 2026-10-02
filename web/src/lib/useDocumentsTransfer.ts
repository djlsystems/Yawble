import { copyDocuments, moveDocuments, uploadDocument } from '../api/documents';
import type {
  DocumentsChangeAnswer,
  DocumentsChangeResult,
  DocumentsClash,
  DocumentsFolderKey,
  OnClash,
} from '../api/types';
import { clashQueue, type ClashAnswer, type DropTarget } from './documentsExplorer';

/**
 * THE ONE CLIENT PATH FOR A MOVE, A COPY OR AN UPLOAD in the Documents explorer.
 *
 * Paste, Move to…, the Upload button and every drop call `transfer` or `uploadInto` here, and
 * nothing else in the explorer calls `moveDocuments`, `copyDocuments` or `uploadDocument` - a scan
 * in `documents-drag.mount.spec.ts` holds that. So the clash question, the resend with the
 * person's choice and the partial-result banner are one piece of code whichever way the person
 * asked.
 *
 * The dialog creates it once and hands it the two things only the dialog has: the clash dialog
 * (`askClash`) and what to do with the outcome (`settled` - the banner and the refresh).
 */

/** How a transfer or upload ended, for the banner and the refresh. */
export type TransferOutcome =
  | { kind: 'done'; verb: 'move' | 'copy' | 'upload'; results: DocumentsChangeResult[]; to: DropTarget; from: DocumentsFolderKey | null }
  | { kind: 'partial'; verb: 'move' | 'copy' | 'upload'; error: string; results: DocumentsChangeResult[]; to: DropTarget; from: DocumentsFolderKey | null }
  | { kind: 'refused'; verb: 'move' | 'copy' | 'upload'; error: string; to: DropTarget; from: DocumentsFolderKey | null }
  | { kind: 'cancelled'; verb: 'move' | 'copy' | 'upload'; to: DropTarget; from: DocumentsFolderKey | null };

export interface DocumentsTransferDeps {
  /** Asks the person about one clash; null when they cancel. */
  askClash: (clash: DocumentsClash, remaining: number) => Promise<ClashAnswer | null>;

  /** Shows the outcome and refreshes what changed. */
  settled: (outcome: TransferOutcome) => void;
}

export interface DocumentsTransfer {
  transfer: (
    mode: 'move' | 'copy',
    source: DocumentsFolderKey,
    paths: string[],
    to: DropTarget,
  ) => Promise<TransferOutcome>;
  uploadInto: (folder: DocumentsFolderKey, path: string, files: File[]) => Promise<TransferOutcome>;
}

/** A clash that keeps coming back (something keeps appearing) is asked about at most this often. */
const MostRounds = 3;

const sentence = (failure: unknown) => (failure as Error)?.message || String(failure);

export function useDocumentsTransfer(deps: DocumentsTransferDeps): DocumentsTransfer {
  async function transfer(
    mode: 'move' | 'copy',
    source: DocumentsFolderKey,
    paths: string[],
    to: DropTarget,
  ): Promise<TransferOutcome> {
    const send = mode === 'move' ? moveDocuments : copyDocuments;
    const base = { verb: mode, to, from: source } as const;
    const outcome = await (async (): Promise<TransferOutcome> => {
      if (to.folder === null) return { kind: 'refused', ...base, error: "Drop into a team's folder." };

      const choices = new Map<string, OnClash>();

      try {
        for (let round = 0; round < MostRounds; round++) {
          const answer: DocumentsChangeAnswer = await send(source, {
            to: { folder: to.folder, path: to.path },
            items: paths.map((path) => {
              const onClash = choices.get(path);
              return onClash ? { path, onClash } : { path };
            }),
          });

          if (answer.kind === 'ok') return { kind: 'done', ...base, results: answer.results };
          if (answer.kind === 'partial') return { kind: 'partial', ...base, error: answer.error, results: answer.results };

          const chosen = await clashQueue(answer.clashes, deps.askClash);
          if (!chosen) return { kind: 'cancelled', ...base };
          for (const [path, choice] of chosen) choices.set(path, choice);
        }

        return { kind: 'refused', ...base, error: 'The names kept clashing; nothing more was sent. Refresh and try again.' };
      } catch (failure) {
        return { kind: 'refused', ...base, error: sentence(failure) };
      }
    })();

    deps.settled(outcome);
    return outcome;
  }

  /**
   * One upload per file, each asking the server about a clash (`onClash: 'ask'`). The clashes are
   * then put to the person in one queue, as a paste's are, and only those files are sent again
   * with the choice.
   */
  async function uploadInto(folder: DocumentsFolderKey, path: string, files: File[]): Promise<TransferOutcome> {
    const to: DropTarget = { folder, path };
    const base = { verb: 'upload', to, from: null } as const;
    const results: DocumentsChangeResult[] = [];
    const failures: string[] = [];
    const clashed: { file: File; clash: DocumentsClash }[] = [];

    const record = async (file: File, onClash: 'ask' | OnClash) => {
      try {
        const answer = await uploadDocument(folder, file, path, onClash);

        if (answer.kind === 'saved') results.push({ from: file.name, to: answer.entry.path, outcome: 'done' });
        else if (answer.kind === 'skipped') results.push({ from: file.name, to: answer.path, outcome: 'skipped', reason: 'Skipped' });
        else clashed.push({ file, clash: answer.clashes[0] ?? { from: file.name, to: file.name, isFolder: false } });
      } catch (failure) {
        failures.push(`${file.name} (${sentence(failure)})`);
        results.push({ from: file.name, to: file.name, outcome: 'failed', reason: sentence(failure) });
      }
    };

    for (const file of files) await record(file, 'ask');

    if (clashed.length > 0) {
      const asked = clashed.splice(0);
      const chosen = await clashQueue(
        asked.map(({ file, clash }) => ({ ...clash, from: file.name })),
        deps.askClash,
      );

      if (!chosen) {
        const outcome: TransferOutcome = results.some((result) => result.outcome === 'done')
          ? { kind: 'done', ...base, results }
          : { kind: 'cancelled', ...base };
        deps.settled(outcome);
        return outcome;
      }

      for (const { file } of asked) await record(file, chosen.get(file.name) ?? 'skip');

      for (const { file } of clashed) {
        failures.push(`${file.name} (something with that name appeared)`);
        results.push({ from: file.name, to: file.name, outcome: 'failed', reason: 'something with that name appeared' });
      }
    }

    const done = results.filter((result) => result.outcome === 'done').length;
    const outcome: TransferOutcome =
      failures.length === 0
        ? { kind: 'done', ...base, results }
        : {
            kind: 'partial',
            ...base,
            results,
            error: `${done === 0 ? 'Nothing was uploaded.' : `${done} of ${files.length} uploaded.`} Not uploaded: ${failures.join('; ')}.`,
          };

    deps.settled(outcome);
    return outcome;
  }

  return { transfer, uploadInto };
}

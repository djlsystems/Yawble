import type { AgentLaunch } from '../api/types';

export interface AgentLaunchDialogState {
  fileName: string;
  argumentsText: string;
  systemPromptArgumentsText: string;
  instructionsFile: string;
  usageFormat: string;

  /** Whether this preset runs a language model. See `AgentLaunch.languageModel` in `api/types.ts` —
   *  carried here so a round trip through this dialog cannot drop it the way it once did. */
  languageModel: boolean;
}

const parseLines = (text: string): string[] =>
  text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

export function launchFromDialogState(state: AgentLaunchDialogState): AgentLaunch {
  const systemPromptArguments = parseLines(state.systemPromptArgumentsText);
  const trimmedInstructionsFile = state.instructionsFile.trim();
  const trimmedUsageFormat = state.usageFormat.trim();

  return {
    fileName: state.fileName.trim(),
    arguments: parseLines(state.argumentsText),
    systemPromptArguments: systemPromptArguments.length > 0 ? systemPromptArguments : null,
    instructionsFile: trimmedInstructionsFile === '' ? null : trimmedInstructionsFile,
    usageFormat: trimmedUsageFormat === '' ? null : trimmedUsageFormat,
    languageModel: state.languageModel,
  };
}

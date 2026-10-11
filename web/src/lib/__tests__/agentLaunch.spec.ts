import { describe, expect, it } from 'vitest';
import type { AgentLaunch } from '../../api/types';
import { launchFromDialogState } from '../agentLaunch';

describe('launchFromDialogState', () => {
  it('rebuilds every AgentLaunch field, including instructionsFile and usageFormat', () => {
    const launch = launchFromDialogState({
      fileName: '  codex  ',
      argumentsText: ' --sandbox workspace-write \n\n --output-format json ',
      systemPromptArgumentsText: '--append-system-prompt-file\n{systemPromptFile}\n',
      instructionsFile: '  AGENTS.md  ',
      usageFormat: '  codex-json  ',
      languageModel: true,
    });

    const launchShape: Record<keyof AgentLaunch, unknown> = {
      fileName: null,
      arguments: null,
      systemPromptArguments: null,
      instructionsFile: null,
      usageFormat: null,
      languageModel: null,
    };

    const expectedKeys = [
      'fileName',
      'arguments',
      'systemPromptArguments',
      'instructionsFile',
      'usageFormat',
      'languageModel',
    ];

    expect(Object.keys(launch).sort()).toEqual([...expectedKeys].sort());
    expect(Object.keys(launch).sort()).toEqual(Object.keys(launchShape).sort());
    expect(launch.fileName).toBe('codex');
    expect(launch.arguments).toEqual(['--sandbox workspace-write', '--output-format json']);
    expect(launch.systemPromptArguments).toEqual(['--append-system-prompt-file', '{systemPromptFile}']);
    expect(launch.instructionsFile).toBe('AGENTS.md');
    expect(launch.usageFormat).toBe('codex-json');
    expect(launch.languageModel).toBe(true);
  });

  it('normalises optional fields to null when left blank', () => {
    const launch = launchFromDialogState({
      fileName: 'claude',
      argumentsText: '',
      systemPromptArgumentsText: ' \n',
      instructionsFile: '   ',
      usageFormat: '',
      languageModel: true,
    });

    expect(launch.arguments).toEqual([]);
    expect(launch.systemPromptArguments).toBeNull();
    expect(launch.instructionsFile).toBeNull();
    expect(launch.usageFormat).toBeNull();
  });

  /**
   * THE ROUND TRIP A TS INTERFACE CANNOT SEE.
   *
   * `languageModel` was added to the C# `AgentLaunch` record with a constructor default of `true`
   * and the SPA was never told. `AgentEditDialog` rebuilds a fresh launch object on every save, so a
   * field this function does not carry is a field the save DELETES: `PUT /api/agents` sends no key,
   * `System.Text.Json` fills in `true`, and editing ANY preset silently turns a
   * `languageModel: false` program back into a billed, probed, firehose-eligible model. This is the
   * test the class of bug says cannot exist — a TS *interface* cannot catch a dropped field, but a
   * pure function's OUTPUT can be asserted directly.
   */
  it('preserves languageModel: false through a round trip', () => {
    const launch = launchFromDialogState({
      fileName: 'lint-sweep',
      argumentsText: '--run',
      systemPromptArgumentsText: '',
      instructionsFile: '',
      usageFormat: '',
      languageModel: false,
    });

    expect(Object.hasOwn(launch, 'languageModel')).toBe(true);
    expect(launch.languageModel).toBe(false);
  });
});

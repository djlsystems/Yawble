import { describe, expect, it } from 'vitest';
import type { PluginConfigField } from '../../api/types';
import { missingRequired, type PluginSettingsShape } from '../pluginSettings';

const shape = (config: Record<string, PluginConfigField>, secrets: PluginSettingsShape['secrets'] = {}): PluginSettingsShape => ({
  config,
  secrets,
});

describe('missingRequired', () => {
  it('never holds back a required list left empty: the Host defaults a list to []', () => {
    const recipients: PluginConfigField = {
      type: 'list', description: 'Who may be sent to.', required: true, default: [], enum: null, setBy: 'anyone',
    };

    expect(missingRequired(shape({ recipients }), { recipients: [] }, {})).toEqual([]);
  });
});

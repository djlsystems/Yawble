import type { AgentModelList } from '../../shared/api';

/** One option of a model picker: the model, where it comes from (core's `from`, "" = not said) and its hint ("" = none). */
export interface ModelOption { model: string; from: string; hint: string }

/** A picker's options from agents.models: its items with their fromLabel, or (an older core, no items) its models with no hint. */
export function modelOptions(list: AgentModelList | null | undefined): ModelOption[] {
  if (!list) return [];
  const items = Array.isArray(list.items) ? list.items.filter((i) => i && typeof i.model === 'string' && i.model.trim()) : [];
  if (items.length) {
    const seen = new Set<string>();
    return items
      .filter((i) => { if (seen.has(i.model)) return false; seen.add(i.model); return true; })
      .map((i) => ({ model: i.model, from: i.from ?? '', hint: (i.fromLabel ?? '').trim() }));
  }
  return (list.models ?? []).map((m) => ({ model: m, from: '', hint: '' }));
}

/** The option's text: the model, then its small hint ("gpt-6.1-sol · from your Codex settings", "opus · built-in"). */
export function modelOptionText(o: ModelOption): string {
  if (!o.hint) return o.model;
  return o.from === 'builtin' ? `${o.model} · ${o.hint}` : `${o.model} · from ${o.hint}`;
}

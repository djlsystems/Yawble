/**
 * Who asked for a host task, in words (card 381, B066). The core records the requester as an agent key ("team/Member"),
 * "_concierge/<conversationId>" for the Concierge or "_companion/<client>" for a companion client (ConciergeHostTasks).
 * The person sees "Concierge" or the client's name, never the raw key or the conversation id. An agent key is shown as it
 * always was: whole, or just the member's name when `memberOnly` is set (the request card's "<Member> asks to run ...").
 */
export function requesterName(key: string, opts: { memberOnly?: boolean } = {}): string {
  if (key === '_concierge' || key.startsWith('_concierge/')) return 'Concierge';
  if (key === '_companion' || key.startsWith('_companion/')) return key.slice('_companion/'.length).trim() || 'A companion client';
  return opts.memberOnly ? (key.split('/')[1] ?? key) : key;
}

import { shallowRef } from 'vue';

/** Whose code the Code dialog shows. One dialog at a time: opening another member's replaces it. */
export interface CodeTarget { team: string; member: string }

export const codeTarget = shallowRef<CodeTarget | null>(null);

export function openCode(team: string, member: string): void {
  codeTarget.value = { team, member };
}

export function closeCode(): void {
  codeTarget.value = null;
}

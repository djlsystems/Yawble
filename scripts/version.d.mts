// Types for version.mjs, which quasar.config.ts and the web suite import.
export const releaseTag: RegExp;
export const unknownVersion: string;
export interface VersionParts { tag: string | null; commits: number; sha: string; dirty: boolean }
export interface BuildInfo { version: string; commit: string; builtAt: string }
export function formatVersion(parts: VersionParts): string;
export function releaseOf(tag: string): string;
export function parseDescribe(output: string): (VersionParts & { tag: string }) | null;
export function describeCheckout(cwd?: string): { version: string; commit: string };
export function buildInfo(env?: Record<string, string | undefined>, cwd?: string, now?: Date): BuildInfo;

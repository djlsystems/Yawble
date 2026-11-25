// The script library's RPC shapes, word for word from the team's scripts-rpc.md. The core
// owns them (UiRpc, scripts.* and schemas.*); this file only names them for the Scripts place.

/** One problem the check found; stepId null: the document as a whole (settings, requires, variables). */
export interface CheckIssue { stepId: string | null; code: string; message: string }
export interface SchemaReference { schema: string; fieldPath: string; stepId: string }
/** warnings never make a draft (stale-requires: the document's requires did not match its steps; the save stores the derived one). */
export interface Check { ok: boolean; issues: CheckIssue[]; schemaReferences: SchemaReference[]; warnings?: CheckIssue[] }

/** A team member that runs a script (version null: on "latest"). */
export interface ScriptUse { teamId: number; team: string; memberId: number; member: string; version: number | null }

export interface ScriptSummary {
  id: number;
  /** The document's own id (e.g. inbox-sms-alert). */
  key: string;
  name: string;
  description: string;
  latestVersion: number;
  /** The newest version that checked clean, null when none did. */
  runnableVersion: number | null;
  /** The latest version failed its check. */
  draft: boolean;
  updatedAt: string;
  usedBy: ScriptUse[];
}

export interface ScriptVersion {
  id: number; key: string; name: string; description: string; version: number; ok: boolean;
  document: ScriptDocument; check: Check; createdAt: string;
}

export interface ScriptSaveResult { id: number; key: string; version: number; ok: boolean; draft: boolean; check: Check }
export interface ScriptVersionRow { version: number; ok: boolean; createdAt: string }
export interface ExportResult { fileName: string; json: string }
/** scripts.delete (DeveloperNoah's core card): refused with code in_use while a member uses the script. */
export interface ScriptDeleteResult { deleted: true }

export interface SchemaSummary { name: string; description: string; latestVersion: number; builtIn: boolean; updatedAt: string }
export interface SchemaVersion { name: string; description: string; version: number; builtIn: boolean; schema: Record<string, unknown>; createdAt: string }
/** A script step a schema change would break. */
export interface SchemaBreak { scriptId: number; script: string; version: number; stepId: string; fieldPath: string }
export interface SchemaSaveResult { name: string; version: number; breaks: SchemaBreak[] }
/** schemas.usage: each script whose latest version uses the schema, the variables typed with it and the steps that read it. */
export interface SchemaUse { id: number; name: string; version: number; variables: string[]; steps: string[] }
export interface SchemaUsage { scripts: SchemaUse[] }

export interface Step {
  seq: number; stepId: string; type: string; input: unknown; output: unknown; status: string; startedAt: string; endedAt: string | null;
}

export type RunStatus = 'running' | 'succeeded' | 'failed' | 'skipped' | 'interrupted';

export interface Run {
  id: number; teamId: number; team: string; memberId: number | null; member: string | null; scriptId: number;
  /** The script's key. */
  script: string;
  version: number;
  /** trigger:<id>, tell or test. */
  trigger: string;
  status: RunStatus;
  dryRun: boolean; test: boolean; input: unknown; outputs: Record<string, unknown> | null;
  failedStepId: string | null; reason: string | null; startedAt: string; endedAt: string | null;
}

/** scripts.run { id }: the run with its steps and log. */
export interface RunDetail extends Run { steps: Step[]; log: { at: string; line: string }[] }

export interface TestRunParams {
  script: number | string; version?: number; input?: unknown; settings?: Record<string, unknown>;
  bindings?: Record<string, string>; teamId: number; dryRun?: boolean;
}
export interface TestRunResult {
  runId: number; status: RunStatus; failedStepId: string | null; reason: string | null;
  outputs: Record<string, unknown> | null; steps: Step[]; log: string[];
}

// ---- the script document, as far as the Scripts place reads it

export interface ScriptSetting { name: string; type: string; label?: string; default?: unknown }
export interface ScriptRequire { name: string; kind: string; actions?: string[] }
export interface ScriptVariable { name: string; type: string; scope?: string; initial?: unknown }
export interface ScriptStep {
  id: string; type: string;
  next?: string | null; then?: string | null; else?: string | null; body?: string | null; default?: string | null;
  cases?: Record<string, string | null>;
  [k: string]: unknown;
}
export interface ScriptDocument {
  format?: string; id?: string; name?: string; description?: string;
  trigger?: { accepts?: string[] };
  settings?: ScriptSetting[]; requires?: ScriptRequire[]; variables?: ScriptVariable[];
  start?: string; steps?: ScriptStep[];
  [k: string]: unknown;
}

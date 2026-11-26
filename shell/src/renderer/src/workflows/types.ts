// The workflow library's RPC shapes, word for word from the team's workflows-rpc.md. The core
// owns them (UiRpc, workflows.* and schemas.*); this file only names them for the Workflows place.

/** One problem the check found; stepId null: the document as a whole (settings, requires, variables). */
export interface CheckIssue { stepId: string | null; code: string; message: string }
export interface SchemaReference { schema: string; fieldPath: string; stepId: string }
/** warnings never make a draft (stale-requires: the document's requires did not match its steps; the save stores the derived one). */
export interface Check { ok: boolean; issues: CheckIssue[]; schemaReferences: SchemaReference[]; warnings?: CheckIssue[] }

/** A team member that runs a workflow (version null: on "latest"). */
export interface WorkflowUse { teamId: number; team: string; memberId: number; member: string; version: number | null }

export interface WorkflowSummary {
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
  usedBy: WorkflowUse[];
}

export interface WorkflowVersion {
  id: number; key: string; name: string; description: string; version: number; ok: boolean;
  document: WorkflowDocument; check: Check; createdAt: string;
}

export interface WorkflowSaveResult { id: number; key: string; version: number; ok: boolean; draft: boolean; check: Check }
export interface WorkflowVersionRow { version: number; ok: boolean; createdAt: string }
export interface ExportResult { fileName: string; json: string }
/** workflows.delete (DeveloperNoah's core card): refused with code in_use while a member uses the workflow. */
export interface WorkflowDeleteResult { deleted: true }

export interface SchemaSummary { name: string; description: string; latestVersion: number; builtIn: boolean; updatedAt: string }
export interface SchemaVersion { name: string; description: string; version: number; builtIn: boolean; schema: Record<string, unknown>; createdAt: string }
/** A workflow step a schema change would break. */
export interface SchemaBreak { workflowId: number; workflow: string; version: number; stepId: string; fieldPath: string }
export interface SchemaSaveResult { name: string; version: number; breaks: SchemaBreak[] }
/** schemas.usage: each workflow whose latest version uses the schema, the variables typed with it and the steps that read it. */
export interface SchemaUse { id: number; name: string; version: number; variables: string[]; steps: string[] }
export interface SchemaUsage { workflows: SchemaUse[] }

export interface Step {
  seq: number; stepId: string; type: string; input: unknown; output: unknown; status: string; startedAt: string; endedAt: string | null;
}

/** waiting: parked at a tell that waits, until its deliverable is done, fails or is blocked, or the tell's timeout passes. */
export type RunStatus = 'running' | 'waiting' | 'succeeded' | 'failed' | 'skipped' | 'interrupted';

export interface Run {
  id: number; teamId: number; team: string; memberId: number | null; member: string | null; workflowId: number;
  /** The workflow's key. */
  workflow: string;
  version: number;
  /** trigger:<id>, tell or test. */
  trigger: string;
  status: RunStatus;
  dryRun: boolean; test: boolean; input: unknown; outputs: Record<string, unknown> | null;
  failedStepId: string | null; reason: string | null; startedAt: string; endedAt: string | null;
  /** The deliverable a waiting run waits on (status waiting), else null; an older core leaves it out. */
  waitDeliverableId?: number | null;
}

/** workflows.run { id }: the run with its steps and log. */
export interface RunDetail extends Run { steps: Step[]; log: { at: string; line: string }[] }

export interface TestRunParams {
  workflow: number | string; version?: number; input?: unknown; settings?: Record<string, unknown>;
  bindings?: Record<string, string>; teamId: number; dryRun?: boolean;
  /** Made-up replies for tells that wait, by step id: text, or JSON for a tell with a reply schema. A test run never sends a tell. */
  tellReplies?: Record<string, unknown>;
}
export interface TestRunResult {
  runId: number; status: RunStatus; failedStepId: string | null; reason: string | null;
  outputs: Record<string, unknown> | null; steps: Step[]; log: string[];
}

/** What a tell step puts into its variable. data: the reply's JSON, of the step's reply schema (null without one). */
export type TellOutcome = 'sent' | 'done' | 'failed' | 'blocked' | 'timedOut' | 'dryRun';
export interface TellResult { outcome: TellOutcome; deliverable: number; reply: string; data: unknown }

// ---- the workflow document, as far as the Workflows place reads it

export interface WorkflowSetting { name: string; type: string; label?: string; default?: unknown }
export interface WorkflowRequire { name: string; kind: string; actions?: string[] }
export interface WorkflowVariable { name: string; type: string; scope?: string; initial?: unknown }
export interface WorkflowStep {
  id: string; type: string;
  next?: string | null; then?: string | null; else?: string | null; body?: string | null; default?: string | null;
  cases?: Record<string, string | null>;
  [k: string]: unknown;
}
export interface WorkflowDocument {
  format?: string; id?: string; name?: string; description?: string;
  trigger?: { accepts?: string[] };
  settings?: WorkflowSetting[]; requires?: WorkflowRequire[]; variables?: WorkflowVariable[];
  start?: string; steps?: WorkflowStep[];
  [k: string]: unknown;
}

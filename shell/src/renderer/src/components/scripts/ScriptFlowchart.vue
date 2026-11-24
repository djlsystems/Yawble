<script setup lang="ts">
import { Background } from '@vue-flow/background';
import { Controls } from '@vue-flow/controls';
import { Handle, MarkerType, Position, VueFlow, type Connection, type Edge, type EdgeMouseEvent, type Node, type NodeDragEvent, type NodeMouseEvent } from '@vue-flow/core';
import { MiniMap } from '@vue-flow/minimap';
import '@vue-flow/core/dist/style.css';
import '@vue-flow/controls/dist/style.css';
import '@vue-flow/minimap/dist/style.css';
import { computed, ref, watch } from 'vue';
import { confirmDialog } from '../../confirm';
import { jsonProblem, pretty } from '../../scripts/api';
import {
  addStep, branchesOf, clearLayout, connect, createHistory, docToGraph, END_ID, moveNode, NODE_SIZE, removeEdge, removeStep,
  replaceStep, START_ID, type Branch, type FlowEdge, type FlowNode,
} from '../../scripts/flowchart';
import type { CheckIssue, ScriptDocument, ScriptStep } from '../../scripts/types';
import FlowPalette from './FlowPalette.vue';
import FlowStepPanel from './FlowStepPanel.vue';

// The flowchart designer over the script's JSON text (v-model): one node per step plus start and end, edges for
// next / then / else / cases / default / body. Every edit goes through the pure functions in scripts/flowchart.ts
// and writes the whole document back as text, so the JSON view and the flowchart are one document. Node positions
// live in the document's `layout` section; without one, dagre lays it out. Undo and redo cover the texts written
// here (toolbar, Ctrl+Z, Ctrl+Y). Read-only (a Visitor, or a run's view): pan, zoom and select only.
const text = defineModel<string>({ required: true });
const selected = defineModel<string | null>('selected', { default: null });
const props = defineProps<{
  issues?: CheckIssue[];
  readonly?: boolean;
  /** A run's view: the steps it took, and the one it failed at. */
  run?: { taken: string[]; failed: string | null };
}>();
const emit = defineEmits<{ edited: [] }>();

const doc = computed<ScriptDocument | null>(() => (jsonProblem(text.value) ? null : (JSON.parse(text.value) as ScriptDocument)));
const graph = computed(() => (doc.value ? docToGraph(doc.value, { issues: props.issues ?? [], run: props.run }) : { nodes: [], edges: [] }));
const byId = computed(() => new Map(graph.value.nodes.map((n) => [n.id, n])));
/** The connection picked on the canvas (Delete removes it). */
const selectedEdge = ref<string | null>(null);

const nodes = computed<Node<FlowNode>[]>(() => graph.value.nodes.map((n) => ({
  id: n.id, type: 'step', position: n.position, data: n, deletable: false,
  draggable: !props.readonly, connectable: !props.readonly,
  width: NODE_SIZE.width, height: NODE_SIZE.height,
})));
const edges = computed<Edge<FlowEdge>[]>(() => graph.value.edges.map((e) => ({
  id: e.id, source: e.source, target: e.target, label: e.label ?? undefined, type: 'smoothstep', data: e, deletable: false,
  markerEnd: MarkerType.ArrowClosed, class: [e.dashed ? 'dashed' : '', selectedEdge.value === e.id ? 'picked' : ''].filter(Boolean).join(' '),
})));

// ---- undo and redo
const history = createHistory(text.value);
const tick = ref(0);
let written = text.value;
const canUndo = computed(() => tick.value >= 0 && history.canUndo);
const canRedo = computed(() => tick.value >= 0 && history.canRedo);
// The text changed elsewhere (the JSON view, a version loaded): it is a step of its own in the history.
watch(text, (t) => { if (t !== written) { written = t; history.push(t); tick.value++; } });

function write(next: ScriptDocument) {
  const t = pretty(next);
  if (t === text.value) return;
  written = t;
  text.value = t;
  history.push(t);
  tick.value++;
  emit('edited');
}
function step(t: string | null) {
  if (t === null) return;
  written = t;
  text.value = t;
  tick.value++;
  emit('edited');
}
const undo = () => { if (!props.readonly) step(history.undo()); };
const redo = () => { if (!props.readonly) step(history.redo()); };

// ---- selection and the panel
const selectedNode = computed(() => (selected.value ? byId.value.get(selected.value) ?? null : null));
const selectedStep = computed<ScriptStep | null>(() => doc.value?.steps?.find((s) => s.id === selected.value) ?? null);

function onNodeClick(e: NodeMouseEvent) { selected.value = e.node.id; selectedEdge.value = null; }
function onEdgeClick(e: EdgeMouseEvent) { selectedEdge.value = e.edge.id; selected.value = null; }
function onPaneClick() { selectedEdge.value = null; }

function onPanelChange(s: ScriptStep) {
  if (!doc.value || props.readonly || !selected.value) return;
  const from = selected.value;
  write(replaceStep(doc.value, from, s));
  if (s.id !== from) selected.value = s.id;
}

// ---- edits
function onDragStop(e: NodeDragEvent) {
  if (!doc.value || props.readonly) return;
  let next = doc.value;
  for (const n of e.nodes?.length ? e.nodes : [e.node]) next = moveNode(next, n.id, { x: Math.round(n.position.x), y: Math.round(n.position.y) });
  write(next);
}
function autoLayout() { if (doc.value && !props.readonly) write(clearLayout(doc.value)); }

function onAdd(type: string) {
  if (!doc.value || props.readonly) return;
  const r = addStep(doc.value, type);
  write(r.doc);
  selected.value = r.id;
  selectedEdge.value = null;
}

/** A connection that waits for its branch (from a step with more than one). */
const pending = ref<{ source: string; target: string; branches: Branch[] } | null>(null);
function onConnect(c: Connection) {
  if (!doc.value || props.readonly || !c.source || !c.target || c.target === START_ID || c.source === END_ID || c.source === c.target) return;
  const branches = branchesOf(doc.value, c.source);
  if (branches.length === 0) return;
  if (branches.length === 1) { write(connect(doc.value, c.source, c.target, branches[0])); return; }
  pending.value = { source: c.source, target: c.target, branches };
}
function pickBranch(b: Branch) {
  const p = pending.value;
  pending.value = null;
  if (p && doc.value) write(connect(doc.value, p.source, p.target, b));
}
const branchKey = (b: Branch) => (b.kind === 'case' ? `case:${b.value}` : b.kind);
function branchLabel(b: Branch, source: string): string {
  const type = doc.value?.steps?.find((s) => s.id === source)?.type;
  switch (b.kind) {
    case 'then': return type === 'forEach' ? 'then (when done)' : 'then (yes)';
    case 'else': return 'else (no)';
    case 'case': return `case ${b.value}`;
    case 'body': return 'body (each)';
    default: return b.kind;
  }
}
const titleOf = (id: string) => (id === START_ID ? 'Start' : id === END_ID ? 'End' : id);

function deleteEdge() {
  const e = graph.value.edges.find((x) => x.id === selectedEdge.value);
  if (!e || !doc.value || props.readonly) return;
  selectedEdge.value = null;
  write(removeEdge(doc.value, e));
}
async function deleteNode() {
  const id = selected.value;
  if (!id || id === START_ID || id === END_ID || !doc.value || props.readonly) return;
  if (!(await confirmDialog({ message: `Delete step ${id}? Steps that go to it will show a problem until you connect them elsewhere.`, okLabel: 'Delete', danger: true }))) return;
  if (!doc.value) return;
  selected.value = null;
  write(removeStep(doc.value, id));
}

function onKey(e: KeyboardEvent) {
  const t = e.target as HTMLElement | null;
  if (t && (t.tagName === 'TEXTAREA' || t.tagName === 'INPUT' || t.isContentEditable)) return;
  const mod = e.ctrlKey || e.metaKey;
  const k = e.key.toLowerCase();
  if (mod && k === 'z' && !e.shiftKey) { e.preventDefault(); undo(); } else if (mod && (k === 'y' || (k === 'z' && e.shiftKey))) { e.preventDefault(); redo(); } else if (e.key === 'Delete' || e.key === 'Backspace') {
    if (props.readonly) return;
    if (selectedEdge.value) { e.preventDefault(); deleteEdge(); } else if (selected.value) { e.preventDefault(); void deleteNode(); }
  } else if (e.key === 'Escape') { pending.value = null; }
}

const shape = (n: FlowNode) => (n.kind === 'if' || n.kind === 'switch' ? 'diamond' : n.kind === 'start' || n.kind === 'end' ? 'round' : 'box');
</script>

<template>
  <div class="flowchart" tabindex="0" data-test="flowchart" :data-readonly="readonly ? 'true' : 'false'" @keydown="onKey">
    <div class="tools row">
      <template v-if="!readonly">
        <button type="button" class="mini" :disabled="!canUndo" data-test="flow-undo" title="Undo (Ctrl+Z)" @click="undo">Undo</button>
        <button type="button" class="mini" :disabled="!canRedo" data-test="flow-redo" title="Redo (Ctrl+Y)" @click="redo">Redo</button>
        <button type="button" class="mini" :disabled="!doc" data-test="flow-auto-layout" title="Forget the saved positions and lay the steps out top to bottom" @click="autoLayout">Auto layout</button>
        <button v-if="selectedEdge" type="button" class="mini" data-test="flow-delete-edge" @click="deleteEdge">Delete connection</button>
        <button v-if="selectedStep" type="button" class="mini" data-test="flow-delete-node" @click="deleteNode">Delete step</button>
      </template>
      <span v-else class="muted small" data-test="flow-readonly">Read only: pan and zoom to look around.</span>
    </div>
    <p v-if="!doc" class="muted" data-test="flow-no-doc">Fix the JSON to see the flowchart.</p>
    <div v-else class="area">
      <FlowPalette v-if="!readonly" @add="onAdd" />
      <div class="canvas" data-test="flow-canvas">
        <VueFlow :nodes="nodes" :edges="edges" :nodes-draggable="!readonly" :nodes-connectable="!readonly" :edges-updatable="false"
          :delete-key-code="null" :min-zoom="0.2" :max-zoom="2" fit-view-on-init
          @node-click="onNodeClick" @edge-click="onEdgeClick" @pane-click="onPaneClick" @node-drag-stop="onDragStop" @connect="onConnect">
          <template #node-step="{ data }">
            <div class="node" :class="[shape(data), { picked: selected === data.id }]" :data-kind="data.kind" :data-run="data.run ?? ''"
              data-test="flow-node" :data-node="data.id" :title="data.problems.join('\n')">
              <Handle v-if="data.kind !== 'start'" type="target" :position="Position.Top" />
              <div class="inner">
                <span class="title"><code>{{ data.title }}</code><span v-if="data.stepType" class="type">{{ data.stepType }}</span></span>
                <span v-if="data.detail" class="detail" data-test="flow-node-detail">{{ data.detail }}</span>
              </div>
              <span v-if="data.problems.length" class="badge" data-test="flow-node-badge" :aria-label="`${data.problems.length} problems`">⚠ {{ data.problems.length }}</span>
              <Handle v-if="data.kind !== 'end'" type="source" :position="Position.Bottom" />
            </div>
          </template>
          <Background :gap="18" />
          <Controls :show-interactive="false" />
          <MiniMap pannable zoomable />
        </VueFlow>
        <div v-if="pending" class="ask" role="dialog" aria-label="Which branch" data-test="flow-branch-ask">
          <p>From <code>{{ titleOf(pending.source) }}</code> to <code>{{ titleOf(pending.target) }}</code>: which branch?</p>
          <div class="row">
            <button v-for="b in pending.branches" :key="branchKey(b)" type="button" class="mini" data-test="flow-branch" :data-branch="branchKey(b)"
              @click="pickBranch(b)">{{ branchLabel(b, pending.source) }}</button>
            <button type="button" class="mini" data-test="flow-branch-cancel" @click="pending = null">Cancel</button>
          </div>
        </div>
      </div>
      <FlowStepPanel v-if="selectedNode" :key="selectedNode.id" :node="selectedNode" :step="selectedStep" :readonly="!!readonly"
        @change="onPanelChange" @remove="deleteNode" @close="selected = null" />
    </div>
  </div>
</template>

<style scoped>
.flowchart { display: flex; flex-direction: column; gap: 6px; min-height: 0; flex: 1 1 0; outline: none; }
.tools { gap: 6px; flex-wrap: wrap; }
.mini { font-size: 12px; padding: 2px 8px; }
.small { font-size: 12px; }
.area { display: flex; gap: 10px; min-height: 460px; flex: 1 1 0; }
.canvas { position: relative; flex: 1 1 0; min-width: 0; min-height: 460px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface-2); overflow: hidden; }

.node { position: relative; width: 220px; height: 72px; box-sizing: border-box; display: flex; align-items: center; justify-content: center;
  padding: 6px 12px; border: 1px solid var(--line-strong); border-radius: 6px; background: var(--pill-neutral-bg); color: var(--pill-neutral-ink); font-size: 12px; }
.node .inner { display: flex; flex-direction: column; align-items: center; gap: 2px; max-width: 100%; min-width: 0; text-align: center; position: relative; }
.node .title { display: inline-flex; gap: 6px; align-items: baseline; max-width: 100%; }
.node .type { font-size: 10.5px; opacity: .8; }
.node .detail { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; }
.node.round { border-radius: 36px; background: var(--pill-green-bg); color: var(--pill-green-ink); border-color: var(--ok-rule); }
.node[data-kind=integration] { background: var(--pill-blue-bg); color: var(--pill-blue-ink); border-color: var(--pill-blue-line); }
.node[data-kind=forEach] { background: var(--pill-violet-bg); color: var(--pill-violet-ink); border-style: dashed; }
.node[data-kind=set] { background: var(--pill-info-bg); color: var(--pill-info-ink); }
.node[data-kind=file] { background: var(--chip-claude-bg); color: var(--chip-claude-ink); border-radius: 2px 14px 2px 2px; }
.node[data-kind=agent] { background: var(--chip-grok-bg); color: var(--chip-grok-ink); border-radius: 14px; }
/* if / switch: a diamond drawn behind the text */
.node.diamond { background: transparent; border: 0; color: var(--pill-yellow-ink); }
.node.diamond::before { content: ''; position: absolute; inset: 0; background: var(--warn-line-strong); clip-path: polygon(50% 0, 100% 50%, 50% 100%, 0 50%); }
.node.diamond::after { content: ''; position: absolute; inset: 1.5px 3px; background: var(--pill-yellow-bg); clip-path: polygon(50% 0, 100% 50%, 50% 100%, 0 50%); }
.node.diamond .inner { z-index: 1; padding: 0 30px; }
.node.picked { box-shadow: 0 0 0 2px var(--focus); }
.node.diamond.picked { box-shadow: none; filter: drop-shadow(0 0 2px var(--focus)); }
.node[data-run=taken] { box-shadow: 0 0 0 3px var(--ok); }
.node.diamond[data-run=taken] { box-shadow: none; filter: drop-shadow(0 0 3px var(--ok)); }
.node[data-run=failed] { box-shadow: 0 0 0 3px var(--bad); }
.node.diamond[data-run=failed] { box-shadow: none; filter: drop-shadow(0 0 3px var(--bad)); }
.badge { position: absolute; top: -9px; right: -6px; z-index: 2; font-size: 10.5px; padding: 0 6px; border-radius: 9px; background: var(--warn-chip); color: var(--warn-ink); border: 1px solid var(--warn-edge); }

.ask { position: absolute; left: 50%; top: 14px; transform: translateX(-50%); z-index: 10; padding: 8px 12px; border: 1px solid var(--line); border-radius: 6px;
  background: var(--surface); color: var(--ink); box-shadow: 0 4px 14px var(--shadow-menu); font-size: 12.5px; }
.ask p { margin: 0 0 6px; }

/* Vue Flow's own parts, in the theme's colours */
.canvas :deep(.vue-flow__edge-path) { stroke: var(--line-strong); stroke-width: 1.5; }
.canvas :deep(.vue-flow__edge.dashed .vue-flow__edge-path) { stroke-dasharray: 6 4; }
.canvas :deep(.vue-flow__edge.picked .vue-flow__edge-path), .canvas :deep(.vue-flow__edge.selected .vue-flow__edge-path) { stroke: var(--accent); stroke-width: 2.5; }
.canvas :deep(.vue-flow__edge-textbg) { fill: var(--surface); }
.canvas :deep(.vue-flow__edge-text) { fill: var(--ink-soft); font-size: 11px; }
.canvas :deep(.vue-flow__arrowhead polyline) { stroke: var(--line-strong); fill: var(--line-strong); }
.canvas :deep(.vue-flow__connection-path) { stroke: var(--accent); }
.canvas :deep(.vue-flow__handle) { width: 8px; height: 8px; background: var(--muted); border: 1px solid var(--surface); }
.canvas :deep(.vue-flow__background pattern circle) { fill: var(--line-strong); }
.canvas :deep(.vue-flow__controls) { box-shadow: 0 0 2px 1px var(--shadow-chip); }
.canvas :deep(.vue-flow__controls-button) { background: var(--surface); border-bottom: 1px solid var(--line-soft); color: var(--ink); fill: var(--ink); }
.canvas :deep(.vue-flow__controls-button:hover) { background: var(--hover); }
.canvas :deep(.vue-flow__controls-button svg) { fill: var(--ink); }
.canvas :deep(.vue-flow__minimap) { background-color: var(--surface); border: 1px solid var(--line); }
.canvas :deep(.vue-flow__minimap-node) { fill: var(--line-strong); stroke: none; }
.canvas :deep(.vue-flow__minimap-mask) { fill: var(--selection); }
.canvas :deep(.vue-flow__node-step) { cursor: grab; }
.flowchart[data-readonly=true] .canvas :deep(.vue-flow__node-step) { cursor: pointer; }
</style>

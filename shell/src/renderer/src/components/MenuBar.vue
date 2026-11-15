<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';

/**
 * The window's menu bar: File, Edit, View, Help, in the page's own title bar (the window draws its title bar itself,
 * so the system's menu bar is not shown). Clicking it never takes the keyboard focus (mousedown is prevented), so
 * Edit → Paste, Copy, Cut and Select all act on the field the person was typing in. Each item names a command the main window carries out; shortcuts are
 * handled by the window too (MenuBar only shows them).
 */
/** radio: one of a group (View > Theme), shown and announced as a radio item; checked marks the chosen one. */
export interface MenuItem { id: string; label: string; keys?: string; checked?: boolean; radio?: boolean; disabled?: boolean; separator?: false }
export interface MenuSeparator { separator: true }
export interface Menu { label: string; items: (MenuItem | MenuSeparator)[] }

const props = defineProps<{ menus: Menu[] }>();
const emit = defineEmits<{ command: [id: string] }>();

const openIndex = ref<number | null>(null);
const root = ref<HTMLElement | null>(null);

function toggle(i: number) { openIndex.value = openIndex.value === i ? null : i; }
function hover(i: number) { if (openIndex.value !== null) openIndex.value = i; }
function run(item: MenuItem) {
  if (item.disabled) return;
  openIndex.value = null;
  emit('command', item.id);
}
function outside(e: MouseEvent) { if (root.value && !root.value.contains(e.target as Node)) openIndex.value = null; }
function key(e: KeyboardEvent) { if (e.key === 'Escape') openIndex.value = null; }
onMounted(() => { document.addEventListener('mousedown', outside); document.addEventListener('keydown', key); });
onBeforeUnmount(() => { document.removeEventListener('mousedown', outside); document.removeEventListener('keydown', key); });
const isSep = (x: MenuItem | MenuSeparator): x is MenuSeparator => x.separator === true;
void props;
</script>

<template>
  <nav ref="root" class="menubar" data-test="menubar">
    <div v-for="(m, i) in menus" :key="m.label" class="menu">
      <button class="top" :class="{ on: openIndex === i }" :data-test="`menu-${m.label.toLowerCase()}`" @mousedown.prevent @click="toggle(i)" @mouseenter="hover(i)">{{ m.label }}</button>
      <div v-if="openIndex === i" class="drop" role="menu">
        <template v-for="(it, j) in m.items" :key="j">
          <div v-if="isSep(it)" class="sep" />
          <button v-else class="item" :role="it.radio ? 'menuitemradio' : 'menuitem'" :aria-checked="it.radio ? !!it.checked : undefined" :disabled="it.disabled" :data-test="`menuitem-${it.id}`" @mousedown.prevent @click="run(it)">
            <span class="check">{{ it.checked ? '✓' : '' }}</span>
            <span class="label">{{ it.label }}</span>
            <span class="keys">{{ it.keys }}</span>
          </button>
        </template>
      </div>
    </div>
  </nav>
</template>

<style scoped>
.menubar { display: flex; align-items: center; gap: 0; -webkit-app-region: no-drag; }
.menu { position: relative; }
.top { border: 0; background: transparent; padding: 4px 9px; font-size: 13px; color: var(--ink); border-radius: 6px; }
.top:hover, .top.on { background: var(--hover); }
.drop { position: absolute; top: calc(100% + 2px); left: 0; min-width: 240px; background: var(--surface); border: 1px solid var(--line); border-radius: 8px;
  box-shadow: 0 8px 24px var(--shadow-menu); padding: 4px; z-index: 1000; }
.item { display: grid; grid-template-columns: 16px 1fr auto; gap: 8px; align-items: center; width: 100%; border: 0; background: transparent;
  padding: 5px 10px 5px 6px; font-size: 13px; text-align: left; border-radius: 5px; color: var(--ink); }
.item:hover:not(:disabled) { background: var(--hover); }
.item:disabled { opacity: .45; }
.check { color: var(--accent); font-size: 12px; }
.keys { color: var(--muted); font-size: 12px; }
.sep { height: 1px; background: var(--line-soft); margin: 4px 6px; }
</style>

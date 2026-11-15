import { computed, ref, watch, type Directive, type VNode } from 'vue';
import type { CoreInfo, RemoteAccess } from '../../shared/api';

// A remote view's access to the instance (B025): "full" (an Owner, or a workgroup Member) or "read" (a workgroup
// Visitor). The core says it in core.getInfo over the link (remoteAccess); a missing or unknown value is "full", so an
// older core keeps working. The desktop window never sets it, so it stays "full" there.
//
// A read-only view shows a "Read-only (Visitor)" chip and disables the easy change buttons (v-change). It is a
// convenience only: the core refuses a reader's change call wherever a button is not marked, and the screens show that
// refusal's text as it is.

export const READ_ONLY_LABEL = 'Read-only (Visitor)';
export const READ_ONLY_TITLE = 'You have read-only access to this computer (Visitor). Ask a workgroup Member or an Owner for more.';
const CHIP_ID = 'remote-access-chip';

export const remoteAccess = ref<RemoteAccess>('full');
export const readOnly = computed(() => remoteAccess.value === 'read');

/** The access a core.getInfo answer gives: "read" only when it says exactly that; anything else is "full". */
export function accessOf(info: Partial<CoreInfo> | null | undefined): RemoteAccess {
  return info?.remoteAccess === 'read' ? 'read' : 'full';
}

/** Sets this view's access: the reactive value, data-remote-access on <html>, and the chip (shown only for "read"). */
export function applyRemoteAccess(access: RemoteAccess, doc: Document = document): void {
  remoteAccess.value = access;
  doc.documentElement.dataset.remoteAccess = access;
  let chip = doc.getElementById(CHIP_ID);
  if (access !== 'read') { chip?.remove(); return; }
  if (!chip) {
    chip = doc.createElement('div');
    chip.id = CHIP_ID;
    chip.className = 'remote-access-chip';
    chip.setAttribute('data-test', 'remote-access-chip');
    chip.setAttribute('role', 'status');
    doc.body.appendChild(chip);
  }
  chip.textContent = READ_ONLY_LABEL;
  chip.title = READ_ONLY_TITLE;
}

/** Asks the core for this view's access and applies it; a failed call keeps what is there (the core still refuses). */
export async function loadRemoteAccess(getInfo: () => Promise<CoreInfo>, doc: Document = document): Promise<RemoteAccess> {
  try {
    const access = accessOf(await getInfo());
    applyRemoteAccess(access, doc);
    return access;
  } catch {
    return remoteAccess.value;
  }
}

// v-change: marks a button that changes something. In a read-only view it is disabled, with the reason as its title.
// The element's own :disabled and :title bindings win again when the view has full access: they are read from the vnode
// on every mount and update, so going from read back to full gives back what the screen says now (an invalid form, a
// busy approval, a title changed while read), not always "enabled" or the title from before read.
const marked = new Set<HTMLElement>();
const ownTitle = new WeakMap<HTMLElement, string | null>();
const ownDisabled = new WeakMap<HTMLElement, boolean>();

/** The screen's own disabled for this element, as Vue renders a boolean attribute (truthy, or "" as in plain `disabled`). */
function screenDisabled(vnode: VNode): boolean {
  const v = (vnode.props as Record<string, unknown> | null)?.disabled;
  return v === '' || !!v;
}

/** The screen's own title for this element (null when it sets none), as Vue renders the attribute. */
function screenTitle(vnode: VNode): string | null {
  const v = (vnode.props as Record<string, unknown> | null)?.title;
  return v === null || v === undefined || v === false ? null : String(v);
}

function remember(el: HTMLElement, vnode: VNode) {
  ownDisabled.set(el, screenDisabled(vnode));
  ownTitle.set(el, screenTitle(vnode));
}

function apply(el: HTMLElement) {
  const control = el as HTMLButtonElement;
  if (readOnly.value) {
    control.disabled = true;
    el.dataset.readOnly = '1';
    el.title = READ_ONLY_TITLE;
  } else if (el.dataset.readOnly === '1') {
    delete el.dataset.readOnly;
    control.disabled = ownDisabled.get(el) ?? false;
    const t = ownTitle.get(el) ?? null;
    if (t === null) el.removeAttribute('title');
    else el.title = t;
  }
}

export const vChange: Directive<HTMLElement> = {
  mounted(el, _binding, vnode) { remember(el, vnode); marked.add(el); apply(el); },
  updated(el, _binding, vnode) { remember(el, vnode); apply(el); },
  unmounted(el) { marked.delete(el); },
};

watch(readOnly, () => { for (const el of marked) apply(el); }, { flush: 'sync' });

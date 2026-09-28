<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue';
import FileBrowser from './FileBrowser.vue';
import { hostFileBrowser, type HostBrowser } from '../lib/fileSystemSource';

/**
 * A picker over the HOST's own filesystem, bounded to the allowlist `GET /api/fs/roots` answers.
 * Opens at the roots level; picking one browses into it with `GET /api/fs/browse`.
 *
 * ---
 * **THE LISTING IS `FileBrowser`, AND THE BOUNDARY IS `FileBrowserPolicy`.**
 *
 * ONE COMPONENT OVER TWO DATA SOURCES: the LOOK and the NAVIGATION are shared with the Documents
 * dialog and the BOUNDARIES are not. This side is bounded by `FileBrowserPolicy` and its
 * `FileBrowser:Roots` allowlist, reaches the machine through the FOUR routes `/api/fs` has, and
 * has no delete - see `lib/fileSystemSource.ts`.
 *
 * **DELETE IS ABSENT BECAUSE THE CAPABILITY IS NEVER PASSED.** `FileBrowser` reads `v-if="deletion"`
 * on its one delete control and nothing below binds that prop. Not bound as `undefined`, not
 * disabled, not hidden behind a flag of this component's own: `/api/fs` has no delete route to
 * give it one, and adding a fifth route is deliberately not done. Pinned by
 * `__tests__/host-path-picker.mount.spec.ts`.
 *
 * **THE ROOTS ARE A LISTING, NOT A SECOND SCREEN.** `list('')` answers the allowlist as ordinary
 * folder rows, so this file keeps no row template, no breadcrumb and no empty state of its own -
 * which is the acceptance. The three things it does keep are the ones the shared browser has no
 * business knowing: the title, which root is open, and the way back out to all of them.
 * ---
 *
 * `mode` is `'folder'`-only FOR NOW - a type union of one, deliberately, rather than accepting
 * `'file'` and doing nothing sensible with it. File rows already render DISABLED rather than
 * hidden so a person can see a folder is not empty, which is what makes this shape a branch a
 * later `mode="file"` can extend rather than a rewrite - but widening the prop's TYPE is the act
 * that has to happen first, so a future caller cannot reach a silently broken picker by typing a
 * value nothing here refuses. The row-level half of that rule lives with the source, which is what
 * decides a file row is disabled.
 *
 * SELECTION IS EXPLICIT: a click on a row navigates, and only the "Choose this folder" button
 * confirms. One click cannot do both, which is what keeps a person from picking a folder by
 * accident on their way past it.
 *
 * This is a reusable component with no fixed caller: it may open from inside an ordinary dialog
 * (two teleported QDialogs stack fine on their own - see `CreateTeamDialog`'s own remark on that)
 * or from inside the Concierge FIXED-position shell, which is why it carries its own
 * `.host-path-picker` z-index lift in `app.scss` unconditionally rather than only where it turns
 * out to be needed.
 */

/**
 * `instanceOnly` offers the instance's own data root and no other root - for a caller whose Host
 * route refuses a path anywhere else, so a person is never led to a folder that cannot be used.
 */
const props = withDefaults(defineProps<{ mode?: 'folder'; instanceOnly?: boolean; title?: string }>(), {
  mode: 'folder',
  instanceOnly: false,
  title: 'Choose a folder',
});

const open = defineModel<boolean>({ required: true });

const emit = defineEmits<{ chose: [string] }>();

/**
 * The source, remade on every open - which is both how `FileBrowser` is told to start over and how
 * the allowlist comes to be read once per open, exactly as this picker has always read it.
 *
 * `shallowRef` RATHER THAN `ref`, and that is load-bearing: `ref` deep-converts what it holds, and
 * the conversion UNWRAPS a `Ref` sitting on a property - `browser.value.activeRoot` would then be
 * the root itself at runtime while TypeScript still called it a `Ref`. Shallow keeps the object
 * exactly as the source module made it, which is also what keeps the source's own functions from
 * being handed back through a proxy.
 */
const browser = shallowRef<HostBrowser | null>(null);

/** Where the browser says it landed - `''` at the roots level, and `null` before anything has
 *  landed at all. Read only by the empty state below, which is the one sentence that differs
 *  between the two levels. */
const here = ref<string | null>(null);

/** Which root is open, for the caption and the way back. `null` at the roots level. */
const activeRoot = computed(() => browser.value?.activeRoot.value ?? null);

const atRoots = computed(() => here.value === '');

/**
 * REPLACING THE SOURCE IS HOW A CALLER CHANGES ROOT - `FileBrowser` re-lists from `start`, which
 * is `''`, whenever the object it was given is swapped. So this button needs no navigation API on
 * the browser and gets a freshly read allowlist on the way back, which is the right answer for a
 * dialog that has been open a while.
 */
function goToRoots() {
  here.value = null;
  browser.value = hostFileBrowser({ instanceOnly: props.instanceOnly });
}

/**
 * The one place a selection is confirmed. A click on a row never reaches here - only the button
 * does - which is what keeps navigation and selection from being the same gesture.
 *
 * `path` is the slot's, which is the path the SOURCE said it opened rather than one this component
 * guessed at. The button disables on `busy` as well, or a click landing between a navigation
 * firing and its response arriving would emit the folder just navigated AWAY from; the guard here
 * is for the state where there is nothing to choose at all - the roots level, where `path` is `''`.
 */
function choose(path: string | null) {
  if (!path) return;

  emit('chose', path);
  open.value = false;
}

watch(open, (showing) => {
  if (!showing) return;

  goToRoots();
});
</script>

<template>
  <q-dialog v-model="open" class="host-path-picker">
    <q-card class="host-path-picker-card os-dialog-md">
      <q-card-section class="q-pb-none row items-center">
        <div>
          <div class="os-dialog-title">{{ title }}</div>
          <div class="text-caption os-text-muted">
            {{ activeRoot ? activeRoot.name : instanceOnly ? 'A folder inside this instance' : 'A folder on the Host itself' }}
          </div>
        </div>
        <q-space />
        <!-- The way back out of a root. `parentWithin` stops AT the root on purpose, so there is
             no `..` row that would climb out of one - this is the affordance that does it. -->
        <q-btn
          v-if="activeRoot"
          flat
          dense
          no-caps
          icon="apps"
          label="All locations"
          @click="goToRoots"
        />
      </q-card-section>

      <!-- THE ONE FILE BROWSER, and NO `deletion` PROP. Delete is absent because the capability
           was never passed; there is no route on `/api/fs` that would answer one. -->
      <FileBrowser :source="browser?.source ?? null" @navigated="here = $event">
        <template #empty>
          <!-- Empty roots: reachable when an operator configured roots and every one of them was
               dropped. A blank panel here would read as a broken feature. -->
          <template v-if="atRoots">
            No host access configured. An operator adds one under
            <span class="mono">FileBrowser:Roots</span> in
            <span class="mono">appsettings.json</span>.
          </template>
          <template v-else>Nothing here.</template>
        </template>

        <template #actions="{ path, busy }">
          <q-btn flat no-caps label="Cancel" @click="open = false" />
          <q-btn
            color="primary"
            no-caps
            label="Choose this folder"
            :disable="busy || !path"
            @click="choose(path)"
          />
        </template>
      </FileBrowser>
    </q-card>
  </q-dialog>
</template>

<style scoped>
</style>

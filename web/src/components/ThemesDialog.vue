<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useAppearanceStore } from '../stores/appearance';
import {
  COLOUR_KEYS,
  COLOUR_LABELS,
  CUSTOM_PREFIX,
  FONTS,
  MATCH_SYSTEM,
  MAX_NAME_LENGTH,
  MAX_TEXT_SIZE,
  MAX_THEMES,
  MIN_TEXT_SIZE,
  PRESETS,
  TEXT_SIZE_STEP,
  coloursOf,
  contrastWarnings,
  newThemeId,
  preset,
  type AppearanceRecord,
  type ColourKey,
  type CustomTheme,
  type FontId,
  type LineSpacing,
} from '../lib/appearance';

/**
 * THEMES: how the app looks to the signed-in person, opened from the account menu.
 *
 * Every change shows at once on the whole app behind the dialog - the app is the preview - and is
 * kept only on Save; Cancel (or closing) puts back what was saved. Text size, font and line spacing
 * apply to whatever theme is chosen; colours are set on the person's own themes, each started from a
 * preset. The Concierge's terminal keeps its own look and is not changed here.
 */
const open = defineModel<boolean>({ required: true });

const appearance = useAppearanceStore();

const draft = ref<AppearanceRecord>({});
const error = ref<string | null>(null);
const saving = ref(false);
const naming = ref(false);
const newName = ref('');

/** On open, the draft starts from what is saved (or what the person sees now, unsaved). */
watch(
  open,
  (isOpen) => {
    if (isOpen) {
      const saved = appearance.record;
      draft.value = JSON.parse(
        JSON.stringify(saved ?? { selected: appearance.resolved.selected }),
      ) as AppearanceRecord;
      error.value = null;
      naming.value = false;
      newName.value = '';
    } else {
      appearance.cancel();
    }
  },
  { immediate: true },
);

/** Every change is on screen at once. */
watch(draft, (value) => appearance.preview(JSON.parse(JSON.stringify(value)) as AppearanceRecord), {
  deep: true,
});

const themes = computed<CustomTheme[]>(() => draft.value.themes ?? []);
const selected = computed(() => appearance.resolved.selected);
const custom = computed(() => appearance.resolved.custom);
const shownColours = computed(() =>
  custom.value ? coloursOf(custom.value) : (preset(appearance.resolved.preset) ?? PRESETS[0]!).colours,
);
const warnings = computed(() => contrastWarnings(shownColours.value));

function select(value: string) {
  draft.value.selected = value;
}

const textSize = computed({
  get: () => draft.value.textSize ?? 100,
  set: (value: number) => (draft.value.textSize = value),
});

const font = computed({
  get: () => draft.value.font ?? 'plex',
  set: (value: FontId) => (draft.value.font = value),
});

const lineSpacing = computed({
  get: () => draft.value.lineSpacing ?? 'normal',
  set: (value: LineSpacing) => (draft.value.lineSpacing = value),
});

const fontOptions = FONTS.map((f) => ({ value: f.id, label: f.label }));
const spacingOptions = [
  { value: 'normal', label: 'Normal' },
  { value: 'relaxed', label: 'Relaxed' },
];

/** The theme being edited: the selected one of the person's own. */
const editing = computed(() =>
  themes.value.find((t) => CUSTOM_PREFIX + t.id === draft.value.selected) ?? null,
);

function setColour(key: ColourKey, value: string) {
  const theme = editing.value;
  if (!theme) return;
  theme.colours = { ...theme.colours, [key]: value.toLowerCase() };
}

function rename(value: string | number | null) {
  if (editing.value) editing.value.name = String(value ?? '').slice(0, MAX_NAME_LENGTH);
}

/** A new theme from what is on screen now: its preset and, for one of the person's own, its colours. */
function createTheme() {
  const name = newName.value.trim();
  if (!name) return;
  const theme: CustomTheme = {
    id: newThemeId(name, themes.value),
    name: name.slice(0, MAX_NAME_LENGTH),
    basedOn: appearance.resolved.preset,
    colours: { ...shownColours.value },
  };
  draft.value.themes = [...themes.value, theme];
  draft.value.selected = CUSTOM_PREFIX + theme.id;
  naming.value = false;
  newName.value = '';
}

/** Deleting the theme in use falls back to the preset it started from. */
function deleteTheme(theme: CustomTheme) {
  draft.value.themes = themes.value.filter((t) => t.id !== theme.id);
  if (draft.value.selected === CUSTOM_PREFIX + theme.id) draft.value.selected = theme.basedOn;
}

function swatch(colours: Record<string, string>) {
  return ['background', 'panels', 'text', 'accent'].map((k) => colours[k]);
}

async function save() {
  saving.value = true;
  error.value = null;
  try {
    await appearance.save(JSON.parse(JSON.stringify(draft.value)) as AppearanceRecord);
    open.value = false;
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    saving.value = false;
  }
}

function cancel() {
  open.value = false;
}
</script>

<template>
  <q-dialog v-model="open" data-test="themes-dialog">
    <q-card class="os-dialog-lg themes-card">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Themes</div>
        <div class="text-caption os-text-muted">
          How the app looks to you, on every computer you sign in from. Changes show at once; Save
          keeps them. The Concierge terminal keeps its own look.
        </div>
      </q-card-section>

      <q-card-section class="themes-body">
        <q-list class="themes-list" dense>
          <q-item
            clickable
            :active="selected === MATCH_SYSTEM"
            data-test="theme-choice-auto"
            @click="select(MATCH_SYSTEM)"
          >
            <q-item-section avatar><q-icon name="brightness_auto" /></q-item-section>
            <q-item-section>
              <q-item-label>Match system</q-item-label>
              <q-item-label caption>Light or Dark, as this computer is set</q-item-label>
            </q-item-section>
          </q-item>

          <q-item
            v-for="p in PRESETS"
            :key="p.id"
            clickable
            :active="selected === p.id"
            :data-test="`theme-choice-${p.id}`"
            @click="select(p.id)"
          >
            <q-item-section avatar>
              <span class="themes-swatch" aria-hidden="true">
                <span v-for="(c, i) in swatch(p.colours)" :key="i" :style="{ background: c }" />
              </span>
            </q-item-section>
            <q-item-section>{{ p.label }}</q-item-section>
          </q-item>

          <q-item-label header>My themes</q-item-label>
          <q-item v-if="themes.length === 0">
            <q-item-section class="text-caption os-text-muted">
              None yet. Choose a theme, then make your own from it below.
            </q-item-section>
          </q-item>
          <q-item
            v-for="t in themes"
            :key="t.id"
            clickable
            :active="selected === CUSTOM_PREFIX + t.id"
            :data-test="`theme-choice-custom-${t.id}`"
            @click="select(CUSTOM_PREFIX + t.id)"
          >
            <q-item-section avatar>
              <span class="themes-swatch" aria-hidden="true">
                <span v-for="(c, i) in swatch(coloursOf(t))" :key="i" :style="{ background: c }" />
              </span>
            </q-item-section>
            <q-item-section>{{ t.name }}</q-item-section>
            <q-item-section side>
              <q-btn
                flat
                dense
                round
                icon="delete"
                :aria-label="`Delete ${t.name}`"
                :data-test="`theme-delete-${t.id}`"
                @click.stop="deleteTheme(t)"
              />
            </q-item-section>
          </q-item>

          <q-item v-if="!naming && themes.length < MAX_THEMES">
            <q-item-section>
              <q-btn
                flat
                dense
                no-caps
                icon="add"
                label="New theme from this one"
                data-test="theme-new"
                @click="naming = true"
              />
            </q-item-section>
          </q-item>
          <q-item v-if="naming">
            <q-item-section>
              <q-input
                v-model="newName"
                dense
                autofocus
                label="Name"
                :maxlength="MAX_NAME_LENGTH"
                data-test="theme-new-name"
                @keyup.enter="createTheme"
              />
            </q-item-section>
            <q-item-section side>
              <q-btn
                flat
                dense
                no-caps
                label="Create"
                color="primary"
                :disable="!newName.trim()"
                data-test="theme-new-create"
                @click="createTheme"
              />
            </q-item-section>
          </q-item>
        </q-list>

        <div class="themes-controls q-gutter-y-md">
          <div>
            <div class="text-subtitle2">Text size</div>
            <div class="row items-center no-wrap q-gutter-x-sm">
              <span class="themes-a-small" aria-hidden="true">A</span>
              <q-slider
                v-model="textSize"
                :min="MIN_TEXT_SIZE"
                :max="MAX_TEXT_SIZE"
                :step="TEXT_SIZE_STEP"
                markers
                snap
                label
                :label-value="`${textSize}%`"
                aria-label="Text size"
                data-test="theme-text-size"
                class="col"
              />
              <span class="themes-a-large" aria-hidden="true">A</span>
              <span class="themes-size-figure" data-test="theme-text-size-figure">{{ textSize }}%</span>
            </div>
          </div>

          <q-select
            v-model="font"
            :options="fontOptions"
            emit-value
            map-options
            dense
            outlined
            label="Font"
            data-test="theme-font"
          />

          <div>
            <div class="text-subtitle2">Line spacing</div>
            <q-option-group
              v-model="lineSpacing"
              :options="spacingOptions"
              inline
              dense
              data-test="theme-line-spacing"
            />
          </div>

          <div>
            <div class="text-subtitle2">Colours</div>
            <div v-if="editing" class="q-gutter-y-sm">
              <q-input
                :model-value="editing.name"
                dense
                outlined
                label="Theme name"
                :maxlength="MAX_NAME_LENGTH"
                data-test="theme-name"
                @update:model-value="rename"
              />
              <div class="themes-colours">
                <label v-for="key in COLOUR_KEYS" :key="key" class="themes-colour">
                  <input
                    type="color"
                    :value="shownColours[key]"
                    :aria-label="COLOUR_LABELS[key]"
                    :data-test="`theme-colour-${key}`"
                    @input="setColour(key, ($event.target as HTMLInputElement).value)"
                  />
                  <span>{{ COLOUR_LABELS[key] }}</span>
                </label>
              </div>
            </div>
            <div v-else class="text-caption os-text-muted" data-test="theme-colours-note">
              Colours are set on your own themes. Use “New theme from this one” to make one from what
              you see now.
            </div>
            <div
              v-for="w in warnings"
              :key="w"
              class="text-caption themes-warning q-mt-xs"
              data-test="theme-contrast-warning"
            >
              <q-icon name="warning" /> {{ w }}
            </div>
          </div>
        </div>
      </q-card-section>

      <q-card-section v-if="error" class="q-pt-none">
        <div class="themes-error" data-test="themes-error">{{ error }}</div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" data-test="themes-cancel" @click="cancel" />
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Save"
          :loading="saving"
          data-test="themes-save"
          @click="save"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.themes-body {
  display: grid;
  grid-template-columns: minmax(14rem, 1fr) 2fr;
  gap: 1.5rem;
}

@media (max-width: 40rem) {
  .themes-body {
    grid-template-columns: 1fr;
  }
}

.themes-list {
  border-right: 1px solid var(--os-rule);
}

.themes-swatch {
  display: inline-flex;
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  overflow: hidden;
}

.themes-swatch > span {
  width: 0.625rem;
  height: 1.25rem;
}

.themes-a-small {
  font-size: 0.75rem;
}

.themes-a-large {
  font-size: 1.25rem;
}

.themes-size-figure {
  min-width: 3rem;
  text-align: right;
}

.themes-warning {
  color: var(--os-warn-ink);
}

.themes-error {
  color: var(--os-error);
}

.themes-colours {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(8rem, 1fr));
  gap: 0.5rem;
}

.themes-colour {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
}

.themes-colour input {
  width: 2rem;
  height: 2rem;
  padding: 0;
  border: 1px solid var(--os-rule-strong);
  border-radius: 4px;
  background: none;
  cursor: pointer;
}
</style>

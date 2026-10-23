<script setup lang="ts">
import FieldHelp from './FieldHelp.vue';
import { DirectoryLabel } from '../lib/connections';

/**
 * A MICROSOFT CONNECTION'S OWN DIRECTORY, optional: a tenant ID or a domain it signs in through. Add
 * connection's Sign in step, Reconnect and Advanced's Connect form show it, in the same words. The
 * Host is the check; its refusal (a sentence naming this field) is shown here as `error`.
 */
defineProps<{
  /** The Host's sentence refusing the value typed, or empty. */
  error?: string | undefined;
}>();

const value = defineModel<string>({ required: true });

const help =
  'Optional. Needed for a personal Microsoft account that owns an Azure subscription: the subscription\'s '
  + 'directory ID (Azure portal, Microsoft Entra ID, Overview) or one of its domains, such as '
  + 'example.onmicrosoft.com. A work or school account leaves it empty.';
</script>

<template>
  <q-input
    v-model="value"
    outlined
    dense
    :label="`${DirectoryLabel} (optional)`"
    spellcheck="false"
    autocomplete="off"
    :error="!!error"
    :error-message="error"
    data-directory-field
  >
    <template #append><FieldHelp :text="help" /></template>
  </q-input>
</template>

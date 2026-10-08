<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { addMailbox } from '../api/client';
import type { Connection, MailboxPreset, MailSecurity, MailServer } from '../api/types';
import { mailboxHint } from '../lib/mailboxHints';

/**
 * A MAILBOX WITH AN APP PASSWORD, over IMAP and SMTP: the form inside Add connection.
 *
 * The tiles are the Host's presets (Gmail, iCloud, Yahoo, Other), never hard-coded here. Choosing one
 * fills the six server fields, each still editable; Other empties them to type. Save sends the lot to
 * the Host, which logs in to IMAP and authenticates to SMTP before it saves anything, and answers one
 * sentence: connected, or what to fix. A refused login saves nothing, so Save can be tried again.
 *
 * THE CHOSEN TILE'S HINTS sit above the fields: where to make the app password, with links, and for
 * Gmail who cannot make one - pointed to Advanced, Gmail through a Google app of one's own, when the
 * dialog can offer it (`advanced`).
 *
 * USERNAME IS THE SERVER LOGIN, Name the connection's label. For a provider's tile the login is the
 * email address, so Username waits under an Advanced disclosure for the provider that asks for
 * something else; Other shows it plainly. Either way, what is typed there is sent.
 *
 * THE APP PASSWORD IS WRITE-ONLY: typed, sent once, cleared once saved, never filled from anything.
 * A saved mailbox says only that its password is set.
 */
const props = defineProps<{
  presets: MailboxPreset[];
  /** The tile chosen when the form opens. */
  preset: string;
  /** Whether Gmail's own Google app is on offer, for who cannot make an app password. */
  advanced?: boolean;
}>();
const emit = defineEmits<{
  /** Gmail through a Google app of one's own, instead. */
  advanced: [];
  /** Saved, after a successful login: the connection and the login's sentence. */
  saved: [connection: Connection, sentence: string];
}>();

const presetId = ref(props.preset);
const name = ref('');
const account = ref('');
const username = ref('');
const password = ref('');
const imapHost = ref('');
const imapPort = ref('');
const imapSecurity = ref<MailSecurity>('TLS');
const smtpHost = ref('');
const smtpPort = ref('');
const smtpSecurity = ref<MailSecurity>('TLS');

const hint = computed(() => mailboxHint(presetId.value));

/** Whether the login is the email address unless said otherwise: every tile but Other. */
const loginIsAddress = computed(() => presetId.value !== 'other');

const securities: MailSecurity[] = ['TLS', 'STARTTLS'];

/** The login test's sentence, and whether it said connected. */
const sentence = ref('');
const connected = ref(false);
const busy = ref(false);

function fill(id: string) {
  presetId.value = id;
  const chosen = props.presets.find((candidate) => candidate.id === id);
  imapHost.value = chosen?.imap?.host ?? '';
  imapPort.value = chosen?.imap ? String(chosen.imap.port) : '';
  imapSecurity.value = chosen?.imap?.security ?? 'TLS';
  smtpHost.value = chosen?.smtp?.host ?? '';
  smtpPort.value = chosen?.smtp ? String(chosen.smtp.port) : '';
  smtpSecurity.value = chosen?.smtp?.security ?? 'TLS';
  sentence.value = '';
}

watch(() => props.preset, fill, { immediate: true });

function server(host: string, port: string, security: MailSecurity): MailServer {
  return { host: host.trim(), port: Number(port.trim()), security };
}

const portProblem = (port: string) => (port.trim() === '' || /^\d{1,5}$/.test(port.trim()) ? null : 'A port is a number, such as 993.');

const ready = computed(
  () =>
    account.value.trim() !== ''
    && password.value !== ''
    && imapHost.value.trim() !== ''
    && smtpHost.value.trim() !== ''
    && /^\d{1,5}$/.test(imapPort.value.trim())
    && /^\d{1,5}$/.test(smtpPort.value.trim()),
);

async function save() {
  if (!ready.value || busy.value || connected.value) return;

  busy.value = true;
  sentence.value = '';
  try {
    const answer = await addMailbox({
      preset: presetId.value,
      name: name.value.trim() || null,
      account: account.value.trim(),
      username: username.value.trim() || null,
      password: password.value,
      imap: server(imapHost.value, imapPort.value, imapSecurity.value),
      smtp: server(smtpHost.value, smtpPort.value, smtpSecurity.value),
    });
    password.value = '';
    connected.value = true;
    sentence.value = answer.sentence;
    emit('saved', answer.connection, answer.sentence);
  } catch (cause) {
    // The Host's sentence: what to fix, or which field is wrong. Nothing was saved.
    sentence.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

/** Whether the person has typed anything not yet saved: the dialog then holds on to it. */
const typed = computed(() => !connected.value && [name, account, username, password].some((field) => field.value !== ''));

defineExpose({ save, ready, busy, connected, typed });
</script>

<template>
  <div class="q-gutter-y-sm" data-mailbox-form>
    <div class="row q-gutter-sm">
      <q-btn
        v-for="tile in presets"
        :key="tile.id"
        no-caps
        :outline="presetId !== tile.id"
        :color="presetId === tile.id ? 'primary' : undefined"
        :label="tile.name"
        :data-mailbox-preset="tile.id"
        :disable="connected"
        @click="fill(tile.id)"
      />
    </div>

    <template v-if="!connected">
      <div class="mailbox-hints text-caption" data-mailbox-hints>
        <ol>
          <li v-for="(item, index) in hint.steps" :key="index" data-mailbox-hint>
            {{ item.text }}
            <a v-if="item.link" :href="item.link.href" target="_blank" rel="noopener noreferrer" data-mailbox-hint-link>{{ item.link.label }}</a>
          </li>
        </ol>
        <div v-if="hint.cannot && advanced" data-mailbox-cannot>
          {{ hint.cannot }}
          <q-btn flat dense no-caps size="sm" label="Advanced…" data-mailbox-advanced @click="emit('advanced')" />
        </div>
      </div>

      <q-input v-model="account" outlined dense label="Email address" type="email" autocomplete="off" spellcheck="false" />
      <q-input
        v-if="!loginIsAddress"
        v-model="username"
        outlined
        dense
        label="Username"
        hint="The server login. Leave empty to use the email address."
        autocomplete="off"
        spellcheck="false"
      />
      <q-input
        v-model="password"
        outlined
        dense
        type="password"
        label="App password"
        hint="Stored encrypted and never shown again."
        autocomplete="new-password"
        spellcheck="false"
      />

      <div class="mailbox-server">
        <q-input v-model="imapHost" outlined dense label="IMAP server" autocomplete="off" spellcheck="false" class="mailbox-host" />
        <q-input
          v-model="imapPort"
          outlined
          dense
          label="IMAP port"
          inputmode="numeric"
          autocomplete="off"
          class="mailbox-port"
          :error="portProblem(imapPort) !== null"
          :error-message="portProblem(imapPort) ?? undefined"
        />
        <q-select v-model="imapSecurity" outlined dense label="IMAP security" :options="securities" class="mailbox-security" />
      </div>
      <div class="mailbox-server">
        <q-input v-model="smtpHost" outlined dense label="SMTP server" autocomplete="off" spellcheck="false" class="mailbox-host" />
        <q-input
          v-model="smtpPort"
          outlined
          dense
          label="SMTP port"
          inputmode="numeric"
          autocomplete="off"
          class="mailbox-port"
          :error="portProblem(smtpPort) !== null"
          :error-message="portProblem(smtpPort) ?? undefined"
        />
        <q-select v-model="smtpSecurity" outlined dense label="SMTP security" :options="securities" class="mailbox-security" />
      </div>

      <q-input
        v-model="name"
        outlined
        dense
        label="Name (optional)"
        hint="The label shown in Connections. Leave empty to use the email address."
        maxlength="80"
        autocomplete="off"
      />

      <q-expansion-item v-if="loginIsAddress" dense dense-toggle label="Advanced" data-mailbox-login-advanced>
        <div class="q-pt-sm q-gutter-y-sm">
          <div class="text-caption">
            Username is the server login. It is needed only when the provider asks for something other than the email
            address - iCloud's IMAP may take the part before the @ alone.
          </div>
          <q-input
            v-model="username"
            outlined
            dense
            label="Username"
            hint="Leave empty to use the email address."
            autocomplete="off"
            spellcheck="false"
          />
        </div>
      </q-expansion-item>
    </template>

    <div v-if="sentence" :class="connected ? 'text-positive' : 'text-negative'" data-mailbox-sentence>{{ sentence }}</div>
  </div>
</template>

<style scoped>
.mailbox-hints ol {
  margin: 0;
  padding-left: 20px;
}

.mailbox-server {
  display: flex;
  gap: 8px;
  align-items: flex-start;
}

.mailbox-host {
  flex: 1 1 auto;
  min-width: 0;
}

.mailbox-port {
  width: 96px;
}

.mailbox-security {
  width: 128px;
}
</style>

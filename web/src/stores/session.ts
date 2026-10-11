import { defineStore, acceptHMRUpdate } from 'pinia';
import * as api from '../api/client';
import { Unauthorized } from '../api/client';

/** Who is signed in. Every person is an administrator, so there is no tier on the session. */
interface User {
  id: string;
  email: string;
}

/**
 * Who is signed in, and whether this instance has been set up at all.
 *
 * `checked` exists so the router can tell "not signed in" from "have not asked yet" - without it
 * the first navigation redirects to the login page before /me has answered.
 *
 * `needsFirstAccount` is the FIRST-ACCOUNT flag - the wire still calls it `needsAdmin`, which is
 * kept there because the server sends it. It gates the landing page's create mode (`lib/door.ts`)
 * and nothing else; it says nothing about a tier, because there is none.
 */
export const useSessionStore = defineStore('session', {
  state: () => ({
    user: null as User | null,
    needsFirstAccount: false,
    checked: false,
  }),

  actions: {
    async refresh() {
      // The whole body is wrapped, not just the /me call: a failing getAuthState() (API
      // unreachable, 500, a cold start) must still leave `checked` true, or every later
      // navigation retries the same failing call forever. A real failure still rethrows -
      // only the flag-setting is unconditional, not the error.
      try {
        const state = await api.getAuthState();
        this.needsFirstAccount = state.needsAdmin;

        if (state.needsAdmin) {
          this.user = null;
          return;
        }

        try {
          this.user = await api.getMe();
        } catch (error) {
          if (!(error instanceof Unauthorized)) throw error;
          this.user = null;
        }
      } finally {
        this.checked = true;
      }
    },

    async signIn(email: string, password: string) {
      this.user = await api.login(email, password);
      this.needsFirstAccount = false;
    },

    async createFirstAccount(email: string, password: string) {
      await api.createFirstAccount(email, password);

      // Cleared HERE rather than left to signIn: the account exists from this line onwards, so the
      // page must stop offering to create it whatever happens next. If the sign-in below throws,
      // the landing page would otherwise still show "Create the first account", the next attempt
      // would 409, and nothing but a reload would move the person forward.
      this.needsFirstAccount = false;

      await this.signIn(email, password);
    },

    /**
     * The server answers with the STORED user, and that answer replaces the local one - not the
     * text that was typed. An address is normalised on the way in (trimmed, lowercased), so
     * echoing the typed value back would leave the name in the corner reading as something
     * slightly different from what the next login actually needs.
     */
    async changeEmail(currentEmail: string, email: string) {
      this.user = await api.changeEmail(currentEmail, email);
    },

    /** Nothing local changes: no claim, no displayed value, and the session deliberately survives
     *  it - the person changing the password is the person holding the session. */
    async changePassword(currentPassword: string, newPassword: string) {
      await api.changePassword(currentPassword, newPassword);
    },

    async signOut() {
      try {
        await api.logout();
      } finally {
        // Cleared regardless: a sign-out that leaves the store thinking it is signed in is worse
        // than one that fails, and the server-side cookie is dropped on the next 401 anyway.
        this.user = null;
      }
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useSessionStore, import.meta.hot));
}

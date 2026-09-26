import { defineRouter } from '#q-app';
import {
  createMemoryHistory,
  createRouter,
  createWebHashHistory,
  createWebHistory,
} from 'vue-router';

import routes from './routes';
import { useSessionStore } from '../stores/session';

/*
 * If not building with SSR mode, you can
 * directly export the Router instantiation;
 *
 * The function below can be async too; either use
 * async/await or return a Promise which resolves
 * with the Router instance.
 */

export default defineRouter((/* { store, ssrContext } */) => {
  const createHistory = import.meta.env.QUASAR_SERVER
    ? createMemoryHistory
    : (import.meta.env.QUASAR_VUE_ROUTER_MODE === 'history' ? createWebHistory : createWebHashHistory);

  const Router = createRouter({
    scrollBehavior: () => ({ left: 0, top: 0 }),
    routes,

    // Leave this as is and make changes in quasar.conf.js instead!
    // quasar.conf.js -> build -> vueRouterMode
    // quasar.conf.js -> build -> publicPath
    history: createHistory(import.meta.env.QUASAR_VUE_ROUTER_BASE)
  });

  // Asked once, not per navigation: /me is cheap, but re-checking on every route change makes every
  // navigation wait on a round trip. `checked` is what distinguishes "signed out" from "have not
  // asked yet" - without it the first navigation redirects away before /me has answered.
  Router.beforeEach(async (to) => {
    const session = useSessionStore();
    const isPublic = to.matched.some((record) => record.meta.publicRoute);

    if (!session.checked) {
      try {
        await session.refresh();
      } catch {
        // A guard that rejects renders nothing at all - App.vue is a bare router-view with no
        // error boundary - so a backend that is down would leave a blank page with no way forward.
        // The landing page needs no backend to render, so it is the one safe destination; its
        // button will surface the real error the moment it is pressed.
        return isPublic ? true : '/';
      }
    }

    // Someone already signed in has no business on the credential page. The landing page is
    // deliberately NOT redirected: it is the product's front page, and it shows a Console button
    // rather than a form once there is a session.
    if (to.path === '/login' && session.user) return '/console';

    if (isPublic) return true;

    return session.user ? true : '/';
  });

  return Router;
});

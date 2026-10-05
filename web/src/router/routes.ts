import type { RouteRecordRaw } from 'vue-router';

/**
 * `/` is the front door - the product name and a login box - and is PUBLIC. The Console lives at `/console`: a
 * signed-out visitor who arrives at the root must meet a door, not a Console rendering an error
 * banner over data it was refused. `/board` redirects here so an old bookmark still lands.
 *
 * `publicRoute` in the meta is what the guard reads. Marking it here rather than listing paths in
 * the guard keeps the two from drifting when a route is added.
 */
/**
 * THE CONSOLE, ONE COPY UNDER EVERY CONSOLE ADDRESS. `/console`, the install link and the Solutions
 * screens all show the board as their default view, and the screen an address opens - the install
 * review, the launcher, a control panel - in the layout's `overlay` view over it. Following a link
 * between them therefore leaves the board as it is and only opens or closes the screen. Each used to
 * render a board of its own, so a link tore the whole Console down and built it again, and a fault
 * in that teardown left the old view up with the new address and nothing else (B003V).
 */
const Console = () => import('@/pages/IndexPage.vue');

const routes: RouteRecordRaw[] = [
  {
    path: '/',
    component: () => import('@/pages/LandingPage.vue'),
    meta: { publicRoute: true },
  },

  // The same door, for anyone holding an old bookmark.
  {
    path: '/login',
    component: () => import('@/pages/LandingPage.vue'),
    meta: { publicRoute: true },
  },

  {
    path: '/console',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [{ path: '', component: Console }],
  },

  // THE INSTALL DEEP LINK: `#/solutions/install?folder=<absolute path>` opens the solution wizard over
  // the Console, filled in with that folder. It never installs by itself. Gated like the Console: a
  // signed-out visitor signs in first and is returned here (see guard.ts).
  {
    path: '/solutions/install',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [{ path: '', components: { default: Console, overlay: () => import('@/pages/SolutionInstallPage.vue') } }],
  },

  // THE SOLUTIONS LAUNCHER, `#/solutions`, and ONE SOLUTION'S CONTROL PANEL, `#/solutions/<team>`: the
  // Console with that screen open over it. Addresses rather than dialog state, so the board header,
  // a tile and the Concierge can all link to the same place. `/solutions/install` is declared above
  // and wins over `:team` (a team id is never `install`'s route).
  {
    path: '/solutions',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [
      { path: '', components: { default: Console, overlay: () => import('@/pages/SolutionsPage.vue') } },
      { path: ':team', components: { default: Console, overlay: () => import('@/pages/SolutionsPage.vue') } },
    ],
  },

  {
    path: '/board',
    redirect: '/console',
  },

  // Always leave this as last one.
  {
    path: '/:catchAll(.*)*',
    component: () => import('@/pages/ErrorNotFound.vue'),
    meta: { publicRoute: true },
  },
];

export default routes;

import type { RouteRecordRaw } from 'vue-router';

/**
 * `/` is the front door - the product name and a login box - and is PUBLIC. The Console lives at `/console`: a
 * signed-out visitor who arrives at the root must meet a door, not a Console rendering an error
 * banner over data it was refused. `/board` redirects here so an old bookmark still lands.
 *
 * `publicRoute` in the meta is what the guard reads. Marking it here rather than listing paths in
 * the guard keeps the two from drifting when a route is added.
 */
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
    children: [{ path: '', component: () => import('@/pages/IndexPage.vue') }],
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

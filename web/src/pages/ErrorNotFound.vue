<template>
  <div class="fullscreen not-found text-center q-pa-md flex flex-center">
    <div>
      <div style="font-size: 30vh">
        404
      </div>

      <div class="text-h2" style="opacity:.4">
        Oops. Nothing here...
      </div>

      <q-btn
        class="q-mt-xl"
        color="primary"
        unelevated
        to="/"
        label="Go Home"
        no-caps
      />
    </div>
  </div>
</template>

<style scoped>
/* The console's own ground and ink rather than Quasar's stock blue. */
.not-found {
  background: var(--os-paper, var(--os-chrome));
  color: var(--os-ink);
}
</style>

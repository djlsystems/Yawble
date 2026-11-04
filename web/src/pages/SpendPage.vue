<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import SpendDialog from '../components/SpendDialog.vue';

/**
 * ADMIN → SPEND AS AN ADDRESS: `#/spend` is the Console with the Spend page open over it, in the layout's
 * `overlay` view, so a reload stays on it. Closing it leaves for the plain Console.
 */
const router = useRouter();
const open = ref(false);

onMounted(() => { open.value = true; });

watch(open, (isOpen) => {
  if (!isOpen) void router.replace('/console');
});
</script>

<template>
  <SpendDialog v-model="open" />
</template>

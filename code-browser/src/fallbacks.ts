import { defineComponent, h } from 'vue';

// Stand-ins for the app's shared LoadingNote and BusyLabel, used only when the host passes none (the package's own
// tests). The shell passes its own components, so the dialog looks like the rest of the app.
export const FallbackLoading = defineComponent({
  props: { text: { type: String, default: 'Loading…' } },
  setup(props) {
    return () => h('p', { class: 'loading-note', role: 'status', 'data-test': 'loading-note' }, props.text);
  },
});

export const FallbackBusy = defineComponent({
  props: { busy: { type: Boolean, required: true }, text: { type: String, default: undefined } },
  setup(props, { slots }) {
    return () => (props.busy ? [h('span', { class: 'btn-spinner', 'data-test': 'btn-spinner' }), props.text ?? ''] : slots.default?.());
  },
});

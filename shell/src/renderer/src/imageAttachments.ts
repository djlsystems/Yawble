import { ref } from 'vue';

/** An image the person attached to a message: its name, its data (base64, no data: prefix) and a preview URL. */
export interface AttachedImage { name: string; base64: string; url: string }

export const MaxImages = 6;
export const MaxImageBytes = 10 * 1024 * 1024;
const Accepted = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];

/**
 * Images for one message box: picked with a file dialog or pasted (a screenshot on the clipboard). The core checks
 * them again and saves them where the agent reading the message can open them.
 */
export function useImageAttachments() {
  const images = ref<AttachedImage[]>([]);
  const problem = ref<string | null>(null);

  async function add(files: Iterable<File>) {
    problem.value = null;
    for (const f of files) {
      if (!Accepted.includes(f.type)) { problem.value = `${f.name || 'That file'} is not a PNG, JPEG, GIF or WebP image`; continue; }
      if (f.size > MaxImageBytes) { problem.value = `${f.name || 'The image'} is larger than 10 MB`; continue; }
      if (images.value.length >= MaxImages) { problem.value = `At most ${MaxImages} images per message`; break; }
      const url = await new Promise<string>((resolve, reject) => {
        const r = new FileReader();
        r.onload = () => resolve(String(r.result));
        r.onerror = () => reject(r.error);
        r.readAsDataURL(f);
      });
      const ext = f.type.split('/')[1].replace('jpeg', 'jpg');
      const name = f.name && f.name !== 'image.png' ? f.name : `pasted-${images.value.length + 1}.${ext}`;
      images.value.push({ name, base64: url.slice(url.indexOf(',') + 1), url });
    }
  }

  /** A paste with images in it adds them (and keeps any text paste as it is). */
  function onPaste(e: ClipboardEvent) {
    const files = [...(e.clipboardData?.items ?? [])].filter((i) => i.kind === 'file' && i.type.startsWith('image/'))
      .map((i) => i.getAsFile()).filter((f): f is File => !!f);
    if (files.length === 0) return;
    e.preventDefault();
    void add(files);
  }

  function remove(i: number) { images.value.splice(i, 1); }
  function clear() { images.value = []; problem.value = null; }
  /** What the core takes: [{ name, base64 }]. */
  const payload = () => images.value.map(({ name, base64 }) => ({ name, base64 }));

  return { images, problem, add, onPaste, remove, clear, payload };
}

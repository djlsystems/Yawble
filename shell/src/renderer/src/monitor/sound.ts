import type { Effect } from './model';

/**
 * The Monitor's optional sound (the speaker beside the density control): one short, quiet tone per kind of event, made
 * with Web Audio (no files). OFF unless the person turns it on; the choice is kept. Only the moments worth an ear make
 * a sound - a step or a told message never does - and each kind sounds at most once per SoundGapMs, all kinds together
 * at most MaxPerSecond, so a busy wall does not chatter. A window without Web Audio stays silent.
 */
export type SoundKind = 'needsYou' | 'failed' | 'done' | 'handback' | 'commit' | 'merge' | 'landed' | 'push';

/** Each kind's tone: frequencies played one after another (Hz), each `ms` long, and a waveform. */
export const SoundTones: Readonly<Record<SoundKind, { notes: number[]; ms: number; wave: OscillatorType }>> = {
  needsYou: { notes: [660, 880], ms: 120, wave: 'sine' },
  failed: { notes: [330, 247], ms: 150, wave: 'triangle' },
  done: { notes: [523, 659, 784], ms: 90, wave: 'sine' },
  handback: { notes: [587, 784], ms: 90, wave: 'sine' },
  commit: { notes: [988], ms: 60, wave: 'sine' },
  merge: { notes: [440, 554], ms: 90, wave: 'triangle' },
  landed: { notes: [392, 523, 659, 784], ms: 90, wave: 'triangle' },
  push: { notes: [740], ms: 70, wave: 'sine' },
};

export const SoundGapMs = 1500;
export const MaxPerSecond = 3;
/** Quiet: the peak gain of a tone. */
export const SoundGain = 0.05;
export const SoundStorageKey = 'yawble.monitor.sound';

/** The kind of sound an effect makes, or null (most make none). */
export function soundOf(e: Effect): SoundKind | null {
  switch (e.kind) {
    case 'commit': return 'commit';
    case 'merge': return e.big ? 'landed' : 'merge';
    case 'push': return 'push';
    case 'comet': return e.color === 'handback' ? 'handback' : null;
    case 'burst':
      if (e.color === 'decision') return 'needsYou';
      if (e.color === 'fail' || e.color === 'blocked') return 'failed';
      if (e.color === 'done' && e.big) return 'done';
      return null;
    default: return null;
  }
}

/** Off unless the person turned it on (stored 'on'). */
export function readSound(storage: Pick<Storage, 'getItem'> | null = safeStorage()): boolean {
  try { return storage?.getItem(SoundStorageKey) === 'on'; } catch { return false; }
}
export function writeSound(on: boolean, storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeStorage()): void {
  try { if (on) storage?.setItem(SoundStorageKey, 'on'); else storage?.removeItem(SoundStorageKey); } catch { /* not kept */ }
}

/** Plays the effects' sounds when on; `play` replaces the Web Audio player (a test hands its own). */
export class MonitorSound {
  on = false;
  private last = new Map<SoundKind, number>();
  private recent: number[] = [];
  private ctx: AudioContext | null = null;
  constructor(private play: ((kind: SoundKind) => void) | null = null) {}

  /** The kinds actually played for these effects at `now` (ms), after the gaps. */
  hear(effects: readonly Effect[], now = performance.now()): SoundKind[] {
    if (!this.on) return [];
    const played: SoundKind[] = [];
    for (const e of effects) {
      const kind = soundOf(e);
      if (!kind || played.includes(kind)) continue;
      if (now - (this.last.get(kind) ?? -Infinity) < SoundGapMs) continue;
      this.recent = this.recent.filter((t) => now - t < 1000);
      if (this.recent.length >= MaxPerSecond) break;
      this.last.set(kind, now);
      this.recent.push(now);
      played.push(kind);
      if (this.play) this.play(kind);
      else this.tone(kind);
    }
    return played;
  }

  /** Lets the browser start audio: called from the click that turns the sound on. */
  unlock(): void {
    if (this.play) return;
    try { void this.context()?.resume?.()?.catch(() => {}); } catch { /* silent */ }
  }

  destroy(): void {
    try { void this.ctx?.close?.()?.catch(() => {}); } catch { /* gone */ }
    this.ctx = null;
  }

  private context(): AudioContext | null {
    if (this.ctx) return this.ctx;
    const g = globalThis as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext };
    const Ctor = g.AudioContext ?? g.webkitAudioContext;
    if (!Ctor) return null;
    try { this.ctx = new Ctor(); } catch { this.ctx = null; }
    return this.ctx;
  }

  private tone(kind: SoundKind): void {
    const ctx = this.context();
    if (!ctx) return;
    try {
      const { notes, ms, wave } = SoundTones[kind];
      const len = ms / 1000;
      let t = ctx.currentTime;
      for (const f of notes) {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = wave;
        osc.frequency.value = f;
        gain.gain.setValueAtTime(0.0001, t);
        gain.gain.exponentialRampToValueAtTime(SoundGain, t + 0.012);
        gain.gain.exponentialRampToValueAtTime(0.0001, t + len);
        osc.connect(gain).connect(ctx.destination);
        osc.start(t);
        osc.stop(t + len + 0.02);
        t += len * 0.85;
      }
    } catch { /* silent */ }
  }
}

function safeStorage(): Storage | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage; } catch { return null; }
}

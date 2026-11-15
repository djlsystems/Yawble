// A fake NSIS for the tests: runs one macro of the generated include (packaging/nsis-include.mjs) over the small
// subset of NSIS and LogicLib it uses, with every outside effect faked (the command line, the environment, the
// processes tasklist finds, the person's answers, the folders). No makensis, no installer, nothing on disk.
// An instruction it does not know fails the test, so the include cannot grow past what is checked here.
import path from 'node:path';

/** The macros of an include's text: name -> body lines. */
export function macros(text) {
  const out = new Map();
  let name = null;
  for (const raw of text.split('\r\n')) {
    const line = raw.trim();
    if (line.startsWith('!macro ')) { name = line.slice(7).trim(); out.set(name, []); continue; }
    if (line === '!macroend') { name = null; continue; }
    if (name !== null && line !== '' && !line.startsWith(';')) out.get(name).push(line);
  }
  return out;
}

/** Splits one line into words: "..." `...` '...' are one word each, quotes removed. */
export function words(line) {
  const out = [];
  let i = 0;
  while (i < line.length) {
    if (line[i] === ' ') { i++; continue; }
    const q = line[i];
    if (q === '"' || q === '`' || q === "'") {
      let j = i + 1;
      let s = '';
      while (j < line.length && line[j] !== q) {
        if (line.startsWith('$\\' + q, j)) { s += '$\\' + q; j += 3; continue; } // kept for expand()
        s += line[j++];
      }
      if (j >= line.length) throw new Error(`unterminated ${q} in: ${line}`);
      out.push(s);
      i = j + 1;
    } else {
      let j = i;
      while (j < line.length && line[j] !== ' ') j++;
      out.push(line.slice(i, j));
      i = j;
    }
  }
  return out;
}

// Flat program: LogicLib compiled to jumps, as makensis does; labels take no instruction (relative jumps skip none).
function compile(all, name) {
  const ops = [];
  const labels = new Map();
  let n = 0;
  const label = () => `L${n++}`;
  const place = (l) => labels.set(l, ops.length);
  const stack = [];
  const cond = (w) => (w.length === 1 ? { flag: w[0] } : { a: w[0], op: w[1], b: w[2] });
  const emit = (lines) => {
    for (const line of lines) {
      const w = words(line);
      const top = stack[stack.length - 1];
      switch (w[0]) {
        case '!insertmacro':
          if (!all.has(w[1])) throw new Error(`no macro ${w[1]}`);
          emit(all.get(w[1]));
          break;
        case '${If}': case '${IfNot}': {
          const f = { kind: 'if', next: label(), end: label() };
          stack.push(f);
          ops.push({ jumpUnless: cond(w.slice(1)), negate: w[0] === '${IfNot}', to: f.next });
          break;
        }
        case '${AndIf}': case '${AndIfNot}':
          ops.push({ jumpUnless: cond(w.slice(1)), negate: w[0] === '${AndIfNot}', to: top.next });
          break;
        case '${ElseIf}':
          ops.push({ goto: top.end });
          place(top.next);
          top.next = label();
          ops.push({ jumpUnless: cond(w.slice(1)), negate: false, to: top.next });
          break;
        case '${Else}':
          ops.push({ goto: top.end });
          place(top.next);
          top.next = label();
          break;
        case '${EndIf}':
          if (stack.pop()?.kind !== 'if') throw new Error('${EndIf} without ${If}');
          place(top.next);
          place(top.end);
          break;
        case '${Do}': {
          const f = { kind: 'do', top: label(), end: label() };
          stack.push(f);
          place(f.top);
          break;
        }
        case '${Loop}':
          if (stack.pop()?.kind !== 'do') throw new Error('${Loop} without ${Do}');
          ops.push({ goto: top.top });
          place(top.end);
          break;
        case '${ExitDo}': case '${Continue}': {
          const d = [...stack].reverse().find((f) => f.kind === 'do');
          if (!d) throw new Error(`${w[0]} outside \${Do}`);
          ops.push({ goto: w[0] === '${ExitDo}' ? d.end : d.top });
          break;
        }
        default:
          ops.push({ words: w, line });
      }
    }
  };
  emit(all.get(name));
  if (stack.length) throw new Error(`unclosed ${stack.map((f) => f.kind).join(', ')} in ${name}`);
  return { ops, labels };
}

/**
 * Runs the macro `name` of `text`. fake: { params, env, silent, updated, installMode, running: Set of image names
 * (or a function of the attempt number), answers: [] (MessageBox answers, in order), cwd, fullPathFails }.
 * Returns { removed, prompts, aborted, sleeps, details, exec }.
 */
export function run(text, name, fake = {}) {
  const all = macros(text);
  if (!all.has(name)) throw new Error(`no macro ${name}`);
  const { ops, labels } = compile(all, name);
  const user = { LOCALAPPDATA: 'C:\\Users\\ana\\AppData\\Local', APPDATA: 'C:\\Users\\ana\\AppData\\Roaming' };
  const everyone = { LOCALAPPDATA: 'C:\\ProgramData', APPDATA: 'C:\\ProgramData' };
  const vars = { installMode: fake.installMode ?? 'CurrentUser', SYSDIR: 'C:\\Windows\\System32' };
  let context = fake.installMode === 'all' ? 'all' : 'current';
  let errors = false;
  const stack = [];
  const answers = [...(fake.answers ?? [])];
  const result = { removed: [], prompts: [], aborted: null, sleeps: 0, details: [], exec: [] };
  let finds = 0;

  const expand = (s) =>
    s.replace(/\$\{NSIS_MAX_STRLEN\}/g, '8192')
      .replace(/\$\\r/g, '\r').replace(/\$\\n/g, '\n').replace(/\$\\(["'`])/g, '$1')
      .replace(/\$(R?[0-9]|[A-Za-z_]+)/g, (m, v) => {
        if (/^R?[0-9]$/.test(v)) return vars[v] ?? '';
        if (v === 'LOCALAPPDATA' || v === 'APPDATA') return (context === 'all' ? everyone : user)[v];
        if (v in vars) return vars[v];
        throw new Error(`unknown variable ${m}`);
      });
  const reg = (w) => {
    if (!/^\$R?[0-9]$/.test(w)) throw new Error(`not a register: ${w}`);
    return w.slice(1);
  };
  const truth = (c) => {
    if (c.flag === '${Errors}') return errors;
    if (c.flag === '${isUpdated}') return !!fake.updated;
    if (c.flag === '${Silent}') return !!fake.silent;
    if (c.flag) throw new Error(`unknown condition ${c.flag}`);
    const a = expand(c.a);
    const b = expand(c.b);
    if (c.op === '==') return a.toLowerCase() === b.toLowerCase();
    if (c.op === '!=') return a.toLowerCase() !== b.toLowerCase();
    if (c.op === '<') return Number(a) < Number(b);
    throw new Error(`unknown comparison ${c.op}`);
  };

  let pc = 0;
  let steps = 0;
  while (pc < ops.length) {
    if (++steps > 10_000) throw new Error('runaway loop');
    const op = ops[pc];
    if (op.goto) { pc = labels.get(op.goto); continue; }
    if (op.jumpUnless) {
      const t = truth(op.jumpUnless) !== op.negate;
      pc = t ? pc + 1 : labels.get(op.to);
      continue;
    }
    const [cmd, ...a] = op.words;
    let next = pc + 1;
    switch (cmd) {
      case 'StrCpy': {
        let s = expand(a[1]);
        const max = a[2] === undefined || expand(a[2]) === '' ? null : Number(expand(a[2]));
        if (a[3] !== undefined) { const st = Number(expand(a[3])); s = st < 0 ? s.slice(s.length + st) : s.slice(st); }
        if (max !== null) s = max < 0 ? s.slice(0, s.length + max) : s.slice(0, max);
        vars[reg(a[0])] = s;
        break;
      }
      case 'StrLen': vars[reg(a[0])] = String(expand(a[1]).length); break;
      case 'IntOp':
        if (a[2] !== '+') throw new Error(`IntOp ${a[2]}`);
        vars[reg(a[0])] = String(Number(expand(a[1])) + Number(expand(a[3])));
        break;
      case 'Push': stack.push(expand(a[0])); break;
      case 'Pop': vars[reg(a[0])] = stack.pop(); break;
      case 'ClearErrors': errors = false; break;
      case '${GetParameters}': vars[reg(a[0])] = fake.params ?? ''; break;
      case '${GetOptions}': {
        const params = expand(a[0]);
        const at = params.toLowerCase().indexOf(expand(a[1]).toLowerCase());
        if (at < 0) { errors = true; vars[reg(a[2])] = ''; } else vars[reg(a[2])] = params.slice(at + a[1].length).split(' ')[0];
        break;
      }
      case 'ReadEnvStr': {
        const v = (fake.env ?? {})[a[1]];
        if (v === undefined) errors = true;
        vars[reg(a[0])] = v ?? '';
        break;
      }
      case 'SetShellVarContext': context = a[0]; break;
      case 'System::Call': {
        const m = /^kernel32::GetFullPathNameW\(w (R?[0-9]), i \$\{NSIS_MAX_STRLEN\}, w \.r([0-9]), i 0\) i \.r([0-9])$/.exec(a[0]);
        if (!m) throw new Error(`unknown System::Call ${a[0]}`);
        const full = fake.fullPathFails ? '' : path.win32.resolve(fake.cwd ?? 'C:\\Windows\\System32', vars[m[1]]);
        vars[m[2]] = full;
        vars[m[3]] = String(full.length);
        break;
      }
      case 'nsExec::Exec': {
        const command = expand(a[0]);
        result.exec.push(command);
        const image = /IMAGENAME eq ([^"]+)"/.exec(command)?.[1];
        const running = typeof fake.running === 'function' ? fake.running(finds++) : (fake.running ?? new Set());
        stack.push(image && running.has(image) ? '0' : '1');
        break;
      }
      case 'Sleep': result.sleeps++; break;
      case 'DetailPrint': result.details.push(expand(a[0])); break;
      case 'RMDir':
        if (a[0] !== '/r') throw new Error('RMDir without /r');
        result.removed.push(expand(a[1]));
        break;
      case 'Abort': result.aborted = expand(a[0] ?? ''); return result;
      case 'WriteRegStr': result.details.push(`reg ${a.map(expand).join(' ')}`); break;
      case 'MessageBox': {
        const text = expand(a[1]);
        const rest = a.slice(2);
        const sd = rest.indexOf('/SD');
        let answer;
        if (fake.silent) {
          if (sd < 0) throw new Error(`a MessageBox without /SD in a silent uninstall: ${text}`);
          answer = rest[sd + 1];
        } else {
          result.prompts.push(text);
          answer = answers.shift();
          if (!answer) throw new Error(`nobody answers: ${text}`);
        }
        // The answer's jump: an ID word followed by its target (not the ID that follows /SD).
        const at = rest.findIndex((x, i) => x === answer && !(sd >= 0 && i === sd + 1));
        if (at >= 0) {
          const target = rest[at + 1];
          if (!/^\+[0-9]+$/.test(target)) throw new Error(`jump ${target}`);
          next = pc + Number(target.slice(1));
        }
        break;
      }
      default: throw new Error(`the fake does not know: ${op.line}`);
    }
    pc = next;
  }
  return result;
}

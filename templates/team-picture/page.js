/*
 * THE TEAM PICTURE PAGE: the platform's template for a team's `team-picture` site.
 *
 * It reads the site's own data every few seconds through the helper script (`site.data.list`, which
 * reaches this site's `_api/` and nothing else) and draws:
 *   - the platform's live figures (tp-team, tp-items, tp-now, tp-runs, tp-spend, tp-waiting,
 *     tp-history), which nobody but the platform writes and the page never estimates;
 *   - the picture the team's Manager described (tp-picture), with draw.js;
 *   - an operational team's own figures (tp-ops), written by a plugin or a member;
 *   - the Concierge's layer (tp-settings): hidden sections, colours, extra sections.
 * Shapes: docs/sites.md, "The Team picture". Every fetched word is a text node. No inline script or
 * style, no storage, nothing fetched but this site's data.
 */
(function (root) {
  'use strict';

  var D = root.TeamPictureDraw;
  var el = D.el;
  var str = D.str;
  var list = D.list;

  var INTERVAL_MS = 5000;
  var HISTORY_SHOWN = 10;
  var RUNS_SHOWN = 10;
  var NOT_MEASURED = 'not measured';
  var NOT_DRAWN = 'The picture is drawn when the team plans its work.';
  var COLLECTIONS = ['tp-team', 'tp-items', 'tp-now', 'tp-runs', 'tp-spend', 'tp-waiting', 'tp-history', 'tp-picture', 'tp-settings', 'tp-ops'];
  var EXTRA = /^tp-extra-[a-z0-9](?:[a-z0-9-]{0,40}[a-z0-9])?$/;
  var COLOUR = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/;
  var COLOUR_KEYS = ['done', 'active', 'todo', 'unchanged', 'accent'];

  var state = { selected: null, lastKey: null, data: null, readError: null, timer: null, sheet: null, colours: null };

  // ------------------------------------------------------------------------------------------
  // Reading
  // ------------------------------------------------------------------------------------------

  function docs(rows) {
    return (Array.isArray(rows) ? rows : []).filter(function (row) {
      return row && typeof row.id === 'string' && row.doc && typeof row.doc === 'object';
    });
  }

  function byId(rows, id) {
    for (var i = 0; i < rows.length; i++) if (rows[i].id === id) return rows[i].doc;
    return null;
  }

  function extraCollections(settingsDoc) {
    return list(settingsDoc && settingsDoc.extraSections, 8)
      .map(function (section) { return section && section.collection; })
      .filter(function (name) { return typeof name === 'string' && EXTRA.test(name); });
  }

  /** Every collection the page shows, read through the helper script. */
  function read(site) {
    return Promise.all(COLLECTIONS.map(function (name) {
      return site.data.list(name).then(docs);
    })).then(function (answers) {
      var data = {};
      COLLECTIONS.forEach(function (name, index) { data[name] = answers[index]; });
      var settings = byId(data['tp-settings'], 'page');
      var extras = extraCollections(settings);
      return Promise.all(extras.map(function (name) {
        return site.data.list(name).then(docs, function () { return []; });
      })).then(function (extraAnswers) {
        data.extras = {};
        extras.forEach(function (name, index) { data.extras[name] = extraAnswers[index]; });
        return data;
      });
    });
  }

  // ------------------------------------------------------------------------------------------
  // Words for the platform's figures
  // ------------------------------------------------------------------------------------------

  function time(value) {
    var at = Date.parse(value);
    return isNaN(at) ? null : at;
  }

  function nowOf(data) {
    var team = byId(data['tp-team'], 'team');
    var at = team && time(team.generatedAt);
    return at === null || at === undefined ? Date.now() : at;
  }

  function span(ms) {
    if (ms === null || ms < 0 || !isFinite(ms)) return null;
    var minutes = Math.floor(ms / 60000);
    if (minutes < 1) return 'under a minute';
    if (minutes < 60) return minutes + ' min';
    var hours = Math.floor(minutes / 60);
    var rest = minutes % 60;
    return hours + ' h' + (rest ? ' ' + rest + ' min' : '');
  }

  function clock(value) {
    var at = time(value);
    if (at === null) return '';
    return new Date(at).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  }

  function day(value) {
    var at = time(value);
    if (at === null) return '';
    return new Date(at).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
  }

  function count(value) {
    return typeof value === 'number' && isFinite(value) ? value.toLocaleString() : NOT_MEASURED;
  }

  function memberWords(name) {
    // "DeveloperInes" reads "Developer Ines".
    return str(name, 80).replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  /** "Developer Ines, 34 min in, last progress 2 min ago: Running the tests". */
  function describeRun(run, now) {
    var parts = [memberWords(run.member)];
    var ran = span(now - time(run.startedAt));
    if (ran) parts.push(ran + ' in');
    var progress = run.lastProgress && typeof run.lastProgress === 'object' ? run.lastProgress : null;
    if (progress && time(progress.at) !== null) {
      var ago = span(now - time(progress.at));
      var text = str(progress.text, 120);
      parts.push('last progress ' + (ago ? ago + ' ago' : 'just now') + (text ? ': ' + text : ''));
    } else {
      parts.push('no progress reported yet');
    }
    return parts.join(', ');
  }

  function liveIndex(nowRows) {
    var live = { byCard: {}, byMember: {}, runs: [] };
    nowRows.forEach(function (row) {
      var run = row.doc;
      if (typeof run.member !== 'string') return;
      live.runs.push(run);
      live.byMember[run.member] = run;
      if (run.card && typeof run.card.id === 'string') live.byCard[run.card.id] = run;
    });
    return live;
  }

  /** A piece's status from its cards in the item's tp-items document, or null when none is found. */
  function cardStatusFor(item) {
    var cards = {};
    list(item && item.cards, 200).forEach(function (card) {
      if (card && typeof card.id === 'string') cards[card.id] = card;
    });
    return function (ids) {
      var found = (ids || []).map(function (id) { return cards[id]; }).filter(Boolean);
      if (found.length === 0) return null;
      if (found.some(function (card) { return card.status === 'running' || card.lane === 'doing'; })) return 'active';
      if (found.every(function (card) { return card.lane === 'done' || card.status === 'done'; })) return 'done';
      return 'todo';
    };
  }

  // ------------------------------------------------------------------------------------------
  // The Concierge's layer: colours through the template's own stylesheet, never inline.
  // ------------------------------------------------------------------------------------------

  function kindOrder(data, picture) {
    var settings = byId(data['tp-settings'], 'page') || {};
    var ops = byId(data['tp-ops'], 'today');
    var order = [];
    function add(name) { if (typeof name === 'string' && name && order.indexOf(name) < 0 && order.length < 24) order.push(name); }
    list(settings.kindOrder, 24).forEach(add);
    list(picture && picture.kinds, 24).forEach(function (kind) { if (kind) add(kind.id); });
    if (ops && ops.counts && typeof ops.counts === 'object') Object.keys(ops.counts).forEach(add);
    list(ops && ops.runs, 400).forEach(function (run) { if (run) add(run.kind); });
    list(ops && ops.items, 50).forEach(function (item) { if (item) add(item.kind); });
    return order;
  }

  /**
   * The Concierge's colours, as ONE constructed stylesheet adopted by the document: CSSOM, never a
   * <style> element or a style attribute, and only `#rgb`/`#rrggbb` values reach it. (The page's own
   * picture.css cannot be edited from script: the page runs in an opaque origin, so its own files are
   * cross-origin to it.) A browser without constructed stylesheets keeps the template's colours.
   */
  function applyColours(data, kinds) {
    var settings = byId(data['tp-settings'], 'page') || {};
    var colours = settings.colours && typeof settings.colours === 'object' ? settings.colours : {};
    var rules = [];
    var declarations = [];
    COLOUR_KEYS.forEach(function (key) {
      if (COLOUR.test(colours[key])) declarations.push('--tp-' + key + ':' + colours[key]);
    });
    if (declarations.length) rules.push(':root{' + declarations.join(';') + '}');
    kinds.forEach(function (name, index) {
      var colour = colours['kind:' + name];
      if (COLOUR.test(colour)) rules.push('.tp-k-' + index + '{--tp-kind:' + colour + '}');
    });
    var text = rules.join('\n');
    if (text === state.colours) return;
    state.colours = text;
    try {
      if (!state.sheet) {
        if (typeof root.CSSStyleSheet !== 'function' || !('adoptedStyleSheets' in document)) return;
        state.sheet = new root.CSSStyleSheet();
        document.adoptedStyleSheets = document.adoptedStyleSheets.concat([state.sheet]);
      }
      state.sheet.replaceSync(text);
    } catch (error) {
      // Colours are a layer: without them the page still draws in the template's own.
    }
  }

  // ------------------------------------------------------------------------------------------
  // Sections
  // ------------------------------------------------------------------------------------------

  function section(id, title, hidden) {
    if (hidden[id]) return null;
    var node = el('section', 'tp-section');
    node.setAttribute('data-section', id);
    if (title) node.appendChild(el('h2', null, title));
    return node;
  }

  function note(text) {
    return el('p', 'tp-note', text);
  }

  function nowSection(data, hidden, now, live) {
    var node = section('now', 'Now', hidden);
    if (!node) return null;
    if (live.runs.length === 0) {
      node.appendChild(note('Nobody is working right now.'));
      return node;
    }
    var rows = el('ul', 'tp-now-list');
    live.runs
      .slice()
      .sort(function (a, b) { return (time(a.startedAt) || 0) - (time(b.startedAt) || 0); })
      .forEach(function (run) {
        var row = el('li', 'tp-now-row');
        row.setAttribute('data-now-member', str(run.member, 80));
        row.appendChild(el('span', 'tp-dot tp-dot-amber', '●'));
        var what = el('span', 'tp-now-what');
        var card = run.card && str(run.card.title, 120);
        what.appendChild(el('strong', null, card || (run.kind === 'plugin' ? 'A scheduled run' : 'Working')));
        if (run.item && typeof run.item === 'string') what.appendChild(el('span', 'tp-chip', run.item));
        row.appendChild(what);
        row.appendChild(el('span', 'tp-now-who', describeRun(run, now)));
        rows.appendChild(row);
      });
    node.appendChild(rows);
    return node;
  }

  var WAITING_WORDS = { decision: 'needs a decision', blocked: 'is blocked', held: 'is held' };

  function waitingSection(data, hidden, now) {
    var node = section('waiting', 'Waiting on a person', hidden);
    if (!node) return null;
    var rows = data['tp-waiting'].map(function (row) { return row.doc; });
    if (rows.length === 0) {
      node.appendChild(note('Nothing waits on a person.'));
      return node;
    }
    var ul = el('ul', 'tp-list');
    rows.sort(function (a, b) { return (time(a.since) || 0) - (time(b.since) || 0); }).slice(0, 20).forEach(function (wait) {
      var li = el('li', 'tp-wait tp-wait-' + (WAITING_WORDS[wait.kind] ? wait.kind : 'other'));
      var head = memberWords(wait.member) || 'The team';
      li.appendChild(el('strong', null, head + ' ' + (WAITING_WORDS[wait.kind] || 'waits')));
      var since = span(now - time(wait.since));
      if (since) li.appendChild(el('span', 'tp-muted', ' for ' + since));
      var text = str(wait.text, 300);
      if (text) li.appendChild(el('div', 'tp-wait-text', text));
      ul.appendChild(li);
    });
    node.appendChild(ul);
    return node;
  }

  function tokensWords(w) {
    if (!w || w.measured !== true || typeof w.tokens !== 'number') return NOT_MEASURED;
    var words = count(w.tokens) + ' tokens';
    if (typeof w.input === "number" && typeof w.output === "number") {
      words += ' (' + count(w.input) + " input, " + count(w.output) + ' output)';
    }
    if (typeof w.runsNotMeasured === "number" && w.runsNotMeasured > 0) {
      words += '; ' + w.runsNotMeasured + (w.runsNotMeasured === 1 ? ' run' : ' runs') + ' not measured';
    }
    return words;
  }

  function spendSection(data, hidden) {
    var node = section('spend', 'Spend', hidden);
    if (!node) return null;
    var team = byId(data['tp-spend'], 'team');
    if (!team) {
      node.appendChild(note('Spend: ' + NOT_MEASURED + '.'));
      return node;
    }
    var words = str(team.words, 300);
    if (words) node.appendChild(el('p', 'tp-muted', words));
    var grid = el('dl', 'tp-spend');
    [['Today', team.today], ['Last 7 days', team.last7Days]].forEach(function (pair) {
      grid.appendChild(el('dt', null, pair[0]));
      var dd = el('dd', null, tokensWords(pair[1]));
      dd.setAttribute('data-spend', pair[0]);
      grid.appendChild(dd);
    });
    node.appendChild(grid);
    var eec = team.estimatedExecutionCost;
    if (eec && typeof eec === 'object' && str(eec.text, 300)) {
      node.appendChild(el('p', 'tp-muted tp-eec', str(eec.text, 300)));
    }
    return node;
  }

  var OUTCOME_WORDS = { running: 'running', completed: 'completed', handedBack: 'handed back', blocked: 'blocked', failed: 'failed', interrupted: 'interrupted' };

  function runsSection(data, hidden, now) {
    var node = section('runs', 'Recent runs', hidden);
    if (!node) return null;
    var runs = data['tp-runs'].map(function (row) { return row.doc; })
      .sort(function (a, b) { return (time(b.startedAt) || 0) - (time(a.startedAt) || 0); })
      .slice(0, RUNS_SHOWN);
    if (runs.length === 0) {
      node.appendChild(note('No runs yet.'));
      return node;
    }
    var table = el('table', 'tp-table');
    var head = el('tr');
    ['Member', 'Started', 'Ran', 'Outcome', 'Tokens'].forEach(function (h) { head.appendChild(el('th', null, h)); });
    var thead = el('thead');
    thead.appendChild(head);
    table.appendChild(thead);
    var body = el('tbody');
    runs.forEach(function (run) {
      var tr = el('tr', run.outcome === 'running' ? 'tp-run-running' : '');
      tr.appendChild(el('td', null, memberWords(run.member)));
      tr.appendChild(el('td', null, clock(run.startedAt)));
      var end = run.outcome === 'running' ? now : time(run.endedAt);
      tr.appendChild(el('td', null, span(end - time(run.startedAt)) || '-'));
      var outcome = el('td', 'tp-outcome tp-o-' + (OUTCOME_WORDS[run.outcome] ? run.outcome : 'other'), OUTCOME_WORDS[run.outcome] || str(run.outcome, 40));
      tr.appendChild(outcome);
      var tokens = run.tokens && run.tokens.measured === true && typeof run.tokens.input === 'number' && typeof run.tokens.output === 'number'
        ? count(run.tokens.input + run.tokens.output)
        : NOT_MEASURED;
      tr.appendChild(el('td', null, tokens));
      body.appendChild(tr);
    });
    table.appendChild(body);
    node.appendChild(table);
    return node;
  }

  // ---- A build item -------------------------------------------------------------------------

  /** The progress bar: done, then a striped (moving) segment for what is in flight, then the rest. */
  function progressBar(progress, anyLive) {
    var total = progress && typeof progress.total === 'number' ? progress.total : 0;
    var bar = D.svg('svg', { viewBox: '0 0 1000 12', preserveAspectRatio: 'none', role: 'img', 'aria-label': 'Progress' }, 'tp-bar');
    bar.appendChild(D.svg('rect', { x: 0, y: 0, width: 1000, height: 12, rx: 6 }, 'tp-bar-track'));
    if (total > 0) {
      var done = Math.max(0, Math.min(total, progress.done || 0));
      var running = Math.max(0, Math.min(total - done, progress.running || 0));
      var doneWidth = Math.round((done / total) * 1000);
      var runWidth = Math.round((running / total) * 1000);
      if (doneWidth > 0) bar.appendChild(D.svg('rect', { x: 0, y: 0, width: doneWidth, height: 12 }, 'tp-bar-done'));
      if (runWidth > 0) {
        var group = D.svg('g', { 'data-bar-now': 'true' });
        group.appendChild(D.svg('rect', { x: doneWidth, y: 0, width: runWidth, height: 12 }, 'tp-bar-active'));
        // Stripes inside an inner svg, which clips them to the segment without an id.
        var clip = D.svg('svg', { x: doneWidth, y: 0, width: runWidth, height: 12, viewBox: '0 0 ' + runWidth + ' 12', preserveAspectRatio: 'none' });
        var stripes = D.svg('g', null, anyLive ? 'tp-stripes tp-moving' : 'tp-stripes');
        for (var x = -40; x < runWidth + 40; x += 20) {
          stripes.appendChild(D.svg('polygon', { points: x + ',12 ' + (x + 10) + ',12 ' + (x + 22) + ',0 ' + (x + 12) + ',0' }, 'tp-stripe'));
        }
        clip.appendChild(stripes);
        group.appendChild(clip);
        bar.appendChild(group);
      }
    }
    return bar;
  }

  function legend() {
    var node = el('div', 'tp-legend');
    [['done', 'done'], ['active', 'in progress'], ['todo', 'not started'], ['unchanged', 'unchanged']].forEach(function (pair) {
      var item = el('span', 'tp-legend-item');
      item.appendChild(el('span', 'tp-swatch tp-sw-' + pair[0]));
      item.appendChild(document.createTextNode(pair[1]));
      node.appendChild(item);
    });
    return node;
  }

  function progressSection(item, hidden, live, now, itemSpend) {
    var node = section('progress', null, hidden);
    if (!node) return null;
    node.classList.add('tp-progress');
    var left = el('div', 'tp-progress-main');
    left.appendChild(el('div', 'tp-label', 'Progress'));
    var progress = item && item.progress && typeof item.progress === 'object' ? item.progress : null;
    var runsHere = live.runs.filter(function (run) { return item && run.item === item.key; });
    left.appendChild(progressBar(progress, runsHere.length > 0));
    var words = el('div', 'tp-progress-words');
    if (progress && typeof progress.total === 'number' && progress.total > 0) {
      words.appendChild(document.createTextNode(count(progress.done) + ' of ' + count(progress.total) + ' cards done.'));
      if (progress.blocked > 0) words.appendChild(document.createTextNode(' ' + progress.blocked + ' blocked.'));
      if (progress.failed > 0) words.appendChild(document.createTextNode(' ' + progress.failed + ' failed.'));
    } else {
      words.appendChild(document.createTextNode('No cards yet.'));
    }
    runsHere.forEach(function (run) {
      words.appendChild(document.createTextNode(' '));
      words.appendChild(el('span', 'tp-dot tp-dot-amber', '●'));
      words.appendChild(document.createTextNode(' Now: '));
      words.appendChild(el('strong', null, (run.card && str(run.card.title, 120)) || 'working'));
      words.appendChild(document.createTextNode(' (' + describeRun(run, now) + ').'));
    });
    left.appendChild(words);
    if (itemSpend) {
      var spent = el('div', 'tp-muted tp-item-spend', 'Tokens on this item: ' + tokensWords(itemSpend.total));
      left.appendChild(spent);
    }
    node.appendChild(left);
    node.appendChild(legend());
    return node;
  }

  function figure(svgDrawn, caption, extraClass) {
    var box = el('div', 'tp-mockup' + (extraClass ? ' ' + extraClass : ''));
    if (caption) box.appendChild(el('div', 'tp-mockup-header', caption));
    var body = el('div', 'tp-mockup-body tp-graph-body');
    body.appendChild(svgDrawn);
    box.appendChild(body);
    return box;
  }

  function buildPanel(panel, item, picture, hidden, live, now, data) {
    var itemSpend = item ? byId(data['tp-spend'], item.key) : null;
    append(panel, progressSection(item, hidden, live, now, itemSpend));
    var options = { live: live, describeRun: function (run) { return describeRun(run, now); }, cardStatus: cardStatusFor(item) };
    if (!picture) {
      panel.appendChild(note(NOT_DRAWN));
      return;
    }
    var summary = str(picture.summary, 600);
    if (summary) panel.appendChild(el('p', 'tp-summary', summary));

    var arch = D.diagram(picture.architecture, options);
    if (arch) {
      var a = section('architecture', '1. Architecture: where this item sits', hidden);
      if (a) {
        a.appendChild(figure(arch.svg, arch.caption));
        panel.appendChild(a);
      }
    }

    var screens = list(picture.screens, D.LIMITS.screens).map(function (s) { return D.screen(s, options, false); }).filter(Boolean);
    var work = list(picture.work, D.LIMITS.screens).map(function (s) { return D.screen(s, options, true); }).filter(Boolean);
    if (screens.length || work.length) {
      var s = section('screens', '2. Screens it adds or changes', hidden);
      if (s) {
        var grid = el('div', 'tp-split');
        screens.concat(work).forEach(function (card) { grid.appendChild(card); });
        s.appendChild(grid);
        panel.appendChild(s);
      }
    }

    var flows = list(picture.flows, 8).map(function (f) { return D.flow(f, options); }).filter(Boolean);
    if (flows.length) {
      var f = section('flows', '3. Logic it changes', hidden);
      if (f) {
        flows.forEach(function (drawn) {
          var caption = [drawn.title, drawn.caption].filter(Boolean).join(': ');
          f.appendChild(figure(drawn.svg, caption));
        });
        panel.appendChild(f);
      }
    }
  }

  // ---- An operational team -------------------------------------------------------------------

  function kindLabel(picture, name) {
    var found = list(picture && picture.kinds, 24).filter(function (kind) { return kind && kind.id === name; })[0];
    return (found && str(found.label, 80)) || name;
  }

  function kindNote(picture, name) {
    var found = list(picture && picture.kinds, 24).filter(function (kind) { return kind && kind.id === name; })[0];
    return found ? str(found.note, 80) : '';
  }

  function kindClassFor(kinds) {
    return function (name) {
      var index = kinds.indexOf(name);
      return index < 0 ? '' : 'tp-kc tp-k-' + index;
    };
  }

  function figuresSection(ops, picture, kinds, hidden, data, now) {
    var node = section('figures', null, hidden);
    if (!node) return null;
    node.classList.add('tp-kpis');
    var counts = ops && ops.counts && typeof ops.counts === 'object' ? ops.counts : null;
    var today = new Date(now).toISOString().slice(0, 10);
    var dated = ops && typeof ops.date === 'string' ? ops.date : null;

    var first = el('div', 'tp-kpi');
    first.appendChild(el('div', 'tp-label', dated && dated !== today ? 'On ' + dated : 'Today'));
    var measured = counts ? Object.keys(counts).filter(function (k) { return typeof counts[k] === 'number'; }) : [];
    var sum = measured.reduce(function (acc, k) { return acc + counts[k]; }, 0);
    first.appendChild(el('div', 'tp-kpi-n', measured.length ? sum.toLocaleString() : NOT_MEASURED));
    var runsToday = data['tp-runs'].filter(function (row) { return typeof row.doc.startedAt === 'string' && row.doc.startedAt.slice(0, 10) === today; }).length;
    first.appendChild(el('div', 'tp-muted', 'items sorted, in ' + runsToday + (runsToday === 1 ? ' run' : ' runs') + ' today'));
    node.appendChild(first);

    kinds.forEach(function (name, index) {
      var kpi = el('div', 'tp-kpi tp-k-' + index);
      kpi.setAttribute('data-kind', name);
      kpi.appendChild(el('div', 'tp-label', kindLabel(picture, name)));
      var value = counts && typeof counts[name] === 'number' ? counts[name].toLocaleString() : NOT_MEASURED;
      kpi.appendChild(el('div', 'tp-kpi-n tp-kind-text', value));
      var words = kindNote(picture, name);
      if (words) kpi.appendChild(el('div', 'tp-muted', words));
      node.appendChild(kpi);
    });

    var message = ops && ops.lastMessage && typeof ops.lastMessage === 'object' ? ops.lastMessage : null;
    var sent = el('div', 'tp-kpi');
    sent.appendChild(el('div', 'tp-label', 'Last message'));
    sent.appendChild(el('div', 'tp-kpi-n', message ? clock(message.sentAt) || 'sent' : 'none yet'));
    if (message && str(message.to, 80)) sent.appendChild(el('div', 'tp-muted', 'to ' + str(message.to, 80)));
    node.appendChild(sent);
    return node;
  }

  function runStrip(ops, kinds, data) {
    var runs = list(ops && ops.runs, 400);
    var running = data['tp-runs'].some(function (row) { return row.doc.outcome === 'running'; });
    var bars = runs.length + (running ? 1 : 0);
    var width = Math.max(200, bars * 8);
    var strip = D.svg('svg', { viewBox: '0 0 ' + width + ' 40', preserveAspectRatio: 'xMinYMax meet', role: 'img', 'aria-label': 'Runs today' }, 'tp-strip');
    var tallest = Math.max(1, kinds.length);
    runs.forEach(function (run, index) {
      var rank = run && typeof run.kind === 'string' ? kinds.indexOf(run.kind) : -1;
      var height = rank < 0 ? 6 : 12 + Math.round(((tallest - rank) / tallest) * 26);
      var bar = D.svg('rect', { x: index * 8, y: 40 - height, width: 6, height: height }, rank < 0 ? 'tp-strip-quiet' : 'tp-kc tp-k-' + rank);
      bar.setAttribute('data-run-kind', rank < 0 ? '' : run.kind);
      strip.appendChild(bar);
    });
    if (running) {
      var now = D.svg('rect', { x: runs.length * 8, y: 14, width: 6, height: 26 }, 'tp-strip-running tp-dot');
      now.setAttribute('data-run-kind', 'running');
      strip.appendChild(now);
    }
    return strip;
  }

  function didTodaySection(ops, picture, kinds, hidden, data) {
    var node = section('didToday', 'What it did', hidden);
    if (!node) return null;
    var grid = el('div', 'tp-split');

    var latest = el('div', 'tp-mockup');
    latest.appendChild(el('div', 'tp-mockup-header', 'Latest items, newest first'));
    var rows = el('div', 'tp-mockup-body tp-rows');
    var items = list(ops && ops.items, 50).filter(function (item) { return item && typeof item === 'object'; })
      .sort(function (a, b) { return (time(b.at) || 0) - (time(a.at) || 0); });
    if (items.length === 0) rows.appendChild(note('Nothing yet today.'));
    items.forEach(function (item) {
      var sorting = typeof item.kind !== 'string';
      var row = el('div', 'tp-row' + (sorting ? ' tp-row-sorting' : ''));
      if (sorting) {
        row.appendChild(el('span', 'tp-dot tp-dot-amber', '●'));
      } else {
        var rank = kinds.indexOf(item.kind);
        row.appendChild(el('span', 'tp-tag tp-k-' + (rank < 0 ? 'x' : rank), kindLabel(picture, item.kind)));
      }
      row.appendChild(el('span', 'tp-row-title', str(item.title, 160)));
      row.appendChild(el('span', 'tp-muted', sorting ? 'sorting…' : clock(item.at)));
      rows.appendChild(row);
    });
    latest.appendChild(rows);
    grid.appendChild(latest);

    var right = el('div', 'tp-mockup');
    right.appendChild(el('div', 'tp-mockup-header', 'Last message sent'));
    var body = el('div', 'tp-mockup-body');
    var message = ops && ops.lastMessage && typeof ops.lastMessage === 'object' ? ops.lastMessage : null;
    if (message && typeof message.text === 'string') {
      var bubble = el('div', 'tp-bubble');
      // As the recipient saw it: line breaks kept, every line a text node.
      message.text.slice(0, 1600).split('\n').forEach(function (line, index) {
        if (index) bubble.appendChild(el('br'));
        bubble.appendChild(document.createTextNode(line));
      });
      body.appendChild(bubble);
      var to = str(message.to, 80);
      body.appendChild(el('div', 'tp-muted tp-small', (to ? 'To ' + to + ', ' : '') + (clock(message.sentAt) ? 'sent ' + clock(message.sentAt) : '')));
    } else {
      body.appendChild(note('No message sent yet.'));
    }
    if (!hidden.runs) {
      body.appendChild(el('div', 'tp-label tp-strip-label', 'Runs today (one bar a run)'));
      body.appendChild(runStrip(ops, kinds, data));
      body.appendChild(el('div', 'tp-muted tp-small', 'grey: nothing new · colour: the most urgent kind it found · amber: running now'));
    }
    right.appendChild(body);
    grid.appendChild(right);
    node.appendChild(grid);
    return node;
  }

  function operationalPanel(panel, picture, hidden, live, now, data) {
    var ops = byId(data['tp-ops'], 'today');
    var kinds = kindOrder(data, picture);
    var options = { live: live, describeRun: function (run) { return describeRun(run, now); }, kindClass: kindClassFor(kinds) };
    append(panel, figuresSection(ops, picture, kinds, hidden, data, now));
    if (!picture) panel.appendChild(note(NOT_DRAWN));

    var how = picture && D.diagram(picture.howItWorks, options);
    if (how) {
      var h = section('howItWorks', '1. How it works', hidden);
      if (h) {
        var running = live.runs[0];
        var caption = how.caption || 'The team\'s pieces, and where the run in progress is';
        h.appendChild(figure(how.svg, running ? caption + ' — now: ' + describeRun(running, now) : caption));
        panel.appendChild(h);
      }
    }
    append(panel, didTodaySection(ops, picture, kinds, hidden, data));
    var rule = picture && D.flow(picture.rule, options);
    if (rule) {
      var r = section('rule', '3. The rule it follows', hidden);
      if (r) {
        r.appendChild(figure(rule.svg, [rule.title, rule.caption].filter(Boolean).join(': ')));
        panel.appendChild(r);
      }
    }
  }

  // ---- Extra sections (the Concierge's) and history -----------------------------------------

  function extraSection(definition, data, hidden) {
    if (!definition || typeof definition.id !== 'string' || !/^[a-z0-9-]{1,40}$/.test(definition.id)) return null;
    var node = section(definition.id, str(definition.title, 80) || definition.id, hidden);
    if (!node) return null;
    node.classList.add('tp-extra');
    D.regions(node, definition.regions);
    if (typeof definition.collection === 'string' && EXTRA.test(definition.collection)) {
      var rows = (data.extras && data.extras[definition.collection]) || [];
      rows.slice().sort(function (a, b) { return (time(b.updatedAt) || 0) - (time(a.updatedAt) || 0); }).slice(0, 50).forEach(function (row) {
        var card = el('div', 'tp-extra-row');
        if (Array.isArray(row.doc.regions)) {
          D.regions(card, row.doc.regions);
        } else {
          var title = str(row.doc.title, 160);
          var text = str(row.doc.text, 600);
          if (title) card.appendChild(el('strong', null, title));
          if (text) card.appendChild(el('p', 'tp-m-text', text));
        }
        if (card.childNodes.length) node.appendChild(card);
      });
    }
    return node;
  }

  function historySection(data, hidden) {
    var node = section('history', 'Finished', hidden);
    if (!node) return null;
    var rows = data['tp-history'].map(function (row) { return row.doc; })
      .sort(function (a, b) { return (time(b.finishedAt) || 0) - (time(a.finishedAt) || 0); })
      .slice(0, HISTORY_SHOWN);
    if (rows.length === 0) {
      node.appendChild(note('Nothing finished yet.'));
      return node;
    }
    rows.forEach(function (item) {
      var details = el('details', 'tp-history-item');
      details.setAttribute('data-history', str(item.key, 64));
      var summary = el('summary');
      if (item.key) summary.appendChild(el('span', 'tp-chip', str(item.key, 64)));
      summary.appendChild(document.createTextNode(' ' + (str(item.title, 160) || 'Untitled') + ' '));
      summary.appendChild(el('span', 'tp-muted', 'finished ' + day(item.finishedAt)));
      details.appendChild(summary);
      // Drawn the first time it is opened, from the picture kept at finish, with nothing live: still.
      details.addEventListener('toggle', function () {
        if (!details.open || details.getAttribute('data-drawn')) return;
        details.setAttribute('data-drawn', 'true');
        var panel = el('div', 'tp-history-panel');
        drawItem(panel, { key: item.key, progress: item.progress, cards: [] }, item.picture && typeof item.picture === 'object' ? item.picture : null, {}, liveIndex([]), Date.now(), emptyData(), item.picture && item.picture.kind === 'operational');
        details.appendChild(panel);
      });
      node.appendChild(details);
    });
    return node;
  }

  function emptyData() {
    var data = { extras: {} };
    COLLECTIONS.forEach(function (name) { data[name] = []; });
    return data;
  }

  function append(parent, child) {
    if (child) parent.appendChild(child);
  }

  function drawItem(panel, item, picture, hidden, live, now, data, operational) {
    if (operational) operationalPanel(panel, picture, hidden, live, now, data);
    else buildPanel(panel, item, picture, hidden, live, now, data);
  }

  // ------------------------------------------------------------------------------------------
  // The page
  // ------------------------------------------------------------------------------------------

  /** One tab per open item, work in flight first, newest first after that; `ongoing` last. */
  function tabsOf(data, live) {
    var items = data['tp-items'].map(function (row) {
      var item = row.doc;
      if (typeof item.key !== 'string') item.key = row.id;
      return item;
    });
    var busy = {};
    live.runs.forEach(function (run) { if (typeof run.item === 'string') busy[run.item] = true; });
    items.sort(function (a, b) {
      if (!!busy[a.key] !== !!busy[b.key]) return busy[a.key] ? -1 : 1;
      return (time(b.startedAt) || 0) - (time(a.startedAt) || 0);
    });
    var tabs = items.map(function (item) {
      var picture = byId(data['tp-picture'], item.key);
      return {
        key: item.key,
        title: (picture && str(picture.title, 80)) || str(item.title, 80) || item.key,
        item: item,
        picture: picture,
        operational: !!(picture && picture.kind === 'operational'),
        busy: !!busy[item.key],
      };
    });
    var ongoing = byId(data['tp-picture'], 'ongoing');
    var ops = byId(data['tp-ops'], 'today');
    if ((ongoing && ongoing.kind !== 'build') || (ops && tabs.length === 0)) {
      tabs.push({
        key: 'ongoing',
        title: (ongoing && str(ongoing.title, 80)) || 'What it runs',
        item: null,
        picture: ongoing,
        operational: true,
        busy: live.runs.some(function (run) { return run.item === 'ongoing' || !run.item; }),
      });
    }
    return tabs;
  }

  function hiddenOf(data) {
    var settings = byId(data['tp-settings'], 'page') || {};
    var hidden = {};
    list(settings.hidden, 40).forEach(function (id) { if (typeof id === 'string') hidden[id] = true; });
    return hidden;
  }

  function render(data) {
    var main = document.getElementById('tp-main');
    if (!main) return;
    var now = nowOf(data);
    var live = liveIndex(data['tp-now']);
    var hidden = hiddenOf(data);
    var tabs = tabsOf(data, live);
    var settings = byId(data['tp-settings'], 'page') || {};

    var team = byId(data['tp-team'], 'team');
    var name = document.getElementById('tp-team');
    if (name) name.textContent = team ? str(team.label, 80) || str(team.team, 80) : '';

    var selected = tabs.filter(function (tab) { return tab.key === state.selected; })[0] || tabs[0] || null;
    state.selected = selected ? selected.key : null;
    applyColours(data, kindOrder(data, selected && selected.picture));

    var blocks = [];
    blocks.push(nowSection(data, hidden, now, live));
    blocks.push(waitingSection(data, hidden, now));

    var work = el('section', 'tp-work');
    work.setAttribute('data-section', 'work');
    if (tabs.length === 0) {
      work.appendChild(note('No work is open.'));
      work.appendChild(note(NOT_DRAWN));
    } else {
      var bar = el('div', 'tp-tabs');
      bar.setAttribute('role', 'tablist');
      tabs.forEach(function (tab) {
        var button = el('button', 'tp-tab' + (tab === selected ? ' tp-tab-on' : ''));
        button.type = 'button';
        button.setAttribute('role', 'tab');
        button.setAttribute('aria-selected', tab === selected ? 'true' : 'false');
        button.setAttribute('data-tab', tab.key);
        if (tab.busy) button.appendChild(el('span', 'tp-dot tp-dot-amber', '● '));
        if (tab.item && tab.key !== tab.title) button.appendChild(el('span', 'tp-chip', tab.key));
        button.appendChild(document.createTextNode(' ' + tab.title));
        button.addEventListener('click', function () {
          state.selected = tab.key;
          state.lastKey = null;
          render(state.data);
        });
        bar.appendChild(button);
      });
      work.appendChild(bar);
      var panel = el('div', 'tp-panel');
      panel.setAttribute('role', 'tabpanel');
      panel.setAttribute('data-panel', selected.key);
      panel.setAttribute('data-kind', selected.operational ? 'operational' : 'build');
      drawItem(panel, selected.item, selected.picture, hidden, live, now, data, selected.operational);
      work.appendChild(panel);
    }
    blocks.push(work);
    blocks.push(runsSection(data, hidden, now));
    blocks.push(spendSection(data, hidden));

    // The Concierge's extra sections, after the section each names (default: before the history).
    var extras = list(settings.extraSections, 8);
    var placed = blocks.filter(Boolean);
    extras.forEach(function (definition) {
      var node = extraSection(definition, data, hidden);
      if (!node) return;
      var after = definition && typeof definition.after === 'string' ? definition.after : null;
      var at = -1;
      placed.forEach(function (block, index) {
        if (after && block.getAttribute('data-section') === after) at = index;
      });
      if (at < 0 && after) {
        // A section inside the item's panel: place it after the work.
        placed.forEach(function (block, index) { if (block.getAttribute('data-section') === 'work' && block.querySelector('[data-section="' + after + '"]')) at = index; });
      }
      if (at < 0) placed.push(node);
      else placed.splice(at + 1, 0, node);
    });
    append2(placed, historySection(data, hidden));

    main.replaceChildren.apply(main, placed);
  }

  function append2(array, node) {
    if (node) array.push(node);
  }

  function status(text) {
    var line = document.getElementById('tp-status');
    if (!line) return;
    line.textContent = text;
    line.hidden = !text;
  }

  /** One read and, when anything changed, one redraw. Resolves either way. */
  function refresh(site) {
    return read(site).then(function (data) {
      state.readError = null;
      status('');
      var key = JSON.stringify(data);
      state.data = data;
      if (key !== state.lastKey) {
        state.lastKey = key;
        render(data);
      }
    }, function (error) {
      state.readError = error;
      var words = error && typeof error.message === 'string' ? error.message : 'the read failed';
      status('Could not read the team\'s figures (' + words + '). ' + (state.data ? 'Showing what was read last.' : 'Trying again.'));
      if (!state.data) {
        state.data = emptyData();
        render(state.data);
      }
    });
  }

  function start(site, interval) {
    var every = interval || INTERVAL_MS;
    function tick() {
      refresh(site).then(function () { state.timer = root.setTimeout(tick, every); });
    }
    tick();
  }

  function stop() {
    if (state.timer) root.clearTimeout(state.timer);
    state.timer = null;
  }

  function reset() {
    stop();
    state.selected = null;
    state.lastKey = null;
    state.data = null;
    state.readError = null;
    state.colours = null;
    if (state.sheet) state.sheet.replaceSync('');
  }

  root.TeamPicture = { refresh: refresh, render: render, start: start, stop: stop, reset: reset, INTERVAL_MS: INTERVAL_MS, NOT_MEASURED: NOT_MEASURED, NOT_DRAWN: NOT_DRAWN };

  if (root.site && root.site.data) start(root.site);
})(window);

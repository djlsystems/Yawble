/*
 * THE TEAM PICTURE'S RENDERER: diagrams, flow charts and screen mockups drawn from the short
 * descriptions a team's Manager writes into `tp-picture` (shapes: docs/sites.md, "The Team picture").
 *
 * Every word an agent wrote reaches the page as a text node (`textContent`, SVG <text>), never as
 * markup, and nothing here sets a style: colour comes from classes in picture.css, positions from
 * SVG geometry attributes. Nothing is fetched. Unknown fields are ignored and missing ones skipped,
 * so a half-written description draws what it has.
 */
(function (root) {
  'use strict';

  var SVG = 'http://www.w3.org/2000/svg';
  var STATUSES = ['done', 'active', 'todo', 'unchanged'];
  var LIMITS = { label: 80, detail: 80, text: 600, nodes: 40, edges: 80, steps: 30, links: 60, screens: 12, regions: 12, rows: 20, columns: 8, items: 12, lines: 12 };
  var ID = /^[A-Za-z0-9._-]{1,64}$/;
  var uid = 0;

  function str(value, max) {
    if (typeof value === 'number' && isFinite(value)) value = String(value);
    if (typeof value !== 'string') return '';
    var text = value.trim();
    return text.length > max ? text.slice(0, max - 1) + '…' : text;
  }

  function list(value, max) {
    return Array.isArray(value) ? value.slice(0, max) : [];
  }

  function statusOf(value) {
    return STATUSES.indexOf(value) >= 0 ? value : 'todo';
  }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null && text !== '') node.textContent = text;
    return node;
  }

  function svg(tag, attrs, className) {
    var node = document.createElementNS(SVG, tag);
    if (attrs) Object.keys(attrs).forEach(function (key) { node.setAttribute(key, String(attrs[key])); });
    if (className) node.setAttribute('class', className);
    return node;
  }

  function svgText(x, y, text, className) {
    var node = svg('text', { x: x, y: y, 'text-anchor': 'middle' }, className);
    node.textContent = text;
    return node;
  }

  /** Cut a label to what fits in `width` user units at about 6.6 units a character. */
  function fit(text, width, size) {
    var max = Math.max(4, Math.floor(width / ((size || 12) * 0.56)));
    return text.length > max ? text.slice(0, max - 1) + '…' : text;
  }

  /** Words broken into at most `maxLines` lines that fit `width`; the last line is cut if it must be. */
  function wrap(text, width, size, maxLines) {
    var max = Math.max(4, Math.floor(width / ((size || 12) * 0.56)));
    var lines = [];
    var current = '';
    text.split(/\s+/).forEach(function (word) {
      if (!word) return;
      var next = current ? current + ' ' + word : word;
      if (next.length <= max || !current) current = next;
      else { lines.push(current); current = word; }
    });
    if (current) lines.push(current);
    if (lines.length > maxLines) {
      lines = lines.slice(0, maxLines - 1).concat([lines.slice(maxLines - 1).join(' ')]);
    }
    return lines.map(function (line) { return fit(line, width, size); });
  }

  function idList(value) {
    return list(value, 40).filter(function (id) { return typeof id === 'string' && id.length > 0 && id.length <= 128; });
  }

  /**
   * Is this piece being worked right now? `live` is { cards: Set, members: Set, byCard, byMember }
   * from the platform's own `tp-now`; a piece is live when one of its cards, or its member, is.
   */
  function liveRun(piece, live) {
    if (!live) return null;
    var cards = piece.cards || [];
    for (var i = 0; i < cards.length; i++) if (live.byCard[cards[i]]) return live.byCard[cards[i]];
    if (piece.member && live.byMember[piece.member]) return live.byMember[piece.member];
    return null;
  }

  // ------------------------------------------------------------------------------------------
  // Graph layout, shared by diagrams and flow charts.
  // ------------------------------------------------------------------------------------------

  function normaliseGraph(nodesIn, edgesIn, maxNodes, maxEdges, isFlow) {
    var nodes = [];
    var byId = {};
    list(nodesIn, maxNodes).forEach(function (raw) {
      if (!raw || typeof raw !== 'object' || typeof raw.id !== 'string' || !ID.test(raw.id) || byId[raw.id]) return;
      var detail = typeof raw.detail === 'string' ? [raw.detail] : list(raw.detail, 3);
      var node = {
        id: raw.id,
        label: str(raw.label, LIMITS.label) || raw.id,
        detail: detail.map(function (line) { return str(line, LIMITS.detail); }).filter(Boolean),
        status: isFlow && raw.status === undefined && !Array.isArray(raw.cards) ? 'unchanged' : statusOf(raw.status),
        shape: isFlow && ['start', 'step', 'decision', 'end'].indexOf(raw.shape) >= 0 ? raw.shape : (isFlow ? 'step' : 'box'),
        column: Number.isInteger(raw.column) && raw.column >= 0 && raw.column <= 11 ? raw.column : null,
        row: Number.isInteger(raw.row) && raw.row >= 0 && raw.row <= 11 ? raw.row : null,
        cards: idList(raw.cards),
        member: str(raw.member, 80),
        kind: typeof raw.kind === 'string' ? raw.kind : null,
      };
      nodes.push(node);
      byId[node.id] = node;
    });

    var edges = [];
    list(edgesIn, maxEdges).forEach(function (raw) {
      if (!raw || !byId[raw.from] || !byId[raw.to] || raw.from === raw.to) return;
      edges.push({ from: raw.from, to: raw.to, label: str(raw.label, 40) });
    });

    // Columns: given, or the longest path from a node with nothing into it (cycles are cut).
    var depth = {};
    function depthOf(id, seen) {
      if (depth[id] !== undefined) return depth[id];
      if (byId[id].column !== null) return (depth[id] = byId[id].column);
      if (seen[id]) return 0;
      seen[id] = true;
      var best = 0;
      edges.forEach(function (edge) {
        if (edge.to === id) best = Math.max(best, depthOf(edge.from, seen) + 1);
      });
      seen[id] = false;
      return (depth[id] = Math.min(best, 11));
    }
    nodes.forEach(function (node) { node.col = depthOf(node.id, {}); });

    var taken = {};
    nodes.forEach(function (node) {
      if (node.row === null) return;
      taken[node.col + ':' + node.row] = true;
    });
    nodes.forEach(function (node) {
      if (node.row !== null) { node.r = node.row; return; }
      var r = 0;
      while (taken[node.col + ':' + r]) r++;
      taken[node.col + ':' + r] = true;
      node.r = r;
    });
    nodes.forEach(function (node) { if (node.row !== null) node.r = node.row; });

    return { nodes: nodes, edges: edges, byId: byId };
  }

  function arrowMarker(defs, id, className) {
    var marker = svg('marker', { id: id, viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto' });
    marker.appendChild(svg('path', { d: 'M0,0 L10,5 L0,10 z' }, className));
    defs.appendChild(marker);
  }

  /**
   * Draw a graph as an SVG. `options.kindClass(kindId)` names a node's kind colour class;
   * `options.live` marks in-flight pieces; `options.frame` is the faded backdrop's label.
   */
  function drawGraph(graph, options) {
    var colWidth = 230;
    var boxWidth = 180;
    var columns = 0;
    var rows = 0;
    var tallest = 44;
    graph.nodes.forEach(function (node) {
      columns = Math.max(columns, node.col + 1);
      rows = Math.max(rows, node.r + 1);
      node.labelLines = wrap(node.label, node.shape === 'decision' ? boxWidth * 0.6 : boxWidth - 14, 12, 2);
      var lineCount = node.labelLines.length + node.detail.length;
      node.h = node.shape === 'decision' ? Math.max(72, 44 + 14 * lineCount) : 30 + 14 * Math.max(1, lineCount);
      tallest = Math.max(tallest, node.h);
    });
    var rowPitch = tallest + 40;
    var top = options.frame ? 44 : 16;
    var width = Math.max(980, 40 + columns * colWidth);
    var height = top + rows * rowPitch + 10;
    var gap = columns > 1 ? (width - 40 - boxWidth) / (columns - 1) : 0;

    graph.nodes.forEach(function (node) {
      node.w = boxWidth;
      node.x = columns > 1 ? 20 + node.col * gap : (width - boxWidth) / 2;
      node.y = top + node.r * rowPitch + (tallest - node.h) / 2;
    });

    var root = svg('svg', { viewBox: '0 0 ' + width + ' ' + height, role: 'img', preserveAspectRatio: 'xMidYMin meet' }, 'tp-graph');
    var defs = svg('defs');
    var markerId = 'tp-arrow-' + (++uid);
    var liveMarkerId = 'tp-arrow-live-' + uid;
    arrowMarker(defs, markerId, 'tp-arrowhead');
    arrowMarker(defs, liveMarkerId, 'tp-arrowhead-live');
    root.appendChild(defs);

    if (options.frame) {
      root.appendChild(svg('rect', { x: 6, y: 6, width: width - 12, height: height - 12, rx: 10 }, 'tp-frame'));
      var frameLabel = svg('text', { x: 22, y: 28 }, 'tp-frame-label');
      frameLabel.textContent = options.frame;
      root.appendChild(frameLabel);
    }

    var liveNodes = {};
    graph.nodes.forEach(function (node) {
      node.run = liveRun(node, options.live);
      if (node.run) liveNodes[node.id] = true;
      var fromCards = options.cardStatus ? options.cardStatus(node.cards) : null;
      if (fromCards) node.status = fromCards;
    });

    var edgeLayer = svg('g', null, 'tp-edges');
    graph.edges.forEach(function (edge) {
      var a = graph.byId[edge.from];
      var b = graph.byId[edge.to];
      var d;
      var lx;
      var ly;
      if (b.col > a.col) {
        var x1 = a.x + a.w;
        var y1 = a.y + a.h / 2;
        var x2 = b.x - 2;
        var y2 = b.y + b.h / 2;
        var mid = (x1 + x2) / 2;
        d = 'M' + x1 + ',' + y1 + ' C' + mid + ',' + y1 + ' ' + mid + ',' + y2 + ' ' + x2 + ',' + y2;
        lx = mid; ly = (y1 + y2) / 2 - 6;
      } else if (b.col < a.col) {
        // Right to left (a reply going back out, an SMS to a phone): side to side.
        var bx1 = a.x;
        var by1 = a.y + a.h / 2;
        var bx2 = b.x + b.w + 2;
        var by2 = b.y + b.h / 2;
        var bmid = (bx1 + bx2) / 2;
        d = 'M' + bx1 + ',' + by1 + ' C' + bmid + ',' + by1 + ' ' + bmid + ',' + by2 + ' ' + bx2 + ',' + by2;
        lx = bmid; ly = (by1 + by2) / 2 - 6;
      } else {
        var down = b.r >= a.r;
        var sx = a.x + a.w / 2;
        var sy = down ? a.y + a.h : a.y;
        var tx = b.x + b.w / 2;
        var ty = down ? b.y - 2 : b.y + b.h + 2;
        var bend = down ? 24 : -24;
        d = 'M' + sx + ',' + sy + ' C' + sx + ',' + (sy + bend) + ' ' + tx + ',' + (ty - bend) + ' ' + tx + ',' + ty;
        lx = (sx + tx) / 2 + 8; ly = (sy + ty) / 2;
      }
      // The path INTO the piece being worked is the one that moves.
      var live = !!liveNodes[edge.to];
      var path = svg('path', { d: d, 'marker-end': 'url(#' + (live ? liveMarkerId : markerId) + ')' }, live ? 'tp-edge tp-live' : 'tp-edge');
      if (live) path.setAttribute('data-live', 'true');
      edgeLayer.appendChild(path);
      if (edge.label) {
        var label = svg('text', { x: lx, y: ly }, 'tp-edge-label');
        label.textContent = edge.label;
        edgeLayer.appendChild(label);
      }
    });
    root.appendChild(edgeLayer);

    graph.nodes.forEach(function (node) {
      var group = svg('g', { 'data-node': node.id, 'data-status': node.run ? 'active' : node.status });
      var status = node.run ? 'active' : node.status;
      var kindClass = node.kind && options.kindClass ? options.kindClass(node.kind) : '';
      var shapeClass = 'tp-node tp-s-' + status + (kindClass ? ' ' + kindClass : '') + (node.shape !== 'box' ? ' tp-shape-' + node.shape : '');
      var body = svg('g', null, node.run ? 'tp-now' : '');
      if (node.shape === 'decision') {
        var cx = node.x + node.w / 2;
        var cy = node.y + node.h / 2;
        body.appendChild(svg('polygon', {
          points: [cx, node.y, node.x + node.w, cy, cx, node.y + node.h, node.x, cy].reduce(function (acc, v, i) {
            return acc + (i % 2 === 0 ? (i ? ' ' : '') + v : ',' + v);
          }, ''),
        }, shapeClass));
      } else {
        body.appendChild(svg('rect', {
          x: node.x, y: node.y, width: node.w, height: node.h,
          rx: node.shape === 'start' ? node.h / 2 : 7,
        }, shapeClass));
      }
      group.appendChild(body);

      var labelCount = node.labelLines.length;
      var lines = node.labelLines.concat(node.detail);
      // An outlined shape (a decision, a start) is white inside, so its words are dark whatever its status.
      var outlined = node.shape === 'decision' || node.shape === 'start';
      var textClass = 'tp-node-text tp-t-' + (outlined ? 'outlined' : status) + (kindClass ? ' tp-t-kind' : '');
      var lineTop = node.y + node.h / 2 - (lines.length - 1) * 7 + 4;
      lines.forEach(function (line, index) {
        var inner = node.shape === 'decision' ? node.w * 0.6 : node.w - 14;
        var detail = index >= labelCount;
        group.appendChild(svgText(node.x + node.w / 2, lineTop + index * 14, fit(line, inner, detail ? 10 : 12), textClass + (detail ? ' tp-node-detail' : '')));
      });
      if (node.run) {
        group.appendChild(svg('circle', { cx: node.x + node.w - 10, cy: node.y + 10, r: 4 }, 'tp-dot tp-dot-svg'));
      }
      group.appendChild(svg('title')).textContent = [node.label].concat(node.detail).join(' - ');
      root.appendChild(group);
    });

    // Beneath the live pieces: who is on it, how long, last progress. Drawn last so it sits on top.
    graph.nodes.forEach(function (node) {
      if (!node.run || !options.describeRun) return;
      wrap(options.describeRun(node.run), colWidth, 11, 2).forEach(function (line, index) {
        root.appendChild(svgText(node.x + node.w / 2, node.y + node.h + 15 + index * 13, line, 'tp-run-note'));
      });
    });

    if (rows > 0) root.setAttribute('height', String(Math.min(height, 120 + rows * 70)));
    return { svg: root, live: graph.nodes.some(function (n) { return !!n.run; }) };
  }

  function diagram(description, options) {
    if (!description || typeof description !== 'object') return null;
    var graph = normaliseGraph(description.nodes, description.edges, LIMITS.nodes, LIMITS.edges, false);
    if (graph.nodes.length === 0) return null;
    var drawn = drawGraph(graph, {
      frame: str(description.backdrop, LIMITS.label),
      live: options && options.live,
      describeRun: options && options.describeRun,
      kindClass: options && options.kindClass,
      cardStatus: options && options.cardStatus,
    });
    return { svg: drawn.svg, live: drawn.live, caption: str(description.caption, LIMITS.text) };
  }

  function flow(description, options) {
    if (!description || typeof description !== 'object') return null;
    var graph = normaliseGraph(description.steps, description.links, LIMITS.steps, LIMITS.links, true);
    if (graph.nodes.length === 0) return null;
    var drawn = drawGraph(graph, {
      live: options && options.live,
      describeRun: options && options.describeRun,
      kindClass: options && options.kindClass,
      cardStatus: options && options.cardStatus,
    });
    return {
      svg: drawn.svg,
      live: drawn.live,
      title: str(description.title, LIMITS.label),
      caption: str(description.caption, LIMITS.text),
    };
  }

  // ------------------------------------------------------------------------------------------
  // Screen mockups: structured regions built with DOM calls.
  // ------------------------------------------------------------------------------------------

  function regions(container, regionsIn) {
    list(regionsIn, LIMITS.regions).forEach(function (region) {
      if (!region || typeof region !== 'object') return;
      var block;
      switch (region.type) {
        case 'figures':
          block = el('div', 'tp-m-figures');
          list(region.items, LIMITS.items).forEach(function (item) {
            if (!item) return;
            var figure = el('div', 'tp-m-figure');
            figure.appendChild(el('div', 'tp-label', str(item.label, LIMITS.label)));
            figure.appendChild(el('div', 'tp-m-value', str(item.value, LIMITS.label)));
            var note = str(item.note, LIMITS.label);
            if (note) figure.appendChild(el('div', 'tp-muted', note));
            block.appendChild(figure);
          });
          break;
        case 'fields':
          block = el('div', 'tp-m-fields');
          list(region.items, LIMITS.items).forEach(function (item) {
            if (!item) return;
            var field = el('div', 'tp-m-field');
            field.appendChild(el('div', 'tp-label', str(item.label, LIMITS.label)));
            field.appendChild(el('div', item.input ? 'tp-m-input' : 'tp-m-fieldvalue', str(item.value, LIMITS.label)));
            block.appendChild(field);
          });
          break;
        case 'table': {
          block = el('table', 'tp-m-table');
          var columns = list(region.columns, LIMITS.columns).map(function (c) { return str(c, LIMITS.label); });
          if (columns.length) {
            var head = el('thead');
            var headRow = el('tr');
            columns.forEach(function (c) { headRow.appendChild(el('th', null, c)); });
            head.appendChild(headRow);
            block.appendChild(head);
          }
          var body = el('tbody');
          list(region.rows, LIMITS.rows).forEach(function (row) {
            var tr = el('tr');
            list(row, LIMITS.columns).forEach(function (cell) { tr.appendChild(el('td', null, str(cell, LIMITS.label))); });
            body.appendChild(tr);
          });
          block.appendChild(body);
          break;
        }
        case 'buttons':
          block = el('div', 'tp-m-buttons');
          list(region.items, LIMITS.items).forEach(function (label) {
            var text = str(typeof label === 'object' && label ? label.label : label, LIMITS.label);
            if (text) block.appendChild(el('span', 'tp-m-button', text));
          });
          break;
        case 'heading':
          block = el('div', 'tp-m-heading', str(region.text, LIMITS.label));
          break;
        case 'text':
          block = el('p', region.muted ? 'tp-m-text tp-muted' : 'tp-m-text', str(region.text, LIMITS.text));
          break;
        case 'terminal':
          block = el('div', 'tp-m-terminal');
          list(region.lines, LIMITS.lines).forEach(function (line) { block.appendChild(el('div', 'tp-m-line', str(line, LIMITS.text) || ' ')); });
          break;
        default:
          return;
      }
      container.appendChild(block);
    });
  }

  var STATUS_WORDS = { done: 'done', active: 'in progress', todo: 'not started', unchanged: 'unchanged' };

  function badge(status, live) {
    var node = el('span', 'tp-badge tp-b-' + status);
    if (live) node.appendChild(el('span', 'tp-dot', '●'));
    node.appendChild(document.createTextNode((live ? ' ' : '') + STATUS_WORDS[status]));
    return node;
  }

  /** One screen mockup, or a stand-in card for work with no screen. */
  function screen(description, options, asStandIn) {
    if (!description || typeof description !== 'object') return null;
    options = options || {};
    var cards = idList(description.cards);
    var run = liveRun({ cards: cards, member: str(description.member, 80) }, options.live);
    var fromCards = options.cardStatus ? options.cardStatus(cards) : null;
    var status = run ? 'active' : fromCards || statusOf(description.status);
    var standIn = asStandIn === true || description.noScreen === true;
    var card = el('article', 'tp-mockup' + (standIn ? ' tp-standin' : '') + (run ? ' tp-now' : ''));
    card.setAttribute('data-screen-status', status);
    if (standIn) card.setAttribute('data-standin', 'true');

    var header = el('header', 'tp-mockup-header');
    var title = str(description.title, LIMITS.label) || 'Untitled screen';
    var change = description.change === 'new' || description.change === 'changed' ? description.change : '';
    var words = standIn ? (change ? change + ', no screen' : 'no screen') : change;
    header.appendChild(el('span', 'tp-mockup-title', title + (words ? ' (' + words + ')' : '')));
    header.appendChild(badge(status, !!run));
    card.appendChild(header);

    var body = el('div', 'tp-mockup-body');
    regions(body, description.regions);
    card.appendChild(body);
    if (run && options.describeRun) card.appendChild(el('div', 'tp-run-note tp-run-note-html', options.describeRun(run)));
    return card;
  }

  root.TeamPictureDraw = {
    diagram: diagram,
    flow: flow,
    screen: screen,
    regions: regions,
    badge: badge,
    el: el,
    svg: svg,
    str: str,
    list: list,
    statusOf: statusOf,
    liveRun: liveRun,
    LIMITS: LIMITS,
    STATUS_WORDS: STATUS_WORDS,
  };
})(window);

/*
 * The triage sample: a list over the `items` collection with Done and Assign actions.
 *
 * The page never changes an item itself. A click posts an action (`done` or `assign`) to the
 * team's log; an event trigger on `site.action` wakes a member, whose run updates the item with
 * the `site` tool, and the page shows the change on its next read (every few seconds, and on
 * focus). Until then the item says what was asked.
 *
 * Every piece of fetched text is set with textContent, never innerHTML: an item's title may be
 * anything a plugin or an agent scraped.
 *
 * An item document: { "title": "...", "detail": "...", "status": "open" | "done", "assignee": "..." }
 */
(function () {
  'use strict';

  var COLLECTION = 'items';
  var REFRESH_MS = 5000;

  // What this page asked for, by item id, until the item changes: { label, updatedAt }.
  // In memory only - an opaque-origin page has no localStorage.
  var asked = {};

  function byId(id) {
    return document.getElementById(id);
  }

  function say(text) {
    byId('status').textContent = text || '';
  }

  function when(iso) {
    var date = new Date(iso);
    return isNaN(date.getTime()) ? '' : date.toLocaleString();
  }

  function row(item) {
    var doc = item.doc || {};
    var node = byId('item').content.firstElementChild.cloneNode(true);
    var done = doc.status === 'done';

    node.querySelector('.title').textContent = doc.title || item.id;
    node.querySelector('.assignee').textContent = doc.assignee ? '→ ' + doc.assignee : '';
    node.querySelector('.detail').textContent = doc.detail || '';
    node.querySelector('.meta').textContent = 'Updated ' + when(item.updatedAt) + ' by ' + item.updatedBy;

    var pending = asked[item.id];
    node.querySelector('.pending').textContent = pending ? pending.label + '…' : '';

    var controls = node.querySelector('.controls');
    if (done) {
      controls.remove();
      node.classList.add('is-done');
      return node;
    }

    var input = node.querySelector('.assign-to');
    var doneButton = node.querySelector('.done');
    var assignButton = node.querySelector('.assign');

    doneButton.addEventListener('click', function () {
      post(item, 'done', { id: item.id }, 'Marking done');
    });

    function assign() {
      var who = input.value.trim();
      if (!who) {
        say('Type who to assign "' + (doc.title || item.id) + '" to.');
        input.focus();
        return;
      }
      post(item, 'assign', { id: item.id, assignee: who }, 'Assigning to ' + who);
    }

    assignButton.addEventListener('click', assign);
    input.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') assign();
    });

    return node;
  }

  function post(item, action, payload, label) {
    var id = item.id;
    asked[id] = { label: label, updatedAt: item.updatedAt };
    say('');
    site.action(action, payload).then(function () {
      render();
    }, function (error) {
      delete asked[id];
      say(error.message);
      render();
    });
  }

  function render() {
    return site.data.list(COLLECTION).then(function (items) {
      var open = byId('open');
      var done = byId('done');
      var openRows = [];
      var doneRows = [];

      items.sort(function (a, b) {
        return String(a.updatedAt).localeCompare(String(b.updatedAt));
      });

      items.forEach(function (item) {
        var isDone = item.doc && item.doc.status === 'done';

        // The member's run has answered - the item changed since the click: forget what was asked.
        if (asked[item.id] && asked[item.id].updatedAt !== item.updatedAt) delete asked[item.id];

        (isDone ? doneRows : openRows).push(row(item));
      });

      open.replaceChildren.apply(open, openRows);
      done.replaceChildren.apply(done, doneRows);
      byId('open-count').textContent = '(' + openRows.length + ')';
      byId('done-count').textContent = '(' + doneRows.length + ')';
      byId('open-empty').hidden = openRows.length > 0;
    }, function (error) {
      say(error.message);
    });
  }

  site.whoami().then(function (me) {
    byId('who').textContent = me.displayName || 'you';
  }, function (error) {
    say(error.message);
  });

  render();
  window.setInterval(render, REFRESH_MS);
  window.addEventListener('focus', render);
})();

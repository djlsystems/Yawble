/*
 * The Job Tracker page: the `jobs` collection, with Apply and Not for me.
 *
 * Apply marks the posting `applying` and posts the `apply` action; the "Apply pressed" trigger wakes
 * the Writer, who drafts the letter. Not for me marks it `skipped` and posts nothing. Fetched text is
 * set with textContent, never innerHTML: a posting's title is whatever a board said.
 */
(function () {
  'use strict';

  var COLLECTION = 'jobs';
  var REFRESH_MS = 5000;

  function byId(id) {
    return document.getElementById(id);
  }

  function say(text) {
    byId('status').textContent = text || '';
  }

  function mark(job, status) {
    return site.data.put(COLLECTION, job.id, Object.assign({}, job.doc, { status: status }));
  }

  function row(job) {
    var doc = job.doc || {};
    var node = byId('job').content.firstElementChild.cloneNode(true);

    node.querySelector('.title').textContent = doc.title || job.id;
    node.querySelector('.company').textContent = doc.company || '';
    node.querySelector('.state').textContent = doc.status || 'new';
    node.querySelector('.fit').textContent = doc.fit || '';

    if (doc.status && doc.status !== 'new') {
      node.querySelector('.controls').remove();
      return node;
    }

    node.querySelector('.apply').addEventListener('click', function () {
      say('Asking for a draft for ' + (doc.title || job.id) + '…');
      mark(job, 'applying')
        .then(function () { return site.action('apply', { id: job.id }); })
        .then(function () { say('The Writer is drafting a letter for ' + (doc.title || job.id) + '.'); refresh(); })
        .catch(function (error) { say(error.message); });
    });

    node.querySelector('.skip').addEventListener('click', function () {
      mark(job, 'skipped').then(refresh).catch(function (error) { say(error.message); });
    });

    return node;
  }

  function refresh() {
    return site.data.list(COLLECTION).then(function (jobs) {
      var list = byId('jobs');
      list.replaceChildren.apply(list, jobs.map(row));
      byId('empty').hidden = jobs.length > 0;
    }).catch(function (error) { say(error.message); });
  }

  site.whoami().then(function (me) { byId('who').textContent = me.displayName; });
  refresh();
  setInterval(refresh, REFRESH_MS);
  window.addEventListener('focus', refresh);
})();

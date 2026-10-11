/*
 * The site helper script. Include it as a plain script, with no build step:
 *
 *   <script src="/sites/_sdk/site.js"></script>
 *
 * It defines `window.site`:
 *
 *   site.data.list(collection)          -> [{ id, doc, updatedAt, updatedBy }]
 *   site.data.get(collection, id)       -> { id, doc, updatedAt, updatedBy } or null
 *   site.data.put(collection, id, doc)  -> { id, doc, updatedAt, updatedBy }
 *   site.data.delete(collection, id)    -> true when there was such a document
 *   site.action(name, payload)          -> { seq }
 *   site.whoami()                       -> { displayName }
 *   site.files.url(path)                -> the link that downloads a file from the site's files folder
 *
 * Every call but site.files.url returns a Promise. A refusal rejects with an Error whose message is
 * the platform's sentence and whose `status` is the HTTP status.
 *
 * site.files.url answers a string at once, for an <a href>: `path` is the path a file has in the
 * site's files folder (`<team documents>/sites/<site>/files/`), as stored in the site's data. A path
 * the platform would refuse throws, with the platform's sentence and `status` 400.
 *
 * The page runs in a sandbox with an opaque origin: it has no cookie, and no localStorage or
 * sessionStorage. Keep state in site.data. This script carries the page's capability - the
 * `_c/<capability>/` part of the page's own address - on every call, and sends no credential.
 */
(function () {
  'use strict';

  var match = /^(\/sites\/[^/]+\/[^/]+\/_c\/[^/]+\/)/.exec(window.location.pathname);
  var base = match ? match[1] + '_api/' : null;

  function segment(value) {
    return encodeURIComponent(String(value));
  }

  function call(method, path, body) {
    if (!base) {
      return Promise.reject(new Error('This page was not opened as a site, so it has no access to site data.'));
    }

    var init = { method: method, credentials: 'omit', cache: 'no-store' };

    // text/plain keeps every call a simple request: no preflight, nothing but the capability.
    if (method === 'POST') {
      init.body = body === undefined ? 'null' : JSON.stringify(body);
      init.headers = { 'Content-Type': 'text/plain;charset=UTF-8' };
    }

    return fetch(base + path, init).then(function (response) {
      return response.text().then(function (text) {
        var data = null;

        try {
          data = text ? JSON.parse(text) : null;
        } catch (ignored) {
          data = null;
        }

        if (!response.ok) {
          var error = new Error((data && data.error) || ('The site answered ' + response.status + '.'));
          error.status = response.status;
          throw error;
        }

        return data;
      });
    });
  }

  // The same rule as the platform's for a path in a site's files folder.
  function isFilePath(path) {
    if (typeof path !== 'string' || path.length === 0 || path.length > 1024) return false;

    return path.split('/').every(function (name) {
      if (name.length === 0 || name.charAt(0) === '.') return false;
      if (unescape(encodeURIComponent(name)).length > 255) return false;
      return !/[\\%:\u0000-\u001f\u007f-\u009f]/.test(name);
    });
  }

  function fileUrl(path) {
    if (!base) {
      throw new Error('This page was not opened as a site, so it has no access to site data.');
    }

    if (!isFilePath(path)) {
      var error = new Error('"' + path + '" is not a path in this site\'s files folder. Use names separated by \'/\', '
        + 'with no \'..\', no name starting with \'.\', and no \'\\\', \'%\', \':\' or control character.');
      error.status = 400;
      throw error;
    }

    return base + 'files/' + path.split('/').map(segment).join('/');
  }

  function documentPath(collection, id) {
    return 'data/' + segment(collection) + '/' + segment(id);
  }

  window.site = {
    data: {
      list: function (collection) {
        return call('GET', 'data/' + segment(collection));
      },
      get: function (collection, id) {
        return call('GET', documentPath(collection, id)).catch(function (error) {
          if (error.status === 404) return null;
          throw error;
        });
      },
      put: function (collection, id, doc) {
        return call('POST', documentPath(collection, id), doc);
      },
      'delete': function (collection, id) {
        return call('POST', documentPath(collection, id) + '/delete').then(function (answer) {
          return !!(answer && answer.deleted);
        });
      }
    },
    action: function (name, payload) {
      return call('POST', 'actions/' + segment(name), payload === undefined ? null : payload);
    },
    files: {
      url: fileUrl
    },
    whoami: function () {
      return call('GET', 'whoami').then(function (answer) {
        return { displayName: answer && answer.displayName };
      });
    }
  };
})();

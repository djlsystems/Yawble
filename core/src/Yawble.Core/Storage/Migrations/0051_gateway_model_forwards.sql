-- 0051 gateway log (B103): successful model requests are no longer one row each in gateway_events.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- From B103 the gateway writes only each run's FIRST successful (2xx) forward per model vhost; the model requests
-- themselves are counted in usage_events. This deletes the rows written before that: kind 'forward', a 2xx status, to
-- a model vhost (the keys of Vhosts.ProductionUpstreams: model, grok, xai, copilot, codex .yawble.internal), keeping
-- the lowest id (the first written) per run and vhost.
-- Untouched: non-2xx forwards, forwards to any other vhost, every other kind, and rows with no run (run_id NULL; they
-- cannot be told apart by run).
DELETE FROM gateway_events
WHERE kind = 'forward'
  AND status BETWEEN 200 AND 299
  AND run_id IS NOT NULL
  AND lower(vhost) IN ('model.yawble.internal', 'grok.yawble.internal', 'xai.yawble.internal',
                       'copilot.yawble.internal', 'codex.yawble.internal')
  AND id NOT IN (
      SELECT min(id) FROM gateway_events
      WHERE kind = 'forward'
        AND status BETWEEN 200 AND 299
        AND run_id IS NOT NULL
        AND lower(vhost) IN ('model.yawble.internal', 'grok.yawble.internal', 'xai.yawble.internal',
                             'copilot.yawble.internal', 'codex.yawble.internal')
      GROUP BY run_id, lower(vhost));

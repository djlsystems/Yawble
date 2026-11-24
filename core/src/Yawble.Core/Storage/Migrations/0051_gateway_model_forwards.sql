-- 0051 gateway log: successful model requests are no longer one row each in gateway_events.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- The gateway (GatewayLog.Write) writes a 2xx forward to a model vhost only the first time per run and vhost,
-- and again for the first one after each credential-problem or rate-limited event on that vhost in that run (the success
-- GatewayVerdicts.From reads as the problem having cleared); the model requests themselves are counted in usage_events.
-- This deletes the rows written before that rule by the same rule: kind 'forward', a 2xx status, to a model vhost (the
-- keys of Vhosts.ProductionUpstreams: model, grok, xai, copilot, codex .yawble.internal, compared lowercase). Each
-- run's events on a vhost are split into segments by its credential-problem / rate-limited events, and the lowest id
-- (the first written) 2xx forward of each segment is kept.
-- Untouched: non-2xx forwards, forwards to any other vhost, every other kind, and rows with no run (run_id NULL; they
-- cannot be told apart by run). Reads no column added after 0004, so it applies the same before or after 0050.
DELETE FROM gateway_events
WHERE kind = 'forward'
  AND status BETWEEN 200 AND 299
  AND run_id IS NOT NULL
  AND lower(vhost) IN ('model.yawble.internal', 'grok.yawble.internal', 'xai.yawble.internal',
                       'copilot.yawble.internal', 'codex.yawble.internal')
  AND id NOT IN (
      SELECT min(id) FROM (
          SELECT id, kind, status, run_id, lower(vhost) AS v,
                 sum(CASE WHEN kind IN ('credential-problem', 'rate-limited') THEN 1 ELSE 0 END)
                     OVER (PARTITION BY run_id, lower(vhost) ORDER BY id ROWS UNBOUNDED PRECEDING) AS segment
          FROM gateway_events
          WHERE run_id IS NOT NULL
            AND lower(vhost) IN ('model.yawble.internal', 'grok.yawble.internal', 'xai.yawble.internal',
                                 'copilot.yawble.internal', 'codex.yawble.internal'))
      WHERE kind = 'forward' AND status BETWEEN 200 AND 299
      GROUP BY run_id, v, segment);

-- 0011 trigger wake choice (triggers.wake_manager): whether a trigger's runs wake the Manager when they end.
-- always | onHandbackOrFailure | never. A trigger saved before this step keeps 'always' (its behaviour so far);
-- the app saves a new trigger with 'onHandbackOrFailure' unless the person chooses otherwise.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE triggers ADD COLUMN wake_manager TEXT NOT NULL DEFAULT 'always'
    CHECK (wake_manager IN ('always', 'onHandbackOrFailure', 'never'));

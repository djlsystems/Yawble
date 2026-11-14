-- 0042 a tool ask keeps the result code of its last Install or Check again.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- last_result: failed, cancelled (pkexec 126: the password window was closed), notAuthorized (pkexec 127), homeOnly
-- (the program is only under the home folder) or notFound (Check again: not there yet); NULL before the first attempt,
-- for an ask from before 0042, and once the ask is installed. tools.asks sends it as lastResult, so a fresh Needs you
-- card (a reloaded window, the other view, a remote view) shows a closed password window as a hint with Install, not as
-- a package failure with Try again. Written beside error: the two always describe the same attempt.
ALTER TABLE tool_asks ADD COLUMN last_result TEXT NULL;

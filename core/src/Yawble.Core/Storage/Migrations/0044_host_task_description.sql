-- 0044 a host task may say what it is for and how to check its result (B066).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- description: free text, what the task does (the Concierge picks a task by it: "deploys the cloud service").
-- check_after: free text, how to check the result once the run is over (a health URL, a page to open).
-- Neither is part of what an approval is pinned to (TaskPinning.Definition): editing them changes nothing that runs.
-- NULL for a task with none, and for every task from before 0044.
ALTER TABLE host_tasks ADD COLUMN description TEXT;
ALTER TABLE host_tasks ADD COLUMN check_after TEXT;

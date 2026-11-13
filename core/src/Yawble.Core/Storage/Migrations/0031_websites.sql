-- 0031 websites as integrations (a signed-in browser on this computer).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- apis.kind says what a row is: 'http' (a web API read from its OpenAPI spec) or 'website' (a site the agents use
-- in a browser window on this computer, signed in as the person). A website's base_url is its site URL; its actions are the
-- fixed six (open, read, screenshot, click, fill, submit), written to api_commands by Discover with no fetch; docs_url,
-- spec_url and auth stay NULL. Its connection_id, when set, is a Connection of kind 'website' (the connections table already
-- allows that kind); none means a public site. The browser session itself (cookies, storage) lives only in the app window's
-- own partition, never in this database.
ALTER TABLE apis ADD COLUMN kind TEXT NOT NULL DEFAULT 'http' CHECK (kind IN ('http', 'website'));

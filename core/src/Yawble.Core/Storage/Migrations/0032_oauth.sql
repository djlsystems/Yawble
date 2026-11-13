-- 0032 generic OAuth Connections for web APIs.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- api_commands.scopes: the OAuth scopes one operation needs, as the spec's per-operation (or top-level) security entry lists them,
-- space-separated. NULL = none known. apis.auth is JSON and needs no column: an OAuth API stores
-- {"type":"oauth2","authorizeUrl":...,"tokenUrl":...,"scopes":{"name":"description"},"openIdConnectUrl":...}.
ALTER TABLE api_commands ADD COLUMN scopes TEXT NULL;

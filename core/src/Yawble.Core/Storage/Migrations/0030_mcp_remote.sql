-- 0030 MCP servers gain a transport (stdio or http), a URL and sign-in for remote ones,
-- environment variables for local ones (secret values live in a Connection, never here), the Connection they use,
-- and the apps they were imported from.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- auth is JSON {"type":"none"|"bearer"|"header"|"oauth","header"?:string}.
-- env is JSON [{"name":string,"value"?:string,"secret":bool}]; a secret entry has no value: its value is the field
-- named "name" of the server's Connection (for an http server with bearer/header sign-in, the field "token").
-- For http servers command is ''. sources is a JSON array of strings (e.g. ["Claude Desktop","Cursor"]).
ALTER TABLE mcp_servers ADD COLUMN transport TEXT NOT NULL DEFAULT 'stdio' CHECK (transport IN ('stdio', 'http'));
ALTER TABLE mcp_servers ADD COLUMN url TEXT NULL;
ALTER TABLE mcp_servers ADD COLUMN auth TEXT NULL;
ALTER TABLE mcp_servers ADD COLUMN env TEXT NOT NULL DEFAULT '[]';
ALTER TABLE mcp_servers ADD COLUMN connection_id INTEGER NULL REFERENCES connections(id) ON DELETE SET NULL;
ALTER TABLE mcp_servers ADD COLUMN sources TEXT NULL;

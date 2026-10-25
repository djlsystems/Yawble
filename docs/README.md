# Yawble documentation

Start with the [README](../README.md) to install and start Yawble. These pages cover the rest.

## Using Yawble

| Doc | What it covers |
|---|---|
| [triggers.md](triggers.md) | Triggers and what they cost: the measured cost line, waking the Manager, the daily token cap, why polling belongs to plugins, and where a scheduled team's activity view begins. |
| [ops/folder-change-triggers.md](ops/folder-change-triggers.md) | Folder-change triggers: watching the team's documents or a mounted share. |
| [local-repositories.md](local-repositories.md) | Local repositories: git repositories kept only on the instance, `local:<name>`, creating and deleting them, and getting the code out with `yawble repo`. |
| [connections.md](connections.md) | Connections: the accounts (Google, Microsoft, a mailbox with an app password, custom OAuth) the Host holds for plugins, setting each one up, the web and `yawble connect` flows, and binding a connection to a member. |
| [sites.md](sites.md) | Sites: a team's small web pages the platform serves and backs - publishing, the data store, actions, the helper script, and how every browser request is authorized. |
| [solutions.md](solutions.md) | Solution packages: a whole working team in one folder - `solution.json`, the package layout, the check, and installing one. |
| [plugins.md](plugins.md) | Plugin members: installing, the manifest and protocol, events, and building one. |

## Running an instance

| Doc | What it covers |
|---|---|
| [../cli/README.md](../cli/README.md) | The `yawble` operator CLI: every command. |
| [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md) | Daily database copies, full-volume backups, restoring either, logs, and agent CLI versions. |
| [ops/database.md](ops/database.md) | The database: SQLite (the default) or SQL Server, the two settings, creating a SQL Server database, backups on each, operator commands, the differences and limits, and writing a schema step for both. |
| [ops/releases-and-updates.md](ops/releases-and-updates.md) | Updating an instance, and how a release is cut. |
| [ops/cli-releases-and-install.md](ops/cli-releases-and-install.md) | Installing the `yawble` operator CLI, and how its releases are built. |

## How it works

| Doc | What it covers |
|---|---|
| [architecture.md](architecture.md) | The components, how work flows through teams, the board (Kanban, Monitor, outcomes, archived teams), control and workers, where data lives, and the security model. |
| [../AGENTS.md](../AGENTS.md) | The rules the code depends on, each with the test that pins it. For contributors. |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | How to build, test and open a pull request. |

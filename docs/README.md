# Yawble documentation

| Doc | What it covers |
|---|---|
| [architecture.md](architecture.md) | The components, how work flows through teams, where data lives, and the security model. |
| [ops/releases-and-updates.md](ops/releases-and-updates.md) | Updating a running instance from a checkout, and cutting a release. |
| [ops/cli-releases-and-install.md](ops/cli-releases-and-install.md) | Installing the `yawble` operator CLI and how its releases are built. |
| [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md) | Daily database copies, full-volume exports, restoring either, logs, and agent CLI versions. |
| [triggers.md](triggers.md) | Triggers and what they cost: the measured cost line, waking the Manager, the daily token cap, and why polling belongs to plugins. |
| [local-repositories.md](local-repositories.md) | Local repositories: git repositories that live only on the instance, `local:<name>`, creating and deleting them, and getting the code out with `yawble repo`. |
| [connections.md](connections.md) | Connections: OAuth accounts (Google, Microsoft, custom) the Host holds for plugins, registering each provider's client, the web and `yawble connect` flows, and binding a connection to a member. |
| [sites.md](sites.md) | Sites: a team's small web pages the platform serves and backs - publishing, the data store, actions, the helper script, and how every browser request is authorized. |
| [solutions.md](solutions.md) | Solution packages: a whole working team in one folder - `solution.json`, the package layout, the check, and the full Job Tracker example. |
| [plugins.md](plugins.md) | Plugin members: installing, the manifest and protocol, events, and polling for free. |
| [ops/folder-change-triggers.md](ops/folder-change-triggers.md) | Folder-change triggers: watching the team's documents or a mounted share. |

The operator CLI's own reference is [cli/README.md](../cli/README.md).

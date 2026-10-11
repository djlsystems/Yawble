#!/bin/sh
# Starts as root and ends as the container's Host user. One script for every role (HARNESS_ROLE):
#
#   all      (one container, as before) ends as `harness`:
#              1. creates the volume directories the host expects;
#              2. sets who owns what on the volume (prepare-volume.sh ownership), every start;
#              3. installs the "System packages" setting with apt-get (prepare-volume.sh packages);
#              4. installs any missing agent CLIs onto the volume, as `agent`, and updates present ones;
#              5. drops to `harness` and execs the host.
#   control  steps 1, 2 and 5. It starts no agent CLI, so it installs no package and no CLI.
#   worker   ends as `worker`. It changes no ownership on the volume - control does, and N workers
#            share it - so it waits until control has prepared it, then steps 3 and 4 (missing CLIs
#            only, under a lock on the volume: an update of a CLI another worker is running goes
#            through control's update gate), then drops to `worker` and execs the host.
# Steps 3 and 4 may fail; the host starts anyway, because the board is more useful than a
# container that refuses to boot over one package or one vendor download.
# The children's HOME is on the volume so an agent CLI login survives an image rebuild; root's is not.
set -e
# ROOT FINDS COMMANDS IN SYSTEM FOLDERS ONLY. The image's PATH also holds folders agent
# owns (/data/bin, npm-global, go, .grok/bin, ...); with those on it, an agent that writes
# /data/bin/find or /data/bin/git would run as root at the next start. Everything up to the exec
# below, and prepare-volume.sh (which sets the same PATH itself), runs on this one.
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
# ROOT READS NO USER CONFIG FROM AGENT-HOME. The image's HOME is /data/agent-home, which
# agent owns: with it, root's git reads agent's ~/.gitconfig (a malformed one stops the boot under
# set -e), python3 in an apt maintainer script (py3compile) imports agent's usercustomize.py as
# root, and dpkg and debconf read ~/.dpkg.cfg and ~/.debconfrc. The root section runs on root's own
# HOME, with no global git config, no Python user site and no XDG folders; prepare-volume.sh sets
# the same. Only the children that leave root (the CLI install, the host) get agent-home back, on their own lines below.
export HOME=/root
export GIT_CONFIG_GLOBAL=/dev/null
export PYTHONNOUSERSITE=1
unset XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME XDG_STATE_HOME
# THE WORKER KEY LEAVES THE ENVIRONMENT BEFORE ANYTHING ELSE RUNS. It arrives as a variable (the
# operator CLI's env file); every process started from here on would inherit it, and an agent can
# read the environment of a process of its own user. So, first, root writes it to a file only this
# container's Host user can read (harness, or worker in a worker), unsets it, and names the file
# instead. HARNESS_WORKER_KEY_DIR and HARNESS_CHOWN are for a test; neither is set in an image.
if [ -n "${HARNESS_WORKER_KEY:-}" ]; then
  key_user=harness
  [ "$(printf '%s' "${HARNESS_ROLE:-all}" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')" != worker ] || key_user=worker
  key_dir="${HARNESS_WORKER_KEY_DIR:-/run/harness}"
  mkdir -p "$key_dir"
  # Symbolic, with ug-s: GNU chmod keeps a directory's setgid bit through an octal mode.
  chmod u=rwx,go=,ug-s "$key_dir"
  ( umask 077 && printf '%s\n' "$HARNESS_WORKER_KEY" > "$key_dir/worker-key" )
  chmod 0400 "$key_dir/worker-key"
  "${HARNESS_CHOWN:-chown}" "$key_user:$key_user" "$key_dir" "$key_dir/worker-key"
  export HARNESS_WORKER_KEY_FILE="$key_dir/worker-key"
fi
unset HARNESS_WORKER_KEY
# The role, as the host reads it: trimmed, any case. An unknown one takes the `all` path and the
# host refuses it with its own sentence.
role=$(printf '%s' "${HARNESS_ROLE:-all}" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')
# The control image has no agent CLI and no toolchain: it runs control and nothing else.
if [ "${HARNESS_IMAGE:-}" = control ] && [ "$role" != control ]; then
  echo "This is the control image; it has no agent CLI, so it runs only as control (HARNESS_ROLE=control)." >&2
  exit 2
fi
# What `agent` and the host run on: the system folders FIRST, so no name in them can be replaced
# from an agent-owned folder, then the tools agents install (claude, codex, grok, npm globals),
# whose names are not in the system folders. Same order as the image's ENV PATH.
tool_path="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/local/go/bin:/data/agent-home/.grok/bin:/data/npm-global/bin:/data/bin:/data/go/bin:/data/agent-home/.local/bin:/data/agent-home/.dotnet/tools"

# The data root is harness:agent 0750, so `agent` cannot create an entry in it: every top-level
# folder or file an agent (or ensure-agent-clis.sh, run as `agent`) writes into is made here first,
# and the ownership step hands it over. One the host makes later, at run time, is the host's until
# the next start. Control and `all` only: a root mkdir in a worker would leave root's entries on
# the volume until control's next start.
prepare_volume() {
  mkdir -p /data/agent-home /data/keys /data/logs /data/steering /data/bin /data/npm-global \
      /data/nuget /data/go /data/go-cache /data/npm-cache /data/pip-cache /data/ms-playwright \
      /data/teams /data/documents /data/skill-drafts /data/tenant-interactive-agent-workspaces
  touch /data/cli-versions.jsonl
  /bin/sh /opt/harness/prepare-volume.sh ownership
}

# Repos on the volume are not owned by every user that works in them (the host is `harness`, the
# tree is `agent`'s). Without this, git refuses them as "dubious ownership" and an agent reports that
# git is missing. In the SYSTEM config, so it holds for both users whatever their HOME.
if ! git config --system --get-all safe.directory 2>/dev/null | grep -qx '\*'; then
  git config --system --add safe.directory '*'
fi

# Prompt, system-prompt and usage files go to /tmp from both users.
chmod 1777 /tmp

# GitHub over https with the token from the environment, so a team's repository URL never has to
# carry a credential. The helper reads GH_TOKEN (or GITHUB_TOKEN) at the moment git asks, so a new
# token takes effect on the next container start without touching any stored URL. In the SYSTEM
# config, like safe.directory: root writing into the shared HOME would depend on its modes.
if [ -n "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]; then
  git config --system credential.https://github.com.helper     '!f() { test "$1" = get && echo username=x-access-token && echo "password=${GH_TOKEN:-$GITHUB_TOKEN}"; }; f'
fi

# THE HOST RUNS AS ITS OWN USER, keeping exactly three capabilities, in the bounding set too, so
# nothing it starts can gain another: SETUID and SETGID to start each agent child and the Concierge
# PTY as `agent` (and, in control, git and gh as `agent`), and KILL to stop an agent's process tree (a
# watchdog, a spend limit, Stop on the board) - a process of another user refuses a signal without
# it. The launch that switches to `agent` clears the inheritable and ambient sets (setpriv
# --inh-caps=-all --ambient-caps=-all), so an agent holds none of the three. umask 0007: nothing the
# host creates is readable by others. With 0002, what it creates in /data between starts
# (messages.db on a fresh volume, agents.json) would be world-readable, and agent can traverse /data
# (harness:agent 0750), so an agent could read them until the next start's ownership pass. Team files
# stay writable by agent: the team dirs are setgid agent and the host's user is in group agent. /tmp
# files an agent must read are handed over by the host's Share().
# Without CAP_KILL a non-root process gets EPERM signalling a process of another uid, so the host
# could not stop an agent child. --bounding-set IS kept here: this line still runs as root, which
# holds CAP_SETPCAP. Run by a non-root user (the host switching a child to agent) it fails with
# "setpriv: apply bounding set: Operation not permitted" (exit 127), which is why the agent switch
# clears only the inheritable and ambient sets.
# Each branch ends the root section the same way: the host's PATH, system folders first, and the
# host's HOME, which the agent children inherit (root's HOME is /root, set at the top). setpriv
# and dotnet by absolute path, so the switch itself never searches PATH.
case "$role" in
  control)
    prepare_volume
    # A control container starts no agent CLI - every one runs on a worker - so it installs no
    # package for agents and installs and records no CLI. Its PATH is the system folders alone.
    echo "agent cli: control role, no agent CLI is installed or recorded here"
    export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/data/agent-home
    unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE
    umask 0007
    exec /usr/bin/setpriv --reuid=harness --regid=harness --init-groups \
        --inh-caps=-all,+setuid,+setgid,+kill --ambient-caps=-all,+setuid,+setgid,+kill \
        --bounding-set=-all,+setuid,+setgid,+kill \
        -- /usr/bin/dotnet /app/Harness.Host.dll
    ;;
  worker)
    # Control prepares the volume; until it has (the data root host:agent 0750, tmp and agent-home
    # there), nothing below can work. A worker started first waits, and after the limit starts
    # anyway, so a misread never keeps a worker down for good. The figures are for a test.
    wait_root="${HARNESS_DATA_ROOT:-/data}"
    wait_every="${HARNESS_VOLUME_WAIT_SECONDS:-10}"
    wait_limit="${HARNESS_VOLUME_WAIT_LIMIT:-600}"
    waited=0
    until [ "$(stat -c '%u:%g %a' "$wait_root" 2>/dev/null)" = "${HARNESS_HOST_UID:-10001}:${HARNESS_AGENT_GID:-10002} 750" ] \
        && [ -d "$wait_root/tmp" ] && [ -d "$wait_root/agent-home" ]; do
      if [ "$waited" -ge "$wait_limit" ]; then
        echo "worker: the volume is still not prepared by control after $waited s; starting anyway"
        break
      fi
      echo "worker: waiting for control to prepare the volume"
      sleep "$wait_every"
      waited=$((waited + wait_every))
    done
    # As root, before anything drops privileges: agents cannot apt-get, this is where a person's
    # list is installed, into this container's own system folders.
    /bin/sh /opt/harness/prepare-volume.sh packages || echo "volume: system packages hit an error; starting the host anyway"
    rm -rf /var/lib/apt/lists/*
    # MISSING CLIs ONLY, ONE WORKER AT A TIME. The CLIs are on the shared volume and other workers may
    # be running members from them: replacing one in place would leave a member launched in that
    # moment with no program. So a worker's start installs only what is missing
    # (HARNESS_UPDATE_AGENTS=0), under a lock every worker takes; updates of present CLIs are
    # control's, through the update gate, which waits for runs and holds launches.
    ( umask 002 && export PATH="$tool_path" HOME=/data/agent-home NPM_CONFIG_PREFIX=/data/npm-global HARNESS_UPDATE_AGENTS=0 \
        && unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE && exec /usr/bin/flock /data/tmp/.agent-cli-install.lock \
        /usr/bin/setpriv --reuid=agent --regid=agent --init-groups --inh-caps=-all --ambient-caps=-all \
        -- /bin/sh /opt/harness/ensure-agent-clis.sh ) \
      || echo "agent cli: setup hit an error; starting the host anyway"
    # The worker's own state - its run process groups, its health and drain files - in the container,
    # not on the volume, and the worker user's alone.
    mkdir -p /var/lib/harness-worker
    chown worker:worker /var/lib/harness-worker
    chmod 0700 /var/lib/harness-worker
    export PATH="$tool_path" HOME=/data/agent-home NPM_CONFIG_PREFIX=/data/npm-global
    unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE
    umask 0007
    exec /usr/bin/setpriv --reuid=worker --regid=worker --init-groups \
        --inh-caps=-all,+setuid,+setgid,+kill --ambient-caps=-all,+setuid,+setgid,+kill \
        --bounding-set=-all,+setuid,+setgid,+kill \
        -- /usr/bin/dotnet /app/Harness.Host.dll
    ;;
  *)
    prepare_volume
    # As root, before anything drops privileges: agents cannot apt-get, this is where a person's list is
    # installed. The package lists are fetched per start and removed again so the layer stays small.
    /bin/sh /opt/harness/prepare-volume.sh packages || echo "volume: system packages hit an error; starting the host anyway"
    rm -rf /var/lib/apt/lists/*
    # The CLIs live in agent-owned folders and are run by `agent`, so `agent` installs them, with
    # agent-home as HOME and without the root section's git and Python settings. umask 002
    # keeps what it writes group-writable for the host, as the ownership step left the rest.
    # Not the host's 0007: installed CLIs hold no secrets, so they stay runnable by users in neither group.
    ( umask 002 && export PATH="$tool_path" HOME=/data/agent-home NPM_CONFIG_PREFIX=/data/npm-global \
        && unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE && exec /usr/bin/setpriv --reuid=agent --regid=agent --init-groups --inh-caps=-all --ambient-caps=-all \
        -- /bin/sh /opt/harness/ensure-agent-clis.sh ) \
      || echo "agent cli: setup hit an error; starting the host anyway"
    # The worker image sets no listen address (a worker dials out); `all` serves the board on 8080.
    export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:8080}"
    export PATH="$tool_path" HOME=/data/agent-home NPM_CONFIG_PREFIX=/data/npm-global
    unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE
    umask 0007
    exec /usr/bin/setpriv --reuid=harness --regid=harness --init-groups \
        --inh-caps=-all,+setuid,+setgid,+kill --ambient-caps=-all,+setuid,+setgid,+kill \
        --bounding-set=-all,+setuid,+setgid,+kill \
        -- /usr/bin/dotnet /app/Harness.Host.dll
    ;;
esac

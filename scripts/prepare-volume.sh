#!/bin/sh
# What the entrypoint does AS ROOT before it drops to the host's user, out of the entrypoint
# so a test can run it against a temporary directory (tests/Harness.Tests/Host/PrepareVolumeTests.cs).
#
#   prepare-volume.sh ownership   who owns what on the volume, every start
#   prepare-volume.sh packages    the "System packages" setting, installed with apt-get
#   prepare-volume.sh roots       lists the file-browser roots ownership would take; changes nothing
#
# THE USERS (fixed in the Containerfile, so a volume moved to a rebuilt image keeps meaning the same):
#   harness  10001:10001  runs the host in control. Also in group `agent`, so it can write in the team trees.
#   agent    10002:10002  runs every agent child and the Concierge PTY. In no harness group.
#   worker   10003:10003  runs the host in a worker. In group `agent` and in no harness group, so the
#                         map below gives it exactly what agent has: no host entry, ever.
# Only control (and `all`) runs this. A worker's entrypoint changes no ownership: it shares the volume
# with control and every other worker, and waits for control to have prepared it.
#
# THE OWNERSHIP MAP. The data root itself is harness:agent 0750 (agents may pass through it, not
# create in it). These top-level entries are the host's and nobody else's - harness:harness, 700
# for directories and 600 for everything else, all the way down:
#   messages.db*  keys  agents.json*  agent-launch.json*  logs  backups  host.lock*  system-packages*  .healthz-*  connections
#   agent-credentials  agent-auth.json*  agent-tools.json*  agent-launch-checks.json*  wip.json*  workers.json*
# agent-credentials is the operator CLI's credential exchange: a request there can carry a credential
# value, and the host refuses the folder unless it is its own and 0700. agent-auth.json, agent-tools.json,
# agent-launch-checks.json and wip.json are what --doctor reports as sign-ins, tools, launch checks and
# run limits, and workers.json what it reports of each worker: an agent that could write one could make
# the doctor lie. Only control writes any of them.
# connections is the operator CLI's `connect` exchange: a request there carries an authorization code
# and the host answers only while no other user can write in it, so it is never agent's.
# agent-launch.json is what --doctor reports as agentLaunch; an agent that could write it could make
# the doctor say launches are allowed. Nothing is pre-created for it: the host writes it at every
# start (a .tmp then a rename) into the data root, which agent cannot write, with umask 0007, so it
# lands harness:harness 660 and this pass makes it 600 at the next start.
# plugins is the host's and readable by agent, which runs every plugin: harness:agent all the way
# down, directories 0750, files 0640 with a plugin's executable 0750 - the execute bits a file has
# are kept, and group write, other and setuid/setgid are taken away. An agent that could write here
# could replace the program a plugin member runs, or ask the host to rescan. It is created when
# missing, so the operator CLI's `plugin install` has somewhere to copy to.
# repos holds the instance's local repositories, bare, one <name>.git each. It is the host's and
# readable by agent, never writable by it: harness:agent all the way down, directories 2750 (setgid,
# so a directory git adds later is still the group's), files lose group write, other and
# setuid/setgid and gain group read; the owner's bits git chose (objects are 0444) are kept. Agents
# never push: the host publishes into these as itself. It is created when missing.
# tmp holds each member's TMPDIR (MemberTemp), on the volume so an engine's tmpfs /tmp never holds
# a member's temporary files in memory. It is created when missing and is agent's like the rest.
# Every other top-level entry - teams, documents, agent-home, npm-global, bin, the tool caches, the
# Concierge's workspaces - is agent:agent, with directories group-writable and setgid and files
# group read-write, so what the host creates there (it runs with umask 0007) stays in group agent
# and writable by both. The exception is agent-home: the owner changes all the way down, but only
# the home itself and its .grok (the host writes grok's config.toml) become group-writable. The
# Claude login, .ssh, .gnupg and the rest keep their modes, because a login or a key that stops
# working, or a key the group can read, is the one thing an upgrade must not do.
#
# A team can also live OUTSIDE the data root, in a file-browser root the container is given as
# FileBrowser__Roots__<n>__Path. Each one that exists is handed to agent the same way. A system
# directory (/, /usr, /etc, /var/..., ...) is refused, whatever the setting says: the path is
# resolved first, so no spelling or symlink gets past the list.
#
# IDEMPOTENT: only entries that differ are touched, and the last line says how many that was. An
# existing volume where everything is root's becomes this map on its first start and stays it.
#
# For a test, every path, id and command below comes from the environment.
set -u
# ROOT FINDS COMMANDS IN SYSTEM FOLDERS ONLY, whatever PATH the caller had: /data/bin and the
# other tool folders are agent's, and a find or chown planted there would run here as root.
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH
# ROOT READS NO USER CONFIG FROM AGENT-HOME, whatever HOME it was called with: git would read
# agent's ~/.gitconfig, python3 in an apt maintainer script agent's usercustomize.py, dpkg and
# debconf ~/.dpkg.cfg and ~/.debconfrc. The tool folders the image points at the volume (pip, npm,
# go, NuGet caches) are agent's too and are no business of apt's.
HOME=/root
GIT_CONFIG_GLOBAL=/dev/null
PYTHONNOUSERSITE=1
export HOME GIT_CONFIG_GLOBAL PYTHONNOUSERSITE
unset XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME XDG_STATE_HOME PYTHONPATH PYTHONSTARTUP PYTHONUSERBASE \
    PIP_CACHE_DIR PIP_CONFIG_FILE npm_config_cache NPM_CONFIG_PREFIX NPM_CONFIG_USERCONFIG GOPATH GOCACHE NUGET_PACKAGES

root="${HARNESS_DATA_ROOT:-/data}"
chown_cmd="${HARNESS_CHOWN:-chown}"
apt_cmd="${HARNESS_APT_GET:-apt-get}"
host_uid="${HARNESS_HOST_UID:-10001}"
host_gid="${HARNESS_HOST_GID:-10001}"
agent_uid="${HARNESS_AGENT_UID:-10002}"
agent_gid="${HARNESS_AGENT_GID:-10002}"

log() { echo "volume: $*"; }

# Prints each path it changes, so the caller can count them.
set_owner() { # path uid gid
  find "$1" \( ! -uid "$2" -o ! -gid "$3" \) -print -exec "$chown_cmd" -h "$2:$3" {} +
}

is_host_entry() {
  case "$1" in
    messages.db|messages.db-*|keys|agents.json|agents.json.*|agent-launch.json|agent-launch.json.*|logs|backups|host.lock|host.lock.*|system-packages|system-packages.*|.healthz-*|connections)
      return 0 ;;
    agent-credentials|agent-auth.json|agent-auth.json.*|agent-tools.json|agent-tools.json.*|agent-launch-checks.json|agent-launch-checks.json.*|wip.json|wip.json.*|workers.json|workers.json.*)
      return 0 ;;
  esac
  return 1
}

own_host() { # path
  set_owner "$1" "$host_uid" "$host_gid"
  # Symbolic, with -s: GNU chmod keeps a directory's setgid bit through an octal mode, and a bit that
  # never clears would count as a change on every start.
  find "$1" -type d ! -perm 700 -print -exec chmod u=rwx,go=,ug-s {} +
  find "$1" ! -type d ! -type l ! -perm 600 -print -exec chmod 600 {} +
}

own_plugins() { # path
  set_owner "$1" "$host_uid" "$agent_gid"
  find "$1" -type d ! -perm 750 -print -exec chmod u=rwx,g=rx,o=,ug-s {} +
  # .rescan and .rescan-report.json are the operator CLI's request and the host's answer; the answer
  # names members of every team and stays the host's alone, so their modes are left as written.
  find "$1" ! -type d ! -type l ! -name '.rescan*' \( -perm /6027 -o ! -perm -640 \) -print -exec chmod u+rw,g+r,g-w,o=,ug-s {} +
}

own_repos() { # path
  set_owner "$1" "$host_uid" "$agent_gid"
  find "$1" -type d ! -perm 2750 -print -exec chmod u=rwx,g=rxs,o=,u-s {} +
  find "$1" ! -type d ! -type l \( -perm /6027 -o ! -perm -040 \) -print -exec chmod g+r,g-w,o=,ug-s {} +
}

own_agent() { # path
  set_owner "$1" "$agent_uid" "$agent_gid"
  if [ "$1" = "$root/agent-home" ]; then own_agent_home "$1"; return; fi
  find "$1" -type d ! -perm -2070 -print -exec chmod g+rwxs {} +
  find "$1" -type f ! -perm -060 -print -exec chmod g+rw {} +
}

# agent-home is NOT group-writable all the way down: the Claude login, ssh and gpg keys and whatever
# else a CLI keeps there stay at the modes their owner set (ssh refuses a group-readable key). Only
# the home itself - so the host can create .grok in it - and .grok, whose config.toml the host
# rewrites with the harness MCP entry, are the group's. Files directly in .grok get g+rw; what is
# deeper (grok's own bin) keeps its modes.
own_agent_home() { # path
  find "$1" -maxdepth 0 -type d ! -perm -2070 -print -exec chmod g+rwxs {} +
  [ -d "$1/.grok" ] && [ ! -L "$1/.grok" ] || return 0
  find "$1/.grok" -maxdepth 0 ! -perm -2070 -print -exec chmod g+rwxs {} +
  find "$1/.grok" -mindepth 1 -maxdepth 1 -type f ! -perm -060 -print -exec chmod g+rw {} +
}

# The file-browser roots from the environment, one per line, that are safe to hand to agent. Each
# is resolved first: //etc, /./etc or a symlink to /etc is /etc, and the check is on that.
extra_roots() {
  data=$(realpath -e -- "$root")
  env | sed -n 's/^FileBrowser__Roots__[0-9][0-9]*__Path=//p' | while IFS= read -r path; do
    case "$path" in
      /*) ;;
      *) log "file-browser root '$path' left alone: not an absolute path" >&2; continue ;;
    esac
    resolved=$(realpath -e -- "$path" 2>/dev/null) || {
      log "file-browser root '$path' left alone: it does not resolve to an existing path" >&2; continue; }
    case "$resolved" in
      /|/bin|/boot|/etc|/home|/lib*|/media|/mnt|/opt|/root|/run|/sbin|/srv|/tmp|/usr|/var|/app|\
      /proc|/proc/*|/sys|/sys/*|/dev|/dev/*|/usr/*|/etc/*|/bin/*|/sbin/*|/lib*/*|/boot/*|/app/*|/opt/*|/root/*|/run/*|/var/*)
        log "file-browser root '$path' left alone: a system directory ($resolved)" >&2; continue ;;
      "$data"|"$data"/*) continue ;;
    esac
    [ -d "$resolved" ] || { log "file-browser root '$path' left alone: not a directory" >&2; continue; }
    printf '%s\n' "$resolved"
  done
}

ownership() {
  if [ ! -d "$root" ]; then
    log "$root is not a directory; ownership left alone"
    return 1
  fi

  changed=$(
    {
      [ "$(stat -c '%u:%g' "$root")" = "$host_uid:$agent_gid" ] || { "$chown_cmd" "$host_uid:$agent_gid" "$root"; echo "$root"; }
      [ "$(stat -c '%a' "$root")" = 750 ] || { chmod u=rwx,g=rx,o=,ug-s "$root"; echo "$root"; }

      [ -e "$root/plugins" ] || [ -L "$root/plugins" ] || { mkdir "$root/plugins"; echo "$root/plugins"; }
      [ -e "$root/repos" ] || [ -L "$root/repos" ] || { mkdir "$root/repos"; echo "$root/repos"; }
      [ -e "$root/tmp" ] || [ -L "$root/tmp" ] || { mkdir "$root/tmp"; echo "$root/tmp"; }

      for path in "$root"/* "$root"/.[!.]* "$root"/..?*; do
        [ -e "$path" ] || [ -L "$path" ] || continue
        name=$(basename "$path")
        if is_host_entry "$name"; then own_host "$path"
        elif [ "$name" = plugins ]; then own_plugins "$path"
        elif [ "$name" = repos ]; then own_repos "$path"
        else own_agent "$path"; fi
      done

      extra_roots | while IFS= read -r path; do
        log "file-browser root $path is agent's" >&2
        own_agent "$path"
      done
    } | wc -l
  )

  log "ownership set: $changed change(s) (0 means the volume already matched)"
}

# A name the setting accepts, and nothing else: TenantSettings holds the same pattern
# (SystemPackages.IsValidName). This line is what stands between a root shell and the file.
valid_package() {
  printf '%s\n' "$1" | grep -Eqx '[a-z0-9][a-z0-9+.-]{1,99}'
}

packages() {
  file="$root/system-packages"
  if [ ! -f "$file" ]; then
    log "no system packages to install"
    return 0
  fi

  wanted=""
  while IFS= read -r line || [ -n "$line" ]; do
    line=$(printf '%s' "$line" | tr -d '\r' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    case "$line" in ''|'#'*) continue ;; esac
    if valid_package "$line"; then
      wanted="$wanted $line"
    else
      log "system packages: refused '$line': not a package name"
    fi
  done < "$file"

  if [ -z "$wanted" ]; then
    log "no system packages to install"
    return 0
  fi

  log "system packages: installing$wanted"
  # $wanted is unquoted on purpose: one argument per name, and every name passed the pattern above,
  # so none holds a space, a glob character or a leading dash.
  # shellcheck disable=SC2086
  if "$apt_cmd" update && DEBIAN_FRONTEND=noninteractive "$apt_cmd" install -y --no-install-recommends -- $wanted; then
    log "system packages: installed"
  else
    log "system packages: FAILED to install. The host will start anyway; see the lines above."
    return 1
  fi
}

case "${1:-}" in
  ownership) ownership ;;
  packages) packages ;;
  # The file-browser roots the ownership step would hand to agent, and nothing else done: a test
  # asks this about system paths, so a broken check can never chmod the machine it runs on.
  roots) extra_roots ;;
  *) echo "usage: $0 ownership|packages|roots" >&2; exit 2 ;;
esac

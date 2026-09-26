#!/bin/sh
# Runs before the host, every start.
#
# The image contains git, curl, and Node. The agent CLIs are downloaded onto the
# data volume (/data), so replacing the image does not make the board forget them.
# A CLI that is already on the volume is left alone. A download that fails is
# reported and does not stop the host: the board is more useful than a container
# that refuses to boot because one vendor was unreachable.
#
# Set HARNESS_REINSTALL_AGENTS=1 to download them again.

set -u

export HOME="${HOME:-/data/agent-home}"
export NPM_CONFIG_PREFIX=/data/npm-global
export PATH="/data/agent-home/.grok/bin:/data/npm-global/bin:/data/bin:${PATH:-/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin}"

mkdir -p /data/bin /data/npm-global /data/agent-home/.grok/bin

reinstall=0
if [ "${HARNESS_REINSTALL_AGENTS:-}" = "1" ]; then
  reinstall=1
fi

install_npm() {
  name="$1"
  package="$2"
  target="/data/npm-global/bin/$name"

  if [ "$reinstall" -eq 0 ] && [ -x "$target" ]; then
    echo "agent cli: $name is already installed"
    return 0
  fi

  echo "agent cli: installing $name ($package)"
  # An install interrupted part-way (a CLI updating itself when the container was replaced) leaves
  # a folder npm then fails to rename onto, on every start. See npm-torn-install.sh.
  sh "$(dirname "$0")/npm-torn-install.sh" /data/npm-global "$package"
  if npm install -g "$package"; then
    if [ -x "$target" ]; then
      echo "agent cli: $name installed"
    else
      echo "agent cli: $package installed, but $target is not executable"
    fi
  else
    echo "agent cli: FAILED to install $name. The board will still start."
  fi
}

# Grok does not take Claude's --mcp-config flag. It reads MCP servers from its own
# config and expands ${HARNESS_KEY} when the Concierge starts. The key is minted
# per session, so it stays in the environment and never in this file.
ensure_grok_mcp() {
  config="$HOME/.grok/config.toml"
  mkdir -p "$HOME/.grok"
  if [ -f "$config" ] && grep -q '\[mcp_servers\.harness\]' "$config"; then
    echo "agent cli: grok already has the harness MCP server"
    return 0
  fi

  listen="${ASPNETCORE_URLS:-http://127.0.0.1:8080}"
  listen="${listen%%;*}"
  listen=$(printf '%s' "$listen" | sed \
    -e 's#://0.0.0.0:#://127.0.0.1:#' \
    -e 's#://\*#://127.0.0.1#' \
    -e 's#://+#://127.0.0.1#')
  listen="${listen%/}/mcp"

  touch "$config"
  printf '\n[mcp_servers.harness]\nurl = "%s"\nenabled = true\nheaders = { "X-Api-Key" = "${HARNESS_KEY}" }\n' "$listen" >> "$config"
  echo "agent cli: pointed grok at the harness MCP server ($listen)"
}

install_grok() {
  target="$HOME/.grok/bin/grok"

  if [ "$reinstall" -eq 0 ] && [ -x "$target" ]; then
    echo "agent cli: grok is already installed"
    return 0
  fi

  echo "agent cli: installing grok from https://x.ai/cli/install.sh"
  if curl -fsSL https://x.ai/cli/install.sh | bash; then
    if [ -x "$target" ]; then
      echo "agent cli: grok installed"
    else
      echo "agent cli: the grok installer finished, but $target is missing"
    fi
  else
    echo "agent cli: FAILED to install grok. The board will still start."
  fi
}

install_npm claude @anthropic-ai/claude-code
install_npm codex @openai/codex
install_npm copilot @github/copilot
install_grok
ensure_grok_mcp

# Headless Chromium for agents testing web apps, onto the volume (PLAYWRIGHT_BROWSERS_PATH), once.
install_browser() {
  export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-/data/ms-playwright}"
  if [ "$reinstall" -eq 0 ] && ls -d "$PLAYWRIGHT_BROWSERS_PATH"/chromium_headless_shell-* >/dev/null 2>&1; then
    echo "agent cli: headless chromium is already installed"
    return 0
  fi
  echo "agent cli: installing headless chromium into $PLAYWRIGHT_BROWSERS_PATH"
  if npx -y playwright install chromium; then
    echo "agent cli: headless chromium installed"
  else
    echo "agent cli: FAILED to install headless chromium. The board will still start."
  fi
}
install_browser

# WHICH VERSIONS THIS START HAS, one JSON line per start, on the volume. The host reads
# it for Admin > Diagnostics. A CLI that is not installed is null; one that prints nothing is said
# so rather than left blank. Only the newest 200 starts are kept.
record_versions() {
  file="${CLI_VERSIONS_FILE:-/data/cli-versions.jsonl}"
  line="{\"at\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",\"versions\":{"
  sep=""
  for name in claude codex copilot grok agy gh git node; do
    if command -v "$name" >/dev/null 2>&1; then
      # First line only, quotes and backslashes and control characters dropped, so the value is a
      # JSON string without an encoder.
      version=$(timeout 30 "$name" --version </dev/null 2>/dev/null | head -n 1 | tr -d '"\\' | tr -d '[:cntrl:]' | cut -c 1-120)
      [ -n "$version" ] || version="(installed, no version reported)"
      value="\"$version\""
    else
      value=null
    fi
    line="$line$sep\"$name\":$value"
    sep=","
  done
  chromium=$(ls -d "${PLAYWRIGHT_BROWSERS_PATH:-/data/ms-playwright}"/chromium_headless_shell-* 2>/dev/null | tail -n 1)
  if [ -n "$chromium" ]; then
    line="$line,\"chromium\":\"$(basename "$chromium" | tr -d '"\\')\""
  else
    line="$line,\"chromium\":null"
  fi
  line="$line}}"

  printf '%s\n' "$line" >> "$file"
  # Rewritten in place, not moved over: this runs as `agent`, which may write the file but may
  # not create or replace an entry in the data root.
  kept=$(tail -n 200 "$file") && printf '%s\n' "$kept" > "$file"
  echo "agent cli: recorded this start's versions in $file"
}
record_versions || echo "agent cli: could not record this start's versions"

# Which agents are not signed in yet. Signed in is a key in the environment OR a saved login on
# the volume, the same two things auth-probes.json names for the Host's own probe: a person who
# signed Grok in through the Concierge has no XAI_API_KEY and is signed in all the same.
note_sign_in() {
  name=$1 variable=$2 file=$3
  eval "key=\${$variable:-}"
  [ -n "$key" ] && return 0
  [ -s "$HOME/$file" ] && return 0
  echo "agent cli: $name is not signed in yet: sign it in from the board (the Concierge can do it for you), or give the container $variable"
}
note_sign_in claude ANTHROPIC_API_KEY .claude/.credentials.json
note_sign_in codex OPENAI_API_KEY .codex/auth.json
[ -n "${GITHUB_TOKEN:-}" ] || note_sign_in copilot GH_TOKEN .copilot/config.json
note_sign_in grok XAI_API_KEY .grok/auth.json

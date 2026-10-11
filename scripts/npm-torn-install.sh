#!/bin/sh
# Clears what an interrupted `npm install -g <package>` leaves, so the next install can run.
#
#   npm-torn-install.sh <npm prefix> <package>
#
# npm moves the old copy aside to `.<name>-<random>` beside it before writing the new one. An
# install killed between the two (e.g. an agent CLI updating itself while the container is
# replaced) leaves that folder and a partial package, and the command in bin/ points at nothing.
# Every later `npm install -g` of that package then fails with ENOTEMPTY renaming onto the folder
# that is still there, so the CLI never comes back by itself. ensure-agent-clis.sh calls this just
# before it (re)installs a package, which it does only when the command is not executable or a
# reinstall was asked for: the package folder is replaced either way, so removing it costs nothing.
#
# Removes only that package's folder and its own `.<name>-*` siblings. Prints each one removed.

set -u

prefix="$1"
package="$2"

dir="$prefix/lib/node_modules/$package"
parent=$(dirname "$dir")
base=$(basename "$dir")

[ -d "$parent" ] || exit 0

for leftover in "$parent/.$base-"*; do
  [ -e "$leftover" ] || continue
  rm -rf "$leftover"
  echo "agent cli: removed $leftover, left by an interrupted install of $package"
done

if [ -e "$dir" ]; then
  rm -rf "$dir"
  echo "agent cli: removed $dir before installing $package again"
fi

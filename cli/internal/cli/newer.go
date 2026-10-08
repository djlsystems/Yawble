package cli

import (
	"context"
	"fmt"
	"io"
	"runtime"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/release"
)

// newerReleaseWait bounds the look for a newer release, so a slow or absent network never holds
// up `yawble` or `yawble version` for more than a moment.
const newerReleaseWait = 3 * time.Second

// sayIfNewer tells a person reading a terminal that a newer release is out and how to update to
// it. It says nothing when the look fails, takes too long, finds nothing newer, when output is not
// a terminal (a script reads only what it asked for) or for a development build with no version.
// A build with build metadata (2026.10.07.3+4.38af38d, a build from a later commit) counts as its
// base release: it is offered only a release newer than that base.
func sayIfNewer(ctx context.Context, deps Deps, out io.Writer) {
	if !deps.StdoutTerminal || buildinfo.Version == "dev" {
		return
	}
	current, _, _ := strings.Cut(buildinfo.Version, "+")
	base := deps.ReleaseBaseURL
	if base == "" {
		base = release.DefaultBaseURL
	}
	goos := deps.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	ctx, cancel := context.WithTimeout(ctx, newerReleaseWait)
	defer cancel()
	rel, err := release.Latest(ctx, deps.HTTP, base, githubToken(deps), goos, runtime.GOARCH, true)
	if err != nil || rel.Tag == "" || !release.IsNewer(rel.Tag, current) {
		return
	}
	command := "yawble update"
	if rel.Prerelease {
		command += " --prerelease"
	}
	fmt.Fprintln(out)
	fmt.Fprintf(out, "A newer yawble is out: %s (this is %s).\n", rel.Tag, buildinfo.Version)
	fmt.Fprintf(out, "Update yawble and the instance:  %s\n", command)
}

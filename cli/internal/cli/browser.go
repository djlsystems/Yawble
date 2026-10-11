package cli

import (
	"context"
	"runtime"
)

// openBrowser hands the URL to the desktop's opener. Best effort: a failure is nobody's problem,
// the URL was printed anyway. Only ever called from an interactive `up`; a script does not want
// a window.
func openBrowser(ctx context.Context, deps Deps, url string) {
	goos := deps.GOOS
	if goos == "" {
		goos = runtime.GOOS
	}
	r := runnerOf(deps)
	switch goos {
	case "darwin":
		_, _ = r.Run(ctx, "open", url)
	case "windows":
		_, _ = r.Run(ctx, "rundll32", "url.dll,FileProtocolHandler", url)
	default:
		_, _ = r.Run(ctx, "xdg-open", url)
	}
}

package engine

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"
)

// GitHubRegistry is where the Yawble image is published. It is the only registry a GitHub
// token is ever handed to.
const GitHubRegistry = "ghcr.io"

// login is `<engine> login <registry> -u <user> --password-stdin` for both engines: the same
// verb and flags in Podman and Docker.
func login(ctx context.Context, r Runner, program, registry, user, password string) error {
	in, ok := r.(InputRunner)
	if !ok {
		return errors.New("this runner cannot pass a password on stdin; refusing to put it on the command line")
	}
	args := []string{"login", registry, "-u", user, "--password-stdin"}
	res, err := in.RunInput(ctx, password, program, args...)
	if err != nil {
		return &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		return fmt.Errorf("%s login %s: %s (exit %d)", program, registry, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return nil
}

// WithRegistryToken is e, except that a pull from ghcr.io logs the engine in with token first,
// once per process. While the repository and its image are private that is the only way the
// pull succeeds; once the image is public no token is set and this is e unchanged. A pull from
// any other registry never sees the token.
func WithRegistryToken(e Engine, token string) Engine {
	if token == "" {
		return e
	}
	return &tokenEngine{Engine: e, token: token}
}

type tokenEngine struct {
	Engine
	token    string
	loggedIn bool
}

func (t *tokenEngine) Pull(ctx context.Context, ref string, progress io.Writer) error {
	if registryOf(ref) == GitHubRegistry && !t.loggedIn {
		// GitHub ignores the user name for a token; this is the name its own docs use.
		if err := t.Engine.Login(ctx, GitHubRegistry, "x-access-token", t.token); err != nil {
			return fmt.Errorf("logging in to %s with GH_TOKEN failed (%w); the token needs the read:packages scope, or unset GH_TOKEN if the image is public", GitHubRegistry, err)
		}
		t.loggedIn = true
	}
	return t.Engine.Pull(ctx, ref, progress)
}

// registryOf is the host part of an image reference: the first path element when it looks like
// a host (has a dot or a port, or is localhost), else Docker Hub.
func registryOf(ref string) string {
	first, _, found := strings.Cut(ref, "/")
	if !found || (!strings.ContainsAny(first, ".:") && first != "localhost") {
		return "docker.io"
	}
	return first
}

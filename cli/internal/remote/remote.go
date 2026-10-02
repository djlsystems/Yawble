package remote

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// SidecarName is the tunnel container. One per instance: enable replaces it.
const SidecarName = instance.TunnelName

// Label marks the sidecar with its provider so status and doctor can name it.
const Label = "yawble.remote"

// ErrInstanceNotRunning is a tunnel asked for with nothing behind it.
var ErrInstanceNotRunning = errors.New("the instance is not running, so there is nothing to put on the internet; run `yawble up` first")

// Polling for the URL in the sidecar's log, and for tailscale coming up. cloudflared prints its
// quick-tunnel name within a few seconds; ngrok its url line at once.
var (
	urlInterval = 2 * time.Second
	urlTimeout  = 60 * time.Second
)

// Target is the Host's address as the sidecar sees it: the pod's loopback under Podman, the
// container's name on the shared network under Docker.
func Target(e engine.Engine) string {
	if e.Name() == "docker" {
		return fmt.Sprintf("http://%s:%d", instance.ContainerName, instance.ContainerPort)
	}
	return fmt.Sprintf("http://127.0.0.1:%d", instance.ContainerPort)
}

// Enable runs the sidecar and answers the public URL when the provider prints one. An existing
// sidecar is removed first, so there is never more than one. The credential reaches the sidecar
// through an owner-only env file in dir, never on the engine's command line.
func Enable(ctx context.Context, e engine.Engine, p Provider, cred Credential, dir string, out io.Writer) (string, error) {
	state, err := e.ContainerState(ctx, instance.ContainerName)
	if err != nil {
		return "", err
	}
	if state != engine.StateRunning {
		return "", ErrInstanceNotRunning
	}
	if p.NeedsCredential() && cred.Token == "" {
		return "", fmt.Errorf("%s needs a token", p.Name())
	}
	existing, err := e.ContainerState(ctx, SidecarName)
	if err != nil {
		return "", err
	}
	if existing != engine.StateAbsent {
		fmt.Fprintf(out, "replacing the existing %s sidecar\n", SidecarName)
		if err := e.Remove(ctx, SidecarName); err != nil {
			return "", err
		}
	}
	if present, err := e.ImagePresent(ctx, p.Image()); err != nil {
		return "", err
	} else if !present {
		fmt.Fprintf(out, "pulling %s\n", p.Image())
		if err := e.Pull(ctx, p.Image(), out); err != nil {
			return "", err
		}
	}
	envFile, err := WriteEnvFile(dir, p.Env(cred))
	if err != nil {
		return "", err
	}
	spec := engine.RunSpec{
		Name:     SidecarName,
		Pod:      instance.PodName,
		Image:    p.Image(),
		EnvFiles: []string{envFile},
		Labels:   map[string]string{Label: p.Name()},
		Command:  p.Args(Target(e), cred),
	}
	if p.Name() == "tailscale" {
		spec.Volumes = []string{"yawble-tailscale:/var/lib/tailscale"}
	}
	if err := e.Run(ctx, spec); err != nil {
		return "", err
	}
	fmt.Fprintf(out, "started %s (%s)\n", SidecarName, p.Name())
	for _, n := range p.Notes(cred) {
		fmt.Fprintln(out, "note:", n)
	}
	if p.Name() == "tailscale" {
		return tailscaleServe(ctx, e, cred, out)
	}
	if !p.PrintsURL(cred) {
		return "", nil
	}
	return waitForURL(ctx, e, p)
}

// sidecarAlive answers an error carrying the last log lines when the sidecar is not running.
func sidecarAlive(ctx context.Context, e engine.Engine) error {
	state, err := e.ContainerState(ctx, SidecarName)
	if err != nil {
		return err
	}
	if state == engine.StateRunning {
		return nil
	}
	var logs strings.Builder
	_ = e.Logs(ctx, SidecarName, false, 20, &logs)
	tail := strings.TrimSpace(logs.String())
	if len(tail) > 600 {
		tail = tail[len(tail)-600:]
	}
	return fmt.Errorf("%s stopped (%s). Its last lines:\n%s", SidecarName, state, tail)
}

func waitForURL(ctx context.Context, e engine.Engine, p Provider) (string, error) {
	deadline := time.Now().Add(urlTimeout)
	for {
		if err := sidecarAlive(ctx, e); err != nil {
			return "", err
		}
		var logs strings.Builder
		_ = e.Logs(ctx, SidecarName, false, 200, &logs)
		if url, ok := p.URLFromLogs(logs.String()); ok {
			return url, nil
		}
		if time.Now().After(deadline) {
			return "", fmt.Errorf("%s is running but printed no URL within %s; read its log with: %s logs %s", p.Name(), urlTimeout, e.Name(), SidecarName)
		}
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(urlInterval):
		}
	}
}

// tailscaleServe turns on serve (or funnel) inside the sidecar once the node is up, pointing at
// the Host's address on the shared network, then reads the node's HTTPS name. Each attempt is
// bounded: `tailscale serve` can wait forever printing an enable link, and that link must reach
// the person rather than a hung prompt.
func tailscaleServe(ctx context.Context, e engine.Engine, cred Credential, out io.Writer) (string, error) {
	verb := "serve"
	if cred.Funnel {
		verb = "funnel"
	}
	target := Target(e)
	deadline := time.Now().Add(urlTimeout)
	var last string
	for {
		if err := sidecarAlive(ctx, e); err != nil {
			return "", err
		}
		attempt, cancel := context.WithTimeout(ctx, 20*time.Second)
		res, err := e.Exec(attempt, SidecarName, "tailscale", verb, "--bg", target)
		cancel()
		if err == nil {
			break
		}
		last = strings.TrimSpace(res.Stdout + "\n" + err.Error())
		if time.Now().After(deadline) {
			return "", fmt.Errorf("tailscale %s did not come up within %s; is the auth key valid, and is HTTPS enabled for your tailnet? tailscale said:\n%s", verb, urlTimeout, last)
		}
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(urlInterval):
		}
	}
	return tailscaleURL(ctx, e)
}

func tailscaleURL(ctx context.Context, e engine.Engine) (string, error) {
	res, err := e.Exec(ctx, SidecarName, "tailscale", "status", "--json")
	if err != nil {
		return "", err
	}
	var status struct {
		Self struct {
			DNSName string `json:"DNSName"`
		} `json:"Self"`
	}
	if err := json.Unmarshal([]byte(res.Stdout), &status); err != nil {
		return "", fmt.Errorf("tailscale status could not be read: %w", err)
	}
	name := strings.TrimSuffix(status.Self.DNSName, ".")
	if name == "" {
		return "", errors.New("tailscale status named no DNS name yet; try `yawble remote status` in a moment")
	}
	return "https://" + name, nil
}

// Disable removes the sidecar. The credential stays unless forget is set.
func Disable(ctx context.Context, e engine.Engine, dir string, forget bool) error {
	state, err := e.ContainerState(ctx, SidecarName)
	if err != nil {
		return err
	}
	if state != engine.StateAbsent {
		if err := e.Remove(ctx, SidecarName); err != nil {
			return err
		}
	}
	if forget {
		return Forget(dir)
	}
	return nil
}

// State is what `remote status` and doctor report. URLFromRecord says the URL is the last one
// seen at enable rather than read from the sidecar just now.
type State struct {
	Provider      string       `json:"provider"`
	State         engine.State `json:"state"`
	URL           string       `json:"url"`
	URLFromRecord bool         `json:"urlFromRecord,omitempty"`
}

// Status asks the engine about the sidecar and, when it runs, reads its URL; when the log no
// longer carries one, the URL recorded at enable stands in and is marked as such.
func Status(ctx context.Context, e engine.Engine, p Provider, lastURL string) (State, error) {
	st := State{Provider: p.Name()}
	info, err := e.Inspect(ctx, SidecarName)
	if err != nil {
		return st, err
	}
	st.State = info.State
	if info.State != engine.StateRunning {
		return st, nil
	}
	if p.Name() == "tailscale" {
		if url, err := tailscaleURL(ctx, e); err == nil {
			st.URL = url
			return st, nil
		}
	} else {
		var logs strings.Builder
		_ = e.Logs(ctx, SidecarName, false, 200, &logs)
		if url, ok := p.URLFromLogs(logs.String()); ok {
			st.URL = url
			return st, nil
		}
	}
	if lastURL != "" {
		st.URL, st.URLFromRecord = lastURL, true
	}
	return st, nil
}

// Package remote puts the instance on the internet through a tunnel sidecar that runs beside the
// Host, reaches it over the shared network, makes only outbound connections, and is started and
// stopped with the instance. A Provider is one adapter: which image, which arguments, which
// credential, and how to read the public URL. Everything else is shared.
package remote

import (
	"fmt"
	"regexp"
	"strings"
)

// Credential is what a provider needs from the person. Token is the tunnel token (Cloudflare),
// auth key (Tailscale) or authtoken (ngrok). Domain is ngrok's static domain. Funnel opens a
// Tailscale node to the public internet and is off unless asked.
type Credential struct {
	Token  string `toml:"token"`
	Domain string `toml:"domain"`
	Funnel bool   `toml:"funnel"`
}

type Provider interface {
	Name() string
	Image() string
	// Args for the sidecar, given the Host's address on the shared network.
	Args(target string, cred Credential) []string
	// Env carries the credential. It reaches the sidecar through an owner-only env file, never
	// on a command line.
	Env(cred Credential) map[string]string
	// NeedsCredential says whether Enable must have a token before it can start anything.
	NeedsCredential() bool
	// PrintsURL says whether the sidecar's log will carry the public URL for this credential.
	// A named Cloudflare tunnel never prints one; its hostname lives in the dashboard.
	PrintsURL(cred Credential) bool
	// URLFromLogs finds the NEWEST public URL in what the sidecar printed. Logs survive
	// restarts and a quick tunnel is relabelled on each, so the last match is the live one.
	URLFromLogs(logs string) (string, bool)
	// Notes are the sentences a person should read once, on enable.
	Notes(cred Credential) []string
}

var names = []string{"cloudflare", "tailscale", "ngrok"}

// ProviderNamed answers the adapter for a name, or an error listing the three.
func ProviderNamed(name string) (Provider, error) {
	switch strings.ToLower(name) {
	case "cloudflare":
		return cloudflare{}, nil
	case "tailscale":
		return tailscale{}, nil
	case "ngrok":
		return ngrok{}, nil
	}
	return nil, fmt.Errorf("unknown provider %q; the providers are %s", name, strings.Join(names, ", "))
}

func lastMatch(re *regexp.Regexp, logs string, group int) (string, bool) {
	matches := re.FindAllStringSubmatch(logs, -1)
	if len(matches) == 0 {
		return "", false
	}
	return matches[len(matches)-1][group], true
}

// cloudflare: a quick tunnel with no account (a random trycloudflare.com name that changes on
// every start), or a named tunnel with a token from the Cloudflare dashboard, whose hostname is
// configured there and is not visible from here.
type cloudflare struct{}

func (cloudflare) Name() string  { return "cloudflare" }
func (cloudflare) Image() string { return "docker.io/cloudflare/cloudflared:latest" }
func (cloudflare) Args(target string, cred Credential) []string {
	if cred.Token != "" {
		return []string{"tunnel", "--no-autoupdate", "run"}
	}
	return []string{"tunnel", "--no-autoupdate", "--url", target}
}
func (cloudflare) Env(cred Credential) map[string]string {
	if cred.Token == "" {
		return nil
	}
	return map[string]string{"TUNNEL_TOKEN": cred.Token}
}
func (cloudflare) NeedsCredential() bool          { return false }
func (cloudflare) PrintsURL(cred Credential) bool { return cred.Token == "" }

// The quick-tunnel name, and NOT api.trycloudflare.com, which a failed request names in the
// error cloudflared logs ("failed to request quick Tunnel: Post https://api.trycloudflare.com").
var trycloudflare = regexp.MustCompile(`https://((?:[a-z0-9-]+)\.trycloudflare\.com)`)

func (cloudflare) URLFromLogs(logs string) (string, bool) {
	matches := trycloudflare.FindAllStringSubmatch(logs, -1)
	for i := len(matches) - 1; i >= 0; i-- {
		if !strings.HasPrefix(matches[i][1], "api.") {
			return matches[i][0], true
		}
	}
	return "", false
}
func (cloudflare) Notes(cred Credential) []string {
	if cred.Token != "" {
		return []string{"a named tunnel: the public hostname is the one you gave this tunnel in the Cloudflare dashboard; consider Cloudflare Access in front of it"}
	}
	return []string{"a quick tunnel: no account, and the name changes every time the sidecar starts; for a stable name create a tunnel in the Cloudflare dashboard and run `yawble remote enable cloudflare --token <token>`"}
}

// tailscale: the instance joins your tailnet as a node named yawble and serves the board over
// HTTPS to devices signed into the tailnet; --funnel opens it publicly. Userspace networking,
// because the sidecar shares the Host's network namespace and has no TUN device.
type tailscale struct{}

const tailscaleSocket = "/var/run/tailscale/tailscaled.sock"

func (tailscale) Name() string                     { return "tailscale" }
func (tailscale) Image() string                    { return "docker.io/tailscale/tailscale:latest" }
func (tailscale) Args(string, Credential) []string { return nil }
func (tailscale) Env(cred Credential) map[string]string {
	return map[string]string{
		"TS_AUTHKEY":   cred.Token,
		"TS_HOSTNAME":  "yawble",
		"TS_USERSPACE": "1",
		"TS_STATE_DIR": "/var/lib/tailscale",
		"TS_SOCKET":    tailscaleSocket,
	}
}
func (tailscale) NeedsCredential() bool             { return true }
func (tailscale) PrintsURL(Credential) bool         { return false }
func (tailscale) URLFromLogs(string) (string, bool) { return "", false }
func (tailscale) Notes(cred Credential) []string {
	notes := []string{"reachable only from devices signed into your tailnet"}
	if cred.Funnel {
		notes = append(notes, "funnel is ON: the board is reachable from the public internet through Tailscale")
	}
	return notes
}

// ngrok: the agent with your authtoken; a static domain when you have one. The free tier shows
// visitors an interstitial page before the board.
type ngrok struct{}

func (ngrok) Name() string  { return "ngrok" }
func (ngrok) Image() string { return "docker.io/ngrok/ngrok:latest" }
func (ngrok) Args(target string, cred Credential) []string {
	args := []string{"http", "--log", "stdout", "--log-format", "logfmt"}
	if cred.Domain != "" {
		args = append(args, "--url", cred.Domain)
	}
	return append(args, target)
}
func (ngrok) Env(cred Credential) map[string]string {
	return map[string]string{"NGROK_AUTHTOKEN": cred.Token}
}
func (ngrok) NeedsCredential() bool     { return true }
func (ngrok) PrintsURL(Credential) bool { return true }

var ngrokURL = regexp.MustCompile(`url=(https://\S+)`)

func (ngrok) URLFromLogs(logs string) (string, bool) { return lastMatch(ngrokURL, logs, 1) }
func (ngrok) Notes(cred Credential) []string {
	notes := []string{"ngrok's free tier shows visitors an interstitial page before the board"}
	if cred.Domain == "" {
		notes = append(notes, "the URL changes on every start; `--domain <your static domain>` keeps it")
	}
	return notes
}

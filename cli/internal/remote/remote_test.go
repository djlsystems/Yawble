package remote_test

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/remote"
)

const (
	hostInspect   = "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble"
	tunnelInspect = "podman container inspect --format {{.State.Status}}|{{.ImageName}}|{{index .Config.Labels \"yawble.settings\"}} yawble-tunnel"
)

func fast(t *testing.T) { t.Cleanup(remote.SetPollingForTests(time.Millisecond, 200*time.Millisecond)) }

func TestEachProviderIsAnImageArgsAndAnEnvironment(t *testing.T) {
	cf, _ := remote.ProviderNamed("cloudflare")
	if cf.Image() != "docker.io/cloudflare/cloudflared:latest" {
		t.Errorf("cloudflare image %q", cf.Image())
	}
	if got := strings.Join(cf.Args("http://127.0.0.1:8080", remote.Credential{}), " "); got != "tunnel --no-autoupdate --url http://127.0.0.1:8080" {
		t.Errorf("quick tunnel args %q", got)
	}
	if got := strings.Join(cf.Args("http://127.0.0.1:8080", remote.Credential{Token: "tok"}), " "); got != "tunnel --no-autoupdate run" {
		t.Errorf("named tunnel args %q", got)
	}
	if env := cf.Env(remote.Credential{Token: "tok"}); env["TUNNEL_TOKEN"] != "tok" {
		t.Errorf("cloudflare env %v", env)
	}
	ts, _ := remote.ProviderNamed("tailscale")
	env := ts.Env(remote.Credential{Token: "tskey-x"})
	if ts.Image() != "docker.io/tailscale/tailscale:latest" || env["TS_AUTHKEY"] != "tskey-x" || env["TS_USERSPACE"] != "1" || env["TS_HOSTNAME"] != "yawble" || env["TS_STATE_DIR"] == "" || env["TS_SOCKET"] == "" {
		t.Errorf("tailscale image %q env %v", ts.Image(), env)
	}
	if !ts.NeedsCredential() {
		t.Error("tailscale needs an auth key")
	}
	ng, _ := remote.ProviderNamed("ngrok")
	if got := strings.Join(ng.Args("http://127.0.0.1:8080", remote.Credential{Domain: "my.ngrok.app"}), " "); got != "http --log stdout --log-format logfmt --url my.ngrok.app http://127.0.0.1:8080" {
		t.Errorf("ngrok args %q", got)
	}
	if env := ng.Env(remote.Credential{Token: "ng"}); env["NGROK_AUTHTOKEN"] != "ng" {
		t.Errorf("ngrok env %v", env)
	}
	if _, err := remote.ProviderNamed("carrier-pigeon"); err == nil || !strings.Contains(err.Error(), "cloudflare, tailscale, ngrok") {
		t.Errorf("unknown provider: %v", err)
	}
}

func TestURLsAreReadFromWhatTheSidecarsPrintTakingTheNewest(t *testing.T) {
	cf, _ := remote.ProviderNamed("cloudflare")
	// A failed quick tunnel names Cloudflare's own API host: not a URL for anyone.
	if url, ok := cf.URLFromLogs(`ERR failed to request quick Tunnel: Post "https://api.trycloudflare.com/tunnel": dial tcp: lookup api.trycloudflare.com: no such host`); ok {
		t.Errorf("api host read as a URL: %q", url)
	}
	// Logs survive restarts and each start mints a new name: the newest wins.
	url, ok := cf.URLFromLogs("INF |  https://first-start.trycloudflare.com  |\n... restart ...\nINF |  https://second-start.trycloudflare.com  |\n")
	if !ok || url != "https://second-start.trycloudflare.com" {
		t.Errorf("cloudflare %q %v", url, ok)
	}
	ng, _ := remote.ProviderNamed("ngrok")
	url, ok = ng.URLFromLogs("t=1 msg=\"started tunnel\" url=https://old.ngrok-free.app\nt=2 msg=\"started tunnel\" url=https://new.ngrok-free.app\n")
	if !ok || url != "https://new.ngrok-free.app" {
		t.Errorf("ngrok %q %v", url, ok)
	}
	ts, _ := remote.ProviderNamed("tailscale")
	if _, ok := ts.URLFromLogs("anything"); ok {
		t.Error("tailscale's URL comes from tailscale status, not the log")
	}
}

func TestTheStoreKeepsTheCredentialOwnerOnly(t *testing.T) {
	dir := t.TempDir()
	if err := remote.Save(dir, remote.Config{Provider: "ngrok", Credential: remote.Credential{Token: "secret", Domain: "d"}, LastURL: "https://x"}); err != nil {
		t.Fatal(err)
	}
	c, ok, err := remote.Load(dir)
	if err != nil || !ok || c.Provider != "ngrok" || c.Credential.Token != "secret" || c.Credential.Domain != "d" || c.LastURL != "https://x" {
		t.Errorf("%+v %v %v", c, ok, err)
	}
	if runtime.GOOS != "windows" {
		info, _ := os.Stat(filepath.Join(dir, "remote.toml"))
		if info.Mode().Perm() != 0o600 {
			t.Errorf("mode %04o", info.Mode().Perm())
		}
	}
	if _, ok, err := remote.Load(t.TempDir()); ok || err != nil {
		t.Errorf("no file: %v %v", ok, err)
	}
}

func runningInstance() *engine.Scripted {
	s := engine.NewScripted()
	s.On(hostInspect, engine.Result{Stdout: "running|img|{}\n"})
	return s
}

func TestEnableRunsTheSidecarInThePodAndReadsTheURL(t *testing.T) {
	fast(t)
	s := runningInstance()
	s.OnSequence(tunnelInspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"},
	)
	s.On("podman image exists", engine.Result{ExitCode: 1})
	s.On("podman logs --tail 200 yawble-tunnel", engine.Result{Stdout: "INF |  https://quiet-owl.trycloudflare.com  |\n"})
	cf, _ := remote.ProviderNamed("cloudflare")
	var out bytes.Buffer
	url, err := remote.Enable(context.Background(), engine.NewPodman(s), cf, remote.Credential{}, t.TempDir(), &out)
	if err != nil {
		t.Fatal(err)
	}
	if url != "https://quiet-owl.trycloudflare.com" {
		t.Errorf("url %q", url)
	}
	c := strings.Join(s.Calls, "\n")
	for _, want := range []string{
		"podman pull docker.io/cloudflare/cloudflared:latest",
		"podman run -d --name yawble-tunnel --pod yawble --restart unless-stopped --label yawble.remote=cloudflare docker.io/cloudflare/cloudflared:latest tunnel --no-autoupdate --url http://127.0.0.1:8080",
	} {
		if !strings.Contains(c, want) {
			t.Errorf("missing %q in:\n%s", want, c)
		}
	}
}

// Review Focus 3: enable twice replaces the sidecar; two never run.
func TestEnableAgainReplacesTheExistingSidecar(t *testing.T) {
	fast(t)
	s := runningInstance()
	s.On(tunnelInspect, engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"})
	s.On("podman logs --tail 200 yawble-tunnel", engine.Result{Stdout: "https://second.trycloudflare.com\n"})
	cf, _ := remote.ProviderNamed("cloudflare")
	if _, err := remote.Enable(context.Background(), engine.NewPodman(s), cf, remote.Credential{}, t.TempDir(), &bytes.Buffer{}); err != nil {
		t.Fatal(err)
	}
	c := strings.Join(s.Calls, "\n")
	rm := strings.Index(c, "podman rm -f yawble-tunnel")
	run := strings.Index(c, "podman run -d --name yawble-tunnel")
	if rm < 0 || run < rm {
		t.Errorf("the old sidecar must go before the new one starts:\n%s", c)
	}
}

// Review Focus 4: no instance, no tunnel.
func TestEnableRefusesWhenTheInstanceIsNotRunning(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman container inspect", engine.Result{Stdout: "exited|img|{}\n"})
	cf, _ := remote.ProviderNamed("cloudflare")
	_, err := remote.Enable(context.Background(), engine.NewPodman(s), cf, remote.Credential{}, t.TempDir(), &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "yawble up") {
		t.Errorf("err %v", err)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "podman run") || strings.HasPrefix(c, "podman pull") {
			t.Errorf("created something: %q", c)
		}
	}
}

// The credential reaches the sidecar through an owner-only env file, not the engine's argv.
func TestTheCredentialTravelsInAnOwnerOnlyEnvFileNotOnTheCommandLine(t *testing.T) {
	fast(t)
	s := runningInstance()
	s.OnSequence(tunnelInspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|docker.io/ngrok/ngrok:latest|\n"},
	)
	s.On("podman image exists", engine.Result{})
	s.On("podman logs", engine.Result{Stdout: "url=https://x.ngrok-free.app\n"})
	dir := t.TempDir()
	ng, _ := remote.ProviderNamed("ngrok")
	_, err := remote.Enable(context.Background(), engine.NewPodman(s), ng, remote.Credential{Token: "SECRET-TOKEN"}, dir, &bytes.Buffer{})
	if err != nil {
		t.Fatal(err)
	}
	c := strings.Join(s.Calls, "\n")
	if strings.Contains(c, "SECRET-TOKEN") {
		t.Errorf("the token is on a command line:\n%s", c)
	}
	envFile := filepath.Join(dir, "remote.env")
	if !strings.Contains(c, "--env-file "+envFile) {
		t.Errorf("the sidecar should read %s:\n%s", envFile, c)
	}
	data, err := os.ReadFile(envFile)
	if err != nil || !strings.Contains(string(data), "NGROK_AUTHTOKEN=SECRET-TOKEN") {
		t.Errorf("env file %q %v", data, err)
	}
	if runtime.GOOS != "windows" {
		info, _ := os.Stat(envFile)
		if info.Mode().Perm() != 0o600 {
			t.Errorf("env file mode %04o", info.Mode().Perm())
		}
	}
}

// A sidecar that dies during the wait is said at once, with its last lines, and is not a URL.
func TestACrashedSidecarIsReportedNotWaitedFor(t *testing.T) {
	fast(t)
	s := runningInstance()
	s.OnSequence(tunnelInspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "exited|docker.io/cloudflare/cloudflared:latest|\n"},
	)
	s.On("podman image exists", engine.Result{})
	s.On("podman logs", engine.Result{Stdout: `ERR failed to request quick Tunnel: Post "https://api.trycloudflare.com/tunnel": no such host` + "\n"})
	cf, _ := remote.ProviderNamed("cloudflare")
	started := time.Now()
	_, err := remote.Enable(context.Background(), engine.NewPodman(s), cf, remote.Credential{}, t.TempDir(), &bytes.Buffer{})
	if err == nil || !strings.Contains(err.Error(), "stopped") || !strings.Contains(err.Error(), "failed to request quick Tunnel") {
		t.Errorf("err %v", err)
	}
	if time.Since(started) > 2*time.Second {
		t.Errorf("waited %s for a dead sidecar", time.Since(started))
	}
}

// A named Cloudflare tunnel prints no URL and is not waited for.
func TestANamedCloudflareTunnelReturnsAtOnceWithoutAURL(t *testing.T) {
	fast(t)
	s := runningInstance()
	s.OnSequence(tunnelInspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"},
	)
	s.On("podman image exists", engine.Result{})
	cf, _ := remote.ProviderNamed("cloudflare")
	url, err := remote.Enable(context.Background(), engine.NewPodman(s), cf, remote.Credential{Token: "tok"}, t.TempDir(), &bytes.Buffer{})
	if err != nil || url != "" {
		t.Errorf("url %q err %v", url, err)
	}
	if strings.Contains(strings.Join(s.Calls, "\n"), "podman logs") {
		t.Errorf("a named tunnel's logs hold no URL; they were read:\n%s", strings.Join(s.Calls, "\n"))
	}
}

func TestTailscaleServesTheHostAtTheEnginesTargetAndReadsItsName(t *testing.T) {
	fast(t)
	statusJSON := `{"Self":{"DNSName":"yawble.tail1234.ts.net.","HostName":"yawble"}}`
	// Podman: the pod's loopback.
	s := runningInstance()
	s.OnSequence(tunnelInspect,
		engine.Result{Stderr: "no such container", ExitCode: 125},
		engine.Result{Stdout: "running|docker.io/tailscale/tailscale:latest|\n"},
	)
	s.On("podman image exists", engine.Result{})
	s.On("podman exec yawble-tunnel tailscale status --json", engine.Result{Stdout: statusJSON})
	ts, _ := remote.ProviderNamed("tailscale")
	url, err := remote.Enable(context.Background(), engine.NewPodman(s), ts, remote.Credential{Token: "tskey"}, t.TempDir(), &bytes.Buffer{})
	if err != nil || url != "https://yawble.tail1234.ts.net" {
		t.Errorf("podman: url %q err %v", url, err)
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), "podman exec yawble-tunnel tailscale serve --bg http://127.0.0.1:8080") {
		t.Errorf("podman calls:\n%s", strings.Join(s.Calls, "\n"))
	}
	// Docker: the Host by name on the shared network, and funnel when asked.
	d := engine.NewScripted()
	d.On("docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} yawble", engine.Result{Stdout: "running|img|{}\n"})
	d.OnSequence("docker container inspect --format {{.State.Status}}|{{.Config.Image}}|{{index .Config.Labels \"yawble.settings\"}} yawble-tunnel",
		engine.Result{Stderr: "No such container", ExitCode: 1},
		engine.Result{Stdout: "running|docker.io/tailscale/tailscale:latest|\n"},
	)
	d.On("docker exec yawble-tunnel tailscale status --json", engine.Result{Stdout: statusJSON})
	url, err = remote.Enable(context.Background(), engine.NewDocker(d), ts, remote.Credential{Token: "tskey", Funnel: true}, t.TempDir(), &bytes.Buffer{})
	if err != nil || url != "https://yawble.tail1234.ts.net" {
		t.Errorf("docker: url %q err %v", url, err)
	}
	if !strings.Contains(strings.Join(d.Calls, "\n"), "docker exec yawble-tunnel tailscale funnel --bg http://yawble:8080") {
		t.Errorf("docker calls:\n%s", strings.Join(d.Calls, "\n"))
	}
}

func TestDisableRemovesTheSidecarAndForgetOnlyOnRequest(t *testing.T) {
	dir := t.TempDir()
	_ = remote.Save(dir, remote.Config{Provider: "cloudflare"})
	s := engine.NewScripted()
	s.On(tunnelInspect, engine.Result{Stdout: "running|x|\n"})
	if err := remote.Disable(context.Background(), engine.NewPodman(s), dir, false); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(strings.Join(s.Calls, "\n"), "podman rm -f yawble-tunnel") {
		t.Errorf("calls %q", s.Calls)
	}
	if _, ok, _ := remote.Load(dir); !ok {
		t.Error("disable without --forget must keep the credential")
	}
	if err := remote.Disable(context.Background(), engine.NewPodman(s), dir, true); err != nil {
		t.Fatal(err)
	}
	if _, ok, _ := remote.Load(dir); ok {
		t.Error("--forget must remove the file")
	}
	if _, err := os.Stat(filepath.Join(dir, "remote.env")); !os.IsNotExist(err) {
		t.Error("--forget must remove the env file too")
	}
}

func TestStatusReportsTheSidecarAndItsURLFallingBackToTheLastKnown(t *testing.T) {
	s := engine.NewScripted()
	s.On(tunnelInspect, engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"})
	s.On("podman logs", engine.Result{Stdout: "https://quiet-owl.trycloudflare.com\n"})
	cf, _ := remote.ProviderNamed("cloudflare")
	st, err := remote.Status(context.Background(), engine.NewPodman(s), cf, "https://old.trycloudflare.com")
	if err != nil || st.State != engine.StateRunning || st.URL != "https://quiet-owl.trycloudflare.com" {
		t.Errorf("%+v %v", st, err)
	}
	s2 := engine.NewScripted()
	s2.On(tunnelInspect, engine.Result{Stdout: "running|docker.io/cloudflare/cloudflared:latest|\n"})
	s2.On("podman logs", engine.Result{Stdout: "banner scrolled away\n"})
	st, _ = remote.Status(context.Background(), engine.NewPodman(s2), cf, "https://old.trycloudflare.com")
	if st.URL != "https://old.trycloudflare.com" || !st.URLFromRecord {
		t.Errorf("fallback: %+v", st)
	}
}

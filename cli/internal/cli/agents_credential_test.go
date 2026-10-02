package cli_test

import (
	"context"
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// credentialHost is a fake Host on the agent credential request file. It keeps ONE credential per
// command, as the Host does, and resolves a preset to the command it runs. A nil answer from
// refuse leaves the request unanswered.
type credentialHost struct {
	*engine.Scripted

	mu       sync.Mutex
	requests []map[string]any
	report   string
	commands map[string]string
	set      map[string]bool
	sources  map[string]string
	silent   bool
	// unsafe, when set, is the reason the Host deletes each request unanswered, as it does with
	// one it cannot answer safely; withNonce says whether its refusal names the request.
	unsafe    string
	withNonce bool
}

func newCredentialHost(t *testing.T) *credentialHost {
	t.Cleanup(cli.FastCredential(2 * time.Second))
	return &credentialHost{
		Scripted: runningScript(),
		commands: map[string]string{"claude": "claude", "claude-headless": "claude", "copilot": "copilot", "copilot-headless": "copilot"},
		set:      map[string]bool{},
		sources:  map[string]string{},
	}
}

func (h *credentialHost) answer(req map[string]any) (int, map[string]any) {
	name, _ := req["agent"].(string)
	command, ok := h.commands[name]
	if !ok {
		return 404, map[string]any{"error": "no preset or command is named " + name}
	}
	switch req["action"] {
	case "set":
		// As the Host takes it: an omitted kind is the only one a CLI declares; claude declares two.
		kind, _ := req["kind"].(string)
		if kind == "" && command == "copilot" {
			kind = "token"
		}
		switch {
		case command == "claude" && kind != "apiKey" && kind != "token":
			return 400, map[string]any{"error": "`claude` takes `apiKey` or `token`, so the kind must be one of those."}
		case command == "copilot" && kind != "token":
			return 400, map[string]any{"error": "`copilot` takes `token`, so the kind must be that."}
		case command == "copilot" && strings.HasPrefix(req["value"].(string), "ghp_"):
			return 400, map[string]any{"error": "This CLI refuses to start with a credential beginning `ghp_` in COPILOT_GITHUB_TOKEN, so it is not stored."}
		}
		h.set[command] = true
		return 200, map[string]any{"command": command, "set": true, "setBy": "operator", "setAt": "2026-10-02T10:00:00Z"}
	case "clear":
		h.set[command] = false
		return 200, map[string]any{"command": command, "set": false, "setBy": nil, "setAt": nil}
	case "source":
		h.sources[name] = req["source"].(string)
		var setBy, setAt any
		if h.set[command] {
			setBy, setAt = "operator", "2026-10-02T10:00:00Z"
		}
		return 200, map[string]any{"agent": name, "command": command, "source": req["source"], "set": h.set[command], "setBy": setBy, "setAt": setAt}
	}
	return 400, map[string]any{"error": "unknown action"}
}

func (h *credentialHost) RunInput(ctx context.Context, stdin string, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	if strings.Contains(line, cli.CredentialRequestScript) {
		var req map[string]any
		if err := json.Unmarshal([]byte(stdin), &req); err != nil {
			return engine.Result{ExitCode: 1, Stderr: "not JSON"}, nil
		}
		h.mu.Lock()
		h.requests = append(h.requests, req)
		if h.unsafe != "" {
			var nonce any = ""
			if h.withNonce {
				nonce = req["request"]
			}
			b, _ := json.Marshal(map[string]any{"request": nonce, "status": 409, "body": map[string]any{"error": h.unsafe}})
			h.report = string(b)
		} else if !h.silent {
			status, body := h.answer(req)
			b, _ := json.Marshal(map[string]any{"request": req["request"], "status": status, "body": body})
			h.report = string(b)
		}
		h.mu.Unlock()
	}
	return h.Scripted.RunInput(ctx, stdin, name, args...)
}

func (h *credentialHost) Run(ctx context.Context, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	if strings.Contains(line, cli.CredentialReportScript) {
		h.Scripted.Calls = append(h.Scripted.Calls, line)
		h.mu.Lock()
		defer h.mu.Unlock()
		return engine.Result{Stdout: h.report + "\n"}, nil
	}
	return h.Scripted.Run(ctx, name, args...)
}

// piped is the deps of a run with value piped in on stdin and no terminal.
func piped(h *credentialHost, value string) cli.Deps {
	deps := stubbed(h)
	deps.Interactive, deps.Stdin = false, strings.NewReader(value)
	return deps
}

const secretValue = "sk-ant-api03-not-a-real-value-0123456789"

func TestCredentialSetReadsTheValueFromStdin(t *testing.T) {
	h := newCredentialHost(t)
	code, out, errOut := run(t, piped(h, secretValue+"\nsecond line is not read\n"), "agents", "credential", "set", "claude-headless", "--api-key")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if len(h.requests) != 1 {
		t.Fatalf("want one request, got %+v", h.requests)
	}
	req := h.requests[0]
	if req["action"] != "set" || req["agent"] != "claude-headless" || req["kind"] != "apiKey" || req["value"] != secretValue {
		t.Errorf("request %+v", req)
	}
	if nonce, _ := req["request"].(string); len(nonce) != 32 {
		t.Errorf("the request carries no 32-hex nonce: %+v", req)
	}
	if !strings.Contains(out, "The claude credential is set by operator") || !strings.Contains(out, "every preset that runs claude") {
		t.Errorf("output %q", out)
	}
	if strings.Contains(out+errOut, secretValue) || strings.Contains(out+errOut, secretValue[:12]) {
		t.Errorf("the value or a part of it was printed: %q %q", out, errOut)
	}
}

func TestCredentialSetAsksAtAHiddenPromptOnATerminal(t *testing.T) {
	h := newCredentialHost(t)
	deps := stubbed(h)
	deps.Interactive = true
	var asked string
	deps.ReadSecret = func(prompt string) (string, error) { asked = prompt; return secretValue, nil }
	code, out, errOut := run(t, deps, "agents", "credential", "set", "claude", "--api-key")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(asked, "hidden") || len(h.requests) != 1 || h.requests[0]["value"] != secretValue {
		t.Errorf("prompt %q, requests %+v", asked, h.requests)
	}
}

func TestCredentialSetRefusesAValueInArgv(t *testing.T) {
	for _, args := range [][]string{
		{"agents", "credential", "set", "claude-headless", secretValue},
		{"agents", "credential", "set", "claude-headless=" + secretValue},
		{"agents", "credential", "set", "--token", "copilot", secretValue},
	} {
		h := newCredentialHost(t)
		code, out, errOut := run(t, piped(h, secretValue+"\n"), args...)
		if code != 2 {
			t.Errorf("%v: exit %d, want 2: %s %s", args, code, out, errOut)
		}
		if !strings.Contains(errOut, "never taken from the command line") {
			t.Errorf("%v: stderr %q", args, errOut)
		}
		if len(h.requests) != 0 || len(h.Scripted.Calls) != 0 {
			t.Errorf("%v: something was sent: %+v %v", args, h.requests, h.Scripted.Calls)
		}
		if strings.Contains(out+errOut, secretValue[:12]) {
			t.Errorf("%v: the refusal repeats the value: %q", args, errOut)
		}
	}
}

func TestCredentialRequestTravelsOnStdinNeverInExecArgs(t *testing.T) {
	h := newCredentialHost(t)
	code, out, errOut := run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude-headless", "--api-key")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, call := range h.Scripted.Calls {
		if strings.Contains(call, secretValue[:12]) {
			t.Errorf("the value is in an exec's arguments: %s", call)
		}
	}
	var onStdin bool
	for _, in := range h.Scripted.Inputs {
		onStdin = onStdin || strings.Contains(in, `"value":"`+secretValue+`"`)
	}
	if !onStdin {
		t.Errorf("the request did not travel on stdin: %q", h.Scripted.Inputs)
	}
	var written bool
	for _, call := range h.Scripted.Calls {
		if strings.HasPrefix(call, "podman exec -i yawble sh -c "+cli.CredentialRequestScript) && strings.HasSuffix(call, " sh "+cli.AgentCredentialsRoot) {
			written = true
		}
	}
	if !written {
		t.Errorf("no request was written to %s: %v", cli.AgentCredentialsRoot, h.Scripted.Calls)
	}
	if !strings.Contains(cli.CredentialRequestScript, "umask 077") || !strings.Contains(cli.CredentialRequestScript, "chmod 0600") ||
		!strings.Contains(cli.CredentialRequestScript, `mv -f "$1/.request.tmp" "$1/.request"`) {
		t.Errorf("the request is not written Host-only through a rename:\n%s", cli.CredentialRequestScript)
	}
}

func TestCredentialSetWithTokenSendsTheTokenKind(t *testing.T) {
	h := newCredentialHost(t)
	code, out, errOut := run(t, piped(h, "github_pat_0123456789\n"), "agents", "credential", "set", "copilot", "--token")
	if code != 0 || len(h.requests) != 1 || h.requests[0]["kind"] != "token" {
		t.Fatalf("exit %d %s %s: %+v", code, out, errOut, h.requests)
	}
}

// A CLI that declares one kind needs no flag; one that declares two is asked which.
func TestCredentialSetLeavesTheKindToTheHostWithoutAFlag(t *testing.T) {
	h := newCredentialHost(t)
	code, out, errOut := run(t, piped(h, "github_pat_0123456789\n"), "agents", "credential", "set", "copilot-headless")
	if code != 0 || len(h.requests) != 1 {
		t.Fatalf("exit %d %s %s: %+v", code, out, errOut, h.requests)
	}
	if _, sent := h.requests[0]["kind"]; sent {
		t.Errorf("a kind was sent with neither flag: %+v", h.requests[0])
	}
	if !h.set["copilot"] || !strings.Contains(out, "The copilot credential is set") {
		t.Errorf("store %+v output %q", h.set, out)
	}

	h = newCredentialHost(t)
	code, _, errOut = run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude")
	if code != 1 || !strings.Contains(errOut, "the kind must be one of those") || !strings.Contains(errOut, "--api-key or --token") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}

	code, _, errOut = run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude", "--token", "--api-key")
	if code != 2 || len(h.requests) != 1 {
		t.Errorf("both flags: exit %d stderr %q requests %d", code, errOut, len(h.requests))
	}
}

func TestCredentialSetShowsTheHostsRefusal(t *testing.T) {
	h := newCredentialHost(t)
	code, _, errOut := run(t, piped(h, "github_pat_0123456789\n"), "agents", "credential", "set", "copilot-headless", "--api-key")
	if code != 1 || !strings.Contains(errOut, "`copilot` takes `token`, so the kind must be that.") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
	// A classic token Copilot would not start with: the Host's 400, as it words it, and nothing stored.
	h = newCredentialHost(t)
	code, _, errOut = run(t, piped(h, "ghp_not-a-real-token\n"), "agents", "credential", "set", "copilot-headless")
	if code != 1 || !strings.Contains(errOut, "refuses to start with a credential beginning `ghp_`") || h.set["copilot"] {
		t.Errorf("exit %d stderr %q store %+v", code, errOut, h.set)
	}
	if strings.Contains(errOut, "not-a-real-token") {
		t.Errorf("the value was printed: %q", errOut)
	}
	h = newCredentialHost(t)
	code, _, errOut = run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "nobody")
	if code != 1 || !strings.Contains(errOut, "no preset or command is named nobody") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

func TestCredentialSetRefusesAnEmptyValueAndSendsNothing(t *testing.T) {
	h := newCredentialHost(t)
	code, _, errOut := run(t, piped(h, "\n"), "agents", "credential", "set", "claude")
	if code != 2 || !strings.Contains(errOut, "empty") || len(h.requests) != 0 {
		t.Errorf("exit %d stderr %q requests %+v", code, errOut, h.requests)
	}
}

// One value per command: set through one preset, cleared through its sibling.
func TestCredentialClear(t *testing.T) {
	h := newCredentialHost(t)
	if code, out, errOut := run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude-headless", "--api-key"); code != 0 {
		t.Fatalf("set: exit %d: %s %s", code, out, errOut)
	}
	code, out, errOut := run(t, stubbed(h), "agents", "credential", "clear", "claude")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	last := h.requests[len(h.requests)-1]
	if last["action"] != "clear" || last["agent"] != "claude" || last["value"] != nil {
		t.Errorf("request %+v", last)
	}
	if h.set["claude"] || !strings.Contains(out, "The claude credential is cleared") {
		t.Errorf("store %+v output %q", h.set, out)
	}
	if code, _, errOut := run(t, stubbed(h), "agents", "credential", "clear", "claude", "extra"); code != 2 || len(h.requests) != 2 {
		t.Errorf("an extra argument to clear: exit %d %q", code, errOut)
	}
}

func TestCredentialSourceSwitch(t *testing.T) {
	h := newCredentialHost(t)
	code, out, errOut := run(t, stubbed(h), "agents", "source", "claude-headless", "issued")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if len(h.requests) != 1 || h.requests[0]["action"] != "source" || h.requests[0]["agent"] != "claude-headless" || h.requests[0]["source"] != "issued" {
		t.Errorf("request %+v", h.requests)
	}
	if !strings.Contains(out, "claude-headless signs in through its issued credential") || !strings.Contains(out, "none is set yet") {
		t.Errorf("output %q", out)
	}

	code, out, _ = run(t, stubbed(h), "agents", "source", "claude-headless", "home")
	if code != 0 || h.sources["claude-headless"] != "home" || !strings.Contains(out, "the shared home") {
		t.Errorf("exit %d output %q sources %+v", code, out, h.sources)
	}

	code, _, errOut = run(t, stubbed(h), "agents", "source", "claude-headless", "elsewhere")
	if code != 2 || !strings.Contains(errOut, "home or issued") || len(h.requests) != 2 {
		t.Errorf("exit %d stderr %q", code, errOut)
	}

	// The source body carries the command's state: once it is set, issued has nothing to warn of.
	if code, out, errOut := run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude", "--api-key"); code != 0 {
		t.Fatalf("set: exit %d: %s %s", code, out, errOut)
	}
	code, out, _ = run(t, stubbed(h), "agents", "source", "claude-headless", "issued")
	if code != 0 || !strings.Contains(out, "its issued credential") || strings.Contains(out, "none is set yet") {
		t.Errorf("exit %d output %q", code, out)
	}
}

func TestATimedOutRequestIsWithdrawn(t *testing.T) {
	h := newCredentialHost(t)
	h.silent = true
	t.Cleanup(cli.FastCredential(50 * time.Millisecond))
	code, _, errOut := run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude")
	if code != 1 || !strings.Contains(errOut, "withdrawn") {
		t.Fatalf("exit %d stderr %q", code, errOut)
	}
	nonce := h.requests[0]["request"].(string)
	var withdrawn bool
	for _, call := range h.Scripted.Calls {
		if strings.Contains(call, cli.CredentialWithdrawScript) && strings.HasSuffix(call, " "+nonce) {
			withdrawn = true
		}
	}
	if !withdrawn {
		t.Errorf("the request was not withdrawn by its nonce: %v", h.Scripted.Calls)
	}
}

// The deadline the Host has is the request-file contract's 60 s.
func TestTheHostIsGivenSixtySeconds(t *testing.T) {
	if got := cli.CredentialWaitDefault(); got != 60*time.Second {
		t.Errorf("wait %s", got)
	}
}

// A secret pasted where the name goes is refused before a value is read or anything is sent, and
// the refusal never repeats it.
func TestANameThatIsNotPlainIsRefusedUnsentAndUnrepeated(t *testing.T) {
	secrets := []string{
		"ghu_AbCdEf/0123+xyzQ",          // characters no name has
		secretValue + "-" + secretValue, // name characters, but longer than any name
	}
	for _, secret := range secrets {
		for _, args := range [][]string{
			{"agents", "credential", "set", secret},
			{"agents", "credential", "set", "--token", secret},
			{"agents", "credential", "clear", secret},
			{"agents", "source", secret, "issued"},
		} {
			h := newCredentialHost(t)
			stdin := strings.NewReader("piped-value-that-must-not-be-read\n")
			deps := piped(h, "")
			deps.Stdin = stdin
			read := false
			deps.Interactive, deps.ReadSecret = false, func(string) (string, error) { read = true; return "", nil }
			code, out, errOut := run(t, deps, args...)
			if code != 2 {
				t.Errorf("%v: exit %d, want 2: %s", args[:3], code, errOut)
			}
			if !strings.Contains(errOut, "not a preset or command name") {
				t.Errorf("%v: stderr %q", args[:3], errOut)
			}
			if len(h.requests) != 0 || len(h.Scripted.Calls) != 0 || len(h.Scripted.Inputs) != 0 {
				t.Errorf("%v: something was sent: %+v %v", args[:3], h.requests, h.Scripted.Calls)
			}
			if read || stdin.Len() == 0 {
				t.Errorf("%v: a value was read before the name was refused", args[:3])
			}
			if strings.Contains(out+errOut, secret[:8]) {
				t.Errorf("%v: the output repeats the argument: %q %q", args[:3], out, errOut)
			}
		}
	}
}

// The source word is not repeated either: it may be a value typed in the wrong place.
func TestASourceThatIsNotHomeOrIssuedIsNotRepeated(t *testing.T) {
	h := newCredentialHost(t)
	code, out, errOut := run(t, stubbed(h), "agents", "source", "claude-headless", secretValue)
	if code != 2 || len(h.requests) != 0 || strings.Contains(out+errOut, secretValue[:12]) {
		t.Errorf("exit %d requests %+v output %q %q", code, h.requests, out, errOut)
	}
}

// A request the Host cannot answer safely is deleted unanswered; the CLI shows the Host's reason
// and fails, whether or not the refusal carries the request's nonce.
func TestARequestTheHostDeletesUnansweredShowsItsReason(t *testing.T) {
	const reason = "The agent credential folder is open to other users, so the request was deleted unanswered; nothing was stored."
	for _, withNonce := range []bool{false, true} {
		h := newCredentialHost(t)
		h.unsafe, h.withNonce = reason, withNonce
		code, out, errOut := run(t, piped(h, secretValue+"\n"), "agents", "credential", "set", "claude", "--api-key")
		if code != 1 || !strings.Contains(errOut, reason) || strings.Contains(out, "is set") {
			t.Errorf("nonce %v: exit %d out %q stderr %q", withNonce, code, out, errOut)
		}
		if strings.Contains(out+errOut, secretValue[:12]) || strings.Contains(errOut, "withdrawn") {
			t.Errorf("nonce %v: stderr %q", withNonce, errOut)
		}
	}
	if !strings.Contains(cli.CredentialRequestScript, `rm -f "$1/.request-report.json"`) {
		t.Errorf("an older report is not removed before a request goes in, so a refusal with no nonce could be stale:\n%s", cli.CredentialRequestScript)
	}
}

package cli_test

import (
	"context"
	"encoding/json"
	"strings"
	"sync"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// linkHost is a running instance whose Host answers attach requests: each request the CLI hands in
// on stdin is read, and the report `answer` gives for it is what the next read of the report sees.
type linkHost struct {
	*engine.Scripted

	mu       sync.Mutex
	requests []map[string]any
	report   string
	answer   func(req map[string]any) map[string]any
}

func newLinkHost(t *testing.T, answer func(req map[string]any) map[string]any) *linkHost {
	t.Cleanup(cli.FastAttach())
	return &linkHost{Scripted: runningScript(), answer: answer}
}

func (h *linkHost) RunInput(ctx context.Context, stdin string, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	if strings.Contains(line, cli.AttachRequestScript) {
		var req map[string]any
		if err := json.Unmarshal([]byte(stdin), &req); err != nil {
			return engine.Result{ExitCode: 1, Stderr: "not JSON"}, nil
		}
		h.mu.Lock()
		h.requests = append(h.requests, req)
		if reply := h.answer(req); reply != nil {
			reply["request"] = req["request"]
			b, _ := json.Marshal(reply)
			h.report = string(b)
		}
		h.mu.Unlock()
	}
	return h.Scripted.RunInput(ctx, stdin, name, args...)
}

func (h *linkHost) Run(ctx context.Context, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	if strings.Contains(line, cli.AttachReportScript) {
		h.Scripted.Calls = append(h.Scripted.Calls, line)
		h.mu.Lock()
		defer h.mu.Unlock()
		return engine.Result{Stdout: h.report + "\n"}, nil
	}
	return h.Scripted.Run(ctx, name, args...)
}

func (h *linkHost) sent(op string) []map[string]any {
	h.mu.Lock()
	defer h.mu.Unlock()
	var found []map[string]any
	for _, r := range h.requests {
		if r["op"] == op {
			found = append(found, r)
		}
	}
	return found
}

func TestAttachSendsTheTokenOnStdin(t *testing.T) {
	const token = "one-time-token-9d2c"
	h := newLinkHost(t, func(req map[string]any) map[string]any {
		return map[string]any{"status": 200, "sentence": "Attached to Example management as Workshop."}
	})

	code, out, errOut := run(t, stubbed(h), "attach", "https://manage.example.com", token)

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if strings.TrimSpace(out) != "Attached to Example management as Workshop." {
		t.Errorf("output: %q", out)
	}
	sent := h.sent("attach")
	if len(sent) != 1 || sent[0]["url"] != "https://manage.example.com" || sent[0]["token"] != token {
		t.Fatalf("sent: %v", sent)
	}
	for _, call := range h.Scripted.Calls {
		if strings.Contains(call, token) {
			t.Errorf("the token is on a command line: %s", call)
		}
	}
}

func TestAttachSaysTheServicesSentenceWhenItRefuses(t *testing.T) {
	h := newLinkHost(t, func(req map[string]any) map[string]any {
		return map[string]any{"status": 400, "error": "This token was already used. Ask for a new one."}
	})

	code, _, errOut := run(t, stubbed(h), "attach", "https://manage.example.com", "used-token")

	if code != 1 || !strings.Contains(errOut, "This token was already used. Ask for a new one.") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
}

func TestAttachStatusSaysNotAttached(t *testing.T) {
	h := newLinkHost(t, func(req map[string]any) map[string]any {
		return map[string]any{"status": 200, "sentence": "Not attached.", "link": nil}
	})

	code, out, errOut := run(t, stubbed(h), "attach", "status")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if out != "Not attached.\n" {
		t.Errorf("output: %q", out)
	}
	if len(h.sent("status")) != 1 {
		t.Errorf("status was not asked: %v", h.requests)
	}
}

func TestAttachStatusPrintsTheLink(t *testing.T) {
	h := newLinkHost(t, func(req map[string]any) map[string]any {
		return map[string]any{"status": 200, "link": map[string]any{
			"serviceUrl": "https://manage.example.com", "serviceName": "Example management", "instanceName": "Workshop",
			"state": "retrying", "stateSince": "2026-11-02T09:00:00+00:00", "reason": "Could not reach https://manage.example.com.",
			"lastReportAt": nil,
		}}
	})

	code, out, errOut := run(t, stubbed(h), "attach", "status")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	for _, want := range []string{"Example management", "https://manage.example.com", "Workshop", "retrying since 2026-11-02T09:00:00+00:00", "Last report:", "never"} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
}

func TestDetachAsksTheHost(t *testing.T) {
	h := newLinkHost(t, func(req map[string]any) map[string]any {
		return map[string]any{"status": 200, "sentence": "Detached from Example management."}
	})

	code, out, errOut := run(t, stubbed(h), "detach")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if strings.TrimSpace(out) != "Detached from Example management." {
		t.Errorf("output: %q", out)
	}
	if len(h.sent("detach")) != 1 {
		t.Errorf("detach was not asked: %v", h.requests)
	}
}

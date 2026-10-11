package cli_test

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// scriptedHost is a running instance whose Host answers connect requests: each request the CLI
// hands in on stdin is read, and the report `answer` gives for it is what the next read of the
// report file sees. `xdg-open` is the browser: `browse` is called with the URL it was given, and
// does what the person and the provider would.
type scriptedHost struct {
	*engine.Scripted

	mu       sync.Mutex
	requests []map[string]any
	report   string
	answer   func(req map[string]any) map[string]any
	browse   func(url string)
	opened   []string
}

func newScriptedHost(t *testing.T, answer func(req map[string]any) map[string]any) *scriptedHost {
	t.Cleanup(cli.FastConnect(5 * time.Second))
	return &scriptedHost{Scripted: runningScript(), answer: answer}
}

func (h *scriptedHost) RunInput(ctx context.Context, stdin string, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	if strings.Contains(line, cli.ConnectRequestScript) {
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

func (h *scriptedHost) Run(ctx context.Context, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	switch {
	case strings.Contains(line, cli.ConnectReportScript):
		h.Scripted.Calls = append(h.Scripted.Calls, line)
		h.mu.Lock()
		defer h.mu.Unlock()
		return engine.Result{Stdout: h.report + "\n"}, nil
	case name == "xdg-open":
		h.Scripted.Calls = append(h.Scripted.Calls, line)
		h.mu.Lock()
		h.opened = append(h.opened, args[0])
		h.mu.Unlock()
		if h.browse != nil {
			go h.browse(args[0])
		}
		return engine.Result{}, nil
	}
	return h.Scripted.Run(ctx, name, args...)
}

func (h *scriptedHost) sent(op string) []map[string]any {
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

const authorize = "https://accounts.example.test/o/oauth2/auth"

var workMail = map[string]any{
	"id": "conn-work", "name": "Work mail", "provider": "google", "providerKind": "google",
	"account": "person@example.com", "scopes": []string{"openid", "email", "https://mail.google.com/"},
	"connectedAt": "2026-09-28T10:00:00Z", "refreshedAt": nil, "status": "ok", "statusReason": nil,
	"usedBy": []map[string]any{{"team": "mail-team", "member": "Inbox", "label": "Inbox", "slot": "mail"}},
}

// googleHost starts a flow as the real Host would - the authorization URL carries the redirect URI
// and the state it issued - and stores the connection on a complete naming that state.
func googleHost(t *testing.T) *scriptedHost {
	return newScriptedHost(t, func(req map[string]any) map[string]any {
		switch req["op"] {
		case "list":
			return map[string]any{"status": 200, "connections": []any{workMail}}
		case "start":
			q := url.Values{"redirect_uri": {req["redirectUri"].(string)}, "state": {"st4te"}, "code_challenge": {"xyz"}}
			return map[string]any{"status": 200, "start": map[string]any{
				"authorizationUrl": authorize + "?" + q.Encode(), "state": "st4te",
				"redirectUri": req["redirectUri"], "expiresAt": time.Now().Add(10 * time.Minute).UTC().Format(time.RFC3339),
			}}
		case "complete":
			if req["state"] != "st4te" || req["code"] != "the-code" {
				e := "The connection's state is unknown, used or expired. Start again."
				return map[string]any{"status": 400, "error": e}
			}
			return map[string]any{"status": 200, "connection": workMail}
		}
		return nil
	})
}

// provider is the browser at the provider: it follows the authorization URL back to its
// redirect_uri - the CLI's real loopback listener - with the query given.
func provider(t *testing.T, query func(state string) url.Values) func(string) {
	return func(authURL string) {
		u, err := url.Parse(authURL)
		if err != nil {
			t.Errorf("authorization URL: %v", err)
			return
		}
		back, _ := url.Parse(u.Query().Get("redirect_uri"))
		back.RawQuery = query(u.Query().Get("state")).Encode()
		resp, err := http.Get(back.String())
		if err != nil {
			t.Errorf("redirect to the loopback listener: %v", err)
			return
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		if strings.Contains(string(body), "the-code") {
			t.Errorf("the page shows the code: %s", body)
		}
	}
}

func TestConnectRunsTheLoopbackFlowAndTheHostDoesTheExchange(t *testing.T) {
	h := googleHost(t)
	h.browse = provider(t, func(state string) url.Values { return url.Values{"code": {"the-code"}, "state": {state}} })

	code, out, errOut := run(t, stubbed(h), "connect", "google", "--scopes", "https://mail.google.com/,https://www.googleapis.com/auth/drive.readonly")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	start := h.sent("start")
	if len(start) != 1 {
		t.Fatalf("start requests: %v", start)
	}
	if start[0]["provider"] != "google" {
		t.Errorf("provider = %v", start[0]["provider"])
	}
	if got := start[0]["scopes"]; !equalJSON(got, []string{"https://mail.google.com/", "https://www.googleapis.com/auth/drive.readonly"}) {
		t.Errorf("scopes = %v", got)
	}
	redirect, _ := start[0]["redirectUri"].(string)
	if !strings.HasPrefix(redirect, "http://127.0.0.1:") || strings.HasSuffix(redirect, ":0/") {
		t.Errorf("redirectUri = %q, want the loopback listener's real port", redirect)
	}
	if len(h.opened) != 1 || !strings.HasPrefix(h.opened[0], authorize) {
		t.Errorf("browser opened %v", h.opened)
	}
	complete := h.sent("complete")
	if len(complete) != 1 || complete[0]["state"] != "st4te" || complete[0]["code"] != "the-code" {
		t.Fatalf("complete requests: %v", complete)
	}
	// The CLI never holds a verifier or a secret: the Host kept both at start.
	for _, key := range []string{"codeVerifier", "clientSecret", "verifier"} {
		if _, has := complete[0][key]; has {
			t.Errorf("complete carries %s", key)
		}
	}
	// The code went in on stdin, never on a command line.
	for _, call := range h.Scripted.Calls {
		if strings.Contains(call, "the-code") {
			t.Errorf("the code is on a command line: %s", call)
		}
	}
	if !strings.Contains(out, "connected Work mail: person@example.com at google") {
		t.Errorf("output: %s", out)
	}
}

func TestConnectStopsWithTheProvidersRefusalAndCompletesNothing(t *testing.T) {
	h := googleHost(t)
	h.browse = provider(t, func(state string) url.Values {
		return url.Values{"error": {"access_denied"}, "error_description": {"The user denied access."}, "state": {state}}
	})

	code, _, errOut := run(t, stubbed(h), "connect", "google")

	if code != 1 || !strings.Contains(errOut, "access_denied: The user denied access.") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if len(h.sent("complete")) != 0 {
		t.Error("a refused consent was completed")
	}
}

func TestConnectIgnoresARedirectWithAnotherState(t *testing.T) {
	h := googleHost(t)
	h.browse = func(authURL string) {
		u, _ := url.Parse(authURL)
		back := u.Query().Get("redirect_uri")
		resp, err := http.Get(back + "?code=forged&state=someone-else")
		if err != nil {
			t.Errorf("forged: %v", err)
			return
		}
		resp.Body.Close()
		if resp.StatusCode != http.StatusBadRequest {
			t.Errorf("forged redirect answered %d", resp.StatusCode)
		}
		provider(t, func(state string) url.Values { return url.Values{"code": {"the-code"}, "state": {state}} })(authURL)
	}

	code, _, errOut := run(t, stubbed(h), "connect", "google")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if c := h.sent("complete"); len(c) != 1 || c[0]["code"] != "the-code" {
		t.Errorf("complete requests: %v", c)
	}
}

func TestConnectSaysTheHostsSentenceWhenTheProviderIsNotSetUp(t *testing.T) {
	refusal := "Provider 'microsoft' has no client set up. Set its client ID and secret in Admin > Connections first."
	h := newScriptedHost(t, func(req map[string]any) map[string]any {
		return map[string]any{"status": 400, "error": refusal}
	})

	code, _, errOut := run(t, stubbed(h), "connect", "microsoft")

	if code != 1 || !strings.Contains(errOut, refusal) {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if len(h.opened) != 0 {
		t.Error("the browser was opened for a flow that never started")
	}
}

func TestConnectWithTheNameOfAnExistingConnectionReconnectsIt(t *testing.T) {
	h := googleHost(t)
	h.browse = provider(t, func(state string) url.Values { return url.Values{"code": {"the-code"}, "state": {state}} })

	code, out, errOut := run(t, stubbed(h), "connect", "google", "--name", "Work mail", "--scopes", "https://www.googleapis.com/auth/drive")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	start := h.sent("start")[0]
	if start["reconnectId"] != "conn-work" {
		t.Errorf("reconnectId = %v", start["reconnectId"])
	}
	if _, named := start["name"]; named {
		t.Error("a reconnect renamed the connection")
	}
	if !strings.Contains(out, "reconnected Work mail") {
		t.Errorf("output: %s", out)
	}
}

func TestConnectGivesUpWhenTheBrowserNeverComesBack(t *testing.T) {
	h := googleHost(t)
	t.Cleanup(cli.FastConnect(100 * time.Millisecond))

	code, _, errOut := run(t, stubbed(h), "connect", "google")

	if code != 1 || !strings.Contains(errOut, "did not send the browser back") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if len(h.sent("complete")) != 0 {
		t.Error("completed without a code")
	}
}

func TestAConnectRequestNoHostAnswersIsWithdrawn(t *testing.T) {
	h := newScriptedHost(t, func(map[string]any) map[string]any { return nil })

	code, _, errOut := run(t, stubbed(h), "connect", "list")

	if code != 1 || !strings.Contains(errOut, "did not answer the connect request") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	withdrawn := false
	for _, call := range h.Scripted.Calls {
		withdrawn = withdrawn || strings.Contains(call, cli.ConnectWithdrawScript)
	}
	if !withdrawn {
		t.Error("the request was not withdrawn")
	}
}

func TestConnectListPrintsEachConnectionWithoutAToken(t *testing.T) {
	stale := map[string]any{
		"id": "conn-o", "name": "Outlook", "provider": "microsoft", "providerKind": "microsoft",
		"account": "someone@contoso.test", "scopes": []string{"offline_access"}, "connectedAt": "2026-09-28T10:00:00Z",
		"status": "needs-reconnect", "statusReason": "invalid_grant: expired", "usedBy": []any{},
	}
	h := newScriptedHost(t, func(map[string]any) map[string]any {
		return map[string]any{"status": 200, "connections": []any{workMail, stale}}
	})

	code, out, errOut := run(t, stubbed(h), "connect", "list")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	for _, want := range []string{"NAME", "Work mail", "person@example.com", "mail-team/Inbox (mail)", "needs reconnect (invalid_grant: expired)"} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}

	code, out, _ = run(t, stubbed(h), "connect", "list", "--json")
	var listed []map[string]any
	if code != 0 || json.Unmarshal([]byte(out), &listed) != nil || len(listed) != 2 {
		t.Fatalf("--json: exit %d: %s", code, out)
	}
}

func TestConnectRemoveSaysWhoUsesItWhenTheHostRefuses(t *testing.T) {
	refusal := "Connection 'Work mail' is used by mail-team/Inbox (slot mail). Unbind it from those members first."
	h := newScriptedHost(t, func(req map[string]any) map[string]any {
		if req["op"] == "list" {
			return map[string]any{"status": 200, "connections": []any{workMail}}
		}
		return map[string]any{"status": 409, "error": refusal, "usedBy": workMail["usedBy"]}
	})

	code, _, errOut := run(t, stubbed(h), "connect", "remove", "Work mail")

	if code != 1 || !strings.Contains(errOut, refusal) {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if r := h.sent("remove"); len(r) != 1 || r[0]["id"] != "conn-work" {
		t.Errorf("remove requests: %v", r)
	}
}

func TestConnectRemoveDisconnectsByNameAndRefusesAnUnknownOne(t *testing.T) {
	h := newScriptedHost(t, func(req map[string]any) map[string]any {
		if req["op"] == "list" {
			return map[string]any{"status": 200, "connections": []any{workMail}}
		}
		return map[string]any{"status": 204}
	})

	code, out, errOut := run(t, stubbed(h), "connect", "remove", "Work mail")
	if code != 0 || !strings.Contains(out, "disconnected Work mail") {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}

	code, _, errOut = run(t, stubbed(h), "connect", "remove", "Personal")
	if code != 1 || !strings.Contains(errOut, `there is no connection named "Personal"; there are: "Work mail"`) {
		t.Fatalf("exit %d: %s", code, errOut)
	}
}

func TestConnectRefusesSomethingThatIsNotAProvider(t *testing.T) {
	h := newScriptedHost(t, func(map[string]any) map[string]any { return nil })

	code, _, errOut := run(t, stubbed(h), "connect", "Google Mail")

	if code != 2 || !strings.Contains(errOut, "is not a provider") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
}

func equalJSON(got any, want any) bool {
	a, _ := json.Marshal(got)
	b, _ := json.Marshal(want)
	return string(a) == string(b)
}

func TestConnectSendsMicrosoftALocalhostRedirectAndStillListensOnTheLoopbackAddress(t *testing.T) {
	h := googleHost(t)
	h.browse = provider(t, func(state string) url.Values { return url.Values{"code": {"the-code"}, "state": {state}} })

	code, _, errOut := run(t, stubbed(h), "connect", "microsoft")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	start := h.sent("start")
	if len(start) != 1 {
		t.Fatalf("start requests: %v", start)
	}
	redirect, _ := start[0]["redirectUri"].(string)
	if !strings.HasPrefix(redirect, "http://localhost:") || strings.HasSuffix(redirect, ":0/") {
		t.Errorf("redirectUri = %q, want http://localhost:<the listener's port>/", redirect)
	}
	// The browser reached the listener through localhost, so the code came back.
	if len(h.sent("complete")) != 1 {
		t.Error("the redirect to localhost did not reach the listener")
	}
}

// lateHost stores the connection on complete but answers nothing, as a Host whose exchange with
// the provider outlasts the CLI's wait; `stores` says whether it stored one at all.
func lateHost(t *testing.T, stores bool) *scriptedHost {
	var stored bool
	return newScriptedHost(t, func(req map[string]any) map[string]any {
		switch req["op"] {
		case "list":
			list := []any{workMail}
			if stored {
				fresh := map[string]any{}
				for k, v := range workMail {
					fresh[k] = v
				}
				fresh["id"], fresh["name"], fresh["usedBy"] = "conn-new", "person@example.com", []any{}
				list = append(list, fresh)
			}
			return map[string]any{"status": 200, "connections": list}
		case "start":
			q := url.Values{"redirect_uri": {req["redirectUri"].(string)}, "state": {"st4te"}}
			return map[string]any{"status": 200, "start": map[string]any{
				"authorizationUrl": authorize + "?" + q.Encode(), "state": "st4te", "redirectUri": req["redirectUri"],
				"expiresAt": time.Now().Add(10 * time.Minute).UTC().Format(time.RFC3339),
			}}
		case "complete":
			stored = stores
		}
		return nil
	})
}

func TestAConnectTheHostStoredTooLateToAnswerIsReportedAsConnected(t *testing.T) {
	h := lateHost(t, true)
	h.browse = provider(t, func(state string) url.Values { return url.Values{"code": {"the-code"}, "state": {state}} })

	code, out, errOut := run(t, stubbed(h), "connect", "google")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if !strings.Contains(out, "connected person@example.com: person@example.com at google") {
		t.Errorf("output: %s", out)
	}
}

func TestAConnectTheHostNeverStoredFailsAfterLookingAgain(t *testing.T) {
	h := lateHost(t, false)
	h.browse = provider(t, func(state string) url.Values { return url.Values{"code": {"the-code"}, "state": {state}} })

	code, _, errOut := run(t, stubbed(h), "connect", "google")

	if code != 1 || !strings.Contains(errOut, "did not answer the connect request") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if n := len(h.sent("list")); n != 2 {
		t.Errorf("list requests = %d, want one before the flow and one after the complete went unanswered", n)
	}
}

func TestConnectRefusesACustomProviderIdLongerThanTheHostTakes(t *testing.T) {
	h := newScriptedHost(t, func(map[string]any) map[string]any { return nil })

	code, _, errOut := run(t, stubbed(h), "connect", "custom-"+strings.Repeat("a", 34))

	if code != 2 || !strings.Contains(errOut, "is not a provider") {
		t.Fatalf("exit %d: %s", code, errOut)
	}
}

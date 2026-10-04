package cli_test

import (
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/cli"
)

// deviceHost starts a sign-in with a code as the real Host would - the provider's device code kept
// on the Host, only the code to type and the link going out - and answers each read of the flow
// with the next of reads, the last one repeated.
func deviceHost(t *testing.T, reads ...map[string]any) *scriptedHost {
	var mu sync.Mutex
	n := 0
	return newScriptedHost(t, func(req map[string]any) map[string]any {
		switch req["op"] {
		case "list":
			return map[string]any{"status": 200, "connections": []any{}}
		case "start":
			if req["flow"] != "device" {
				return map[string]any{"status": 400, "error": "this scripted Host only signs in with a code"}
			}
			return map[string]any{"status": 200, "start": map[string]any{
				"flowId": "flow-1", "userCode": "FAKE-CODE", "verificationUri": "https://microsoft.example.test/devicelogin",
				"expiresAt": time.Now().Add(15 * time.Minute).UTC().Format(time.RFC3339),
			}}
		case "flow":
			if req["flowId"] != "flow-1" {
				return map[string]any{"status": 404, "error": "There is no such sign-in."}
			}
			mu.Lock()
			defer mu.Unlock()
			read := reads[min(n, len(reads)-1)]
			n++
			return map[string]any{"status": 200, "flow": read}
		}
		return nil
	})
}

var outlook = map[string]any{
	"id": "conn-ms", "name": "person@example.test", "provider": "microsoft", "providerKind": "microsoft",
	"account": "person@example.test", "scopes": []string{"openid", "email", "offline_access", "Mail.Send"},
	"connectedAt": "2026-10-04T10:00:00Z", "refreshedAt": nil, "status": "ok", "statusReason": nil, "usedBy": []any{},
}

func TestConnectDevicePrintsTheCodeAndLinkWaitsAndOpensNoListener(t *testing.T) {
	listens := cli.CountListens()
	h := deviceHost(t,
		map[string]any{"state": "waiting", "sentence": "Waiting for you to sign in."},
		map[string]any{"state": "waiting", "sentence": "Waiting for you to sign in."},
		map[string]any{"state": "done", "sentence": "Connected person@example.test.", "connection": outlook},
	)

	code, out, errOut := run(t, stubbed(h), "connect", "microsoft", "--device", "--scopes", "Mail.Send")

	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	start := h.sent("start")
	if len(start) != 1 || start[0]["provider"] != "microsoft" || start[0]["flow"] != "device" {
		t.Fatalf("start requests: %v", start)
	}
	if !equalJSON(start[0]["scopes"], []string{"Mail.Send"}) {
		t.Errorf("scopes = %v", start[0]["scopes"])
	}
	// No redirect: nothing comes back to this computer.
	if _, has := start[0]["redirectUri"]; has {
		t.Errorf("a sign-in with a code sent a redirect URI: %v", start[0]["redirectUri"])
	}
	if got := listens(); got != 0 {
		t.Errorf("opened %d listener(s); a sign-in with a code needs none", got)
	}
	if !strings.Contains(out, "FAKE-CODE") || !strings.Contains(out, "https://microsoft.example.test/devicelogin") {
		t.Errorf("the code and the link are not printed:\n%s", out)
	}
	if !strings.Contains(out, "expires in") {
		t.Errorf("no word of when the code expires:\n%s", out)
	}
	// It waited: the flow was read until it was done.
	if reads := h.sent("flow"); len(reads) != 3 || reads[0]["flowId"] != "flow-1" {
		t.Errorf("flow reads: %v", reads)
	}
	if !strings.Contains(out, "connected person@example.test: person@example.test at microsoft") {
		t.Errorf("output: %s", out)
	}
}

func TestConnectDeviceEndsWithTheHostsSentenceWhenRefusedOrExpired(t *testing.T) {
	for _, ending := range []map[string]any{
		{"state": "refused", "sentence": "You did not allow the app to sign in. Nothing was connected."},
		{"state": "expired", "sentence": "The code expired before you entered it. Nothing was connected."},
	} {
		t.Run(ending["state"].(string), func(t *testing.T) {
			h := deviceHost(t, map[string]any{"state": "waiting", "sentence": "Waiting."}, ending)

			code, _, errOut := run(t, stubbed(h), "connect", "microsoft", "--device")

			if code != 1 || !strings.Contains(errOut, ending["sentence"].(string)) {
				t.Fatalf("exit %d: %s", code, errOut)
			}
		})
	}
}

func TestConnectDeviceIsRefusedForAProviderWithNoDeviceEndpoint(t *testing.T) {
	listens := cli.CountListens()
	refusal := "Google has no sign-in with a code here. Run yawble connect google without --device."
	h := newScriptedHost(t, func(req map[string]any) map[string]any {
		switch req["op"] {
		case "list":
			return map[string]any{"status": 200, "connections": []any{}}
		case "start":
			return map[string]any{"status": 400, "error": refusal}
		}
		return nil
	})

	code, out, errOut := run(t, stubbed(h), "connect", "google", "--device")

	if code != 1 || !strings.Contains(errOut, refusal) {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	if start := h.sent("start"); len(start) != 1 || start[0]["flow"] != "device" {
		t.Errorf("start requests: %v", start)
	}
	if len(h.sent("flow")) != 0 {
		t.Error("a flow that never started was read")
	}
	if got := listens(); got != 0 {
		t.Errorf("opened %d listener(s)", got)
	}
	if len(h.opened) != 0 || strings.Contains(out, "enter the code") {
		t.Errorf("acted on a flow that never started; browser %v, output:\n%s", h.opened, out)
	}
}

func TestConnectDeviceTakesNoPort(t *testing.T) {
	listens := cli.CountListens()
	// Were the port taken, this Host would let the sign-in run to the end.
	h := deviceHost(t, map[string]any{"state": "done", "sentence": "Connected person@example.test.", "connection": outlook})

	code, out, errOut := run(t, stubbed(h), "connect", "microsoft", "--device", "--port", "8400")

	if code != 2 || !strings.Contains(errOut, "--device signs in with a code and opens no port; leave out --port") {
		t.Fatalf("exit %d, not refused with the sentence for --device with --port: %s", code, errOut)
	}
	if got := listens(); got != 0 {
		t.Errorf("opened %d listener(s) for a refused command line", got)
	}
	if start := h.sent("start"); len(start) != 0 {
		t.Errorf("started a flow for a refused command line: %v", start)
	}
	if strings.Contains(out, "FAKE-CODE") {
		t.Errorf("printed a code for a refused command line:\n%s", out)
	}
}

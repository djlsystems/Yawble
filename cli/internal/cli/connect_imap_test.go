package cli_test

import (
	"strings"
	"testing"
)

// A mailbox connection (kind imap) is listed where every connection is: its servers in place of
// scopes, its status in the Host's words, and never a password - the Host answers only passwordSet.
func TestConnectListPrintsAMailboxWithItsServersAndNeverAPassword(t *testing.T) {
	mailbox := map[string]any{
		"id": "conn-box", "name": "Home mail", "provider": "imap", "providerKind": "imap", "kind": "imap",
		"account": "me@icloud.test", "username": "me", "scopes": []string{}, "connectedAt": "2026-10-07T10:00:00Z",
		"status": "needs-reconnect", "statusReason": "The server refused the password.", "usedBy": []any{},
		"imap":        map[string]any{"host": "imap.mail.me.com", "port": 993, "security": "TLS"},
		"smtp":        map[string]any{"host": "smtp.mail.me.com", "port": 587, "security": "STARTTLS"},
		"passwordSet": true,
	}
	h := newScriptedHost(t, func(req map[string]any) map[string]any {
		if req["op"] == "list" {
			return map[string]any{"status": 200, "connections": []any{workMail, mailbox}}
		}
		return map[string]any{"status": 204}
	})

	code, out, errOut := run(t, stubbed(h), "connect", "list")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, errOut)
	}
	for _, want := range []string{
		"Home mail", "imap", "me@icloud.test", "imap.mail.me.com:993 TLS, smtp.mail.me.com:587 STARTTLS",
		"needs a new app password (The server refused the password.)", "Work mail",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}

	code, out, errOut = run(t, stubbed(h), "connect", "remove", "Home mail")
	if code != 0 || !strings.Contains(out, "disconnected Home mail (me@icloud.test, a mailbox); its app password is deleted") {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
}

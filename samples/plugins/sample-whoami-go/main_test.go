package main

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const token = "ya29.test-access-token"

func userinfo(t *testing.T, status int, body string) *httptest.Server {
	t.Helper()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer "+token {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		w.WriteHeader(status)
		_, _ = w.Write([]byte(body))
	}))
	t.Cleanup(srv.Close)
	return srv
}

func stdin(url string, conns map[string]connection) *strings.Reader {
	req := map[string]any{
		"protocol":    "harness.member/1",
		"work":        []any{map[string]any{"seq": 1, "instruction": "who am I"}},
		"config":      map[string]any{"userinfoUrl": url},
		"connections": conns,
	}
	b, _ := json.Marshal(req)
	return strings.NewReader(string(b))
}

func lastRecord(t *testing.T, out string) map[string]any {
	t.Helper()
	lines := strings.Split(strings.TrimSpace(out), "\n")
	var r map[string]any
	if err := json.Unmarshal([]byte(lines[len(lines)-1]), &r); err != nil {
		t.Fatalf("last line is not JSON: %q", lines[len(lines)-1])
	}
	return r
}

func TestReportsTheAccountAndScopes(t *testing.T) {
	srv := userinfo(t, 200, `{"sub":"1","email":"person@example.test"}`)
	var out bytes.Buffer
	code := run(context.Background(), stdin(srv.URL, map[string]connection{
		"account": {Provider: "google", Account: "person@example.test", AccessToken: token, Scopes: []string{"openid", "email"}},
	}), &out)

	if code != 0 {
		t.Fatalf("exit %d: %s", code, out.String())
	}
	r := lastRecord(t, out.String())
	if r["t"] != "result" || r["ok"] != true || r["output"] != "account person@example.test\nscopes openid email" {
		t.Fatalf("result = %v", r)
	}
	if strings.Contains(out.String(), token) {
		t.Fatal("the access token was written out")
	}
}

func TestARefusedTokenFailsWithoutWritingIt(t *testing.T) {
	srv := userinfo(t, 200, `{}`)
	var out bytes.Buffer
	code := run(context.Background(), stdin(srv.URL, map[string]connection{
		"account": {Provider: "google", AccessToken: "stale-token"},
	}), &out)

	if code != 1 {
		t.Fatalf("exit %d", code)
	}
	r := lastRecord(t, out.String())
	if r["ok"] != false || !strings.Contains(r["error"].(string), "reconnect") {
		t.Fatalf("result = %v", r)
	}
	if strings.Contains(out.String(), "stale-token") {
		t.Fatal("the access token was written out")
	}
}

func TestNoConnectionFailsNamingTheSlot(t *testing.T) {
	var out bytes.Buffer
	code := run(context.Background(), stdin("http://127.0.0.1:1", nil), &out)

	if code != 1 || !strings.Contains(lastRecord(t, out.String())["error"].(string), "account slot") {
		t.Fatalf("exit %d: %s", code, out.String())
	}
}

package github_test

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/github"
)

// What kind of token GH_TOKEN is, and whether contributor mode can use it, is
// read from GitHub's own answer: the token's prefix and the X-OAuth-Scopes header.
func TestCheckReadsTheKindAndScopesContributorModeNeeds(t *testing.T) {
	scopes := ""
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		if scopes != "" {
			w.Header().Set("X-OAuth-Scopes", scopes)
		}
		_, _ = w.Write([]byte(`{"login":"someone"}`))
	}))
	defer server.Close()

	cases := []struct {
		token, scopes, kind string
		ready               bool
	}{
		{"ghp_test", "public_repo, gist", github.KindClassic, true},
		{"gho_test", "gist, read:org, repo, workflow", github.KindOAuth, true},
		{"ghp_test", "gist", github.KindClassic, false},
		{"github_pat_test", "", github.KindFineGrained, false},
	}
	for _, c := range cases {
		scopes = c.scopes
		a := github.Check(context.Background(), server.Client(), server.URL, c.token)
		if a.Kind != c.kind || a.ContributorReady() != c.ready || a.Login != "someone" {
			t.Errorf("%s %q: %+v ready=%v", c.token, c.scopes, a, a.ContributorReady())
		}
	}
}

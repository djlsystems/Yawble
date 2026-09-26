// Package github asks GitHub about a token: whether it is accepted, and as whom. `yawble doctor`
// and the guided GitHub setup both use it, so they cannot disagree about what "works" means.
package github

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
)

// DefaultAPI is GitHub's REST API.
const DefaultAPI = "https://api.github.com"

// Answer is what GitHub said about a token: its HTTP status (200 accepted, 401 rejected) and the
// account on a 200, or Unreachable when GitHub could not be asked at all.
type Answer struct {
	Status      int
	Login       string
	Unreachable bool
	// Kind is what the token is, read from its prefix: see KindOf.
	Kind string
	// Scopes is GitHub's X-OAuth-Scopes answer. A classic or OAuth token has scopes; a fine-grained
	// token has permissions instead, and GitHub sends no scopes header for it.
	Scopes []string
}

// The kinds of GitHub token, by prefix.
const (
	KindClassic     = "classic"      // ghp_
	KindOAuth       = "oauth"        // gho_: the GitHub CLI's login; it carries classic scopes
	KindFineGrained = "fine-grained" // github_pat_
	KindOther       = "other"
)

// KindOf names a token's kind from its prefix. The token itself is never kept.
func KindOf(token string) string {
	switch {
	case strings.HasPrefix(token, "github_pat_"):
		return KindFineGrained
	case strings.HasPrefix(token, "ghp_"):
		return KindClassic
	case strings.HasPrefix(token, "gho_"):
		return KindOAuth
	}
	return KindOther
}

// ContributorNeeds is what contributor mode needs of the token, said the same way by
// `yawble github` and `yawble doctor`. A classic-scoped token can fork an upstream its owner does
// not own and open a pull request on it.
const ContributorNeeds = "contributor mode (fork a project and open pull requests on it) needs a classic token with the public_repo scope, or repo for a private upstream"

// ContributorReady says whether GitHub's answer shows a token contributor mode can use: a classic
// or OAuth token whose scopes include public_repo or repo. A fine-grained token is not.
func (a Answer) ContributorReady() bool {
	if !a.Accepted() || (a.Kind != KindClassic && a.Kind != KindOAuth) {
		return false
	}
	for _, scope := range a.Scopes {
		if scope == "repo" || scope == "public_repo" {
			return true
		}
	}
	return false
}

// Accepted says whether GitHub took the token.
func (a Answer) Accepted() bool { return !a.Unreachable && a.Status == http.StatusOK }

// Check asks GitHub who token belongs to. The token is sent to api only and never logged.
func Check(ctx context.Context, client *http.Client, api, token string) Answer {
	if client == nil {
		client = &http.Client{}
	}
	if api == "" {
		api = DefaultAPI
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(api, "/")+"/user", nil)
	if err != nil {
		return Answer{Unreachable: true}
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/vnd.github+json")
	req.Header.Set("User-Agent", "yawble")
	resp, err := client.Do(req)
	if err != nil {
		return Answer{Unreachable: true}
	}
	defer resp.Body.Close()
	a := Answer{Status: resp.StatusCode, Kind: KindOf(token)}
	if raw := resp.Header.Get("X-OAuth-Scopes"); raw != "" {
		for _, scope := range strings.Split(raw, ",") {
			if scope = strings.TrimSpace(scope); scope != "" {
				a.Scopes = append(a.Scopes, scope)
			}
		}
	}
	if resp.StatusCode == http.StatusOK {
		var who struct {
			Login string `json:"login"`
		}
		_ = json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&who)
		a.Login = who.Login
	}
	return a
}

// SAMPLE WHO AM I, GO: the end-to-end check for connections, protocol harness.member/1.
//
// In:  ONE JSON request on stdin - { protocol, member, work: [...], config: { userinfoUrl },
//
//	connections: { account: { provider, account, accessToken, expiresAt, scopes } }, ... }.
//
// Out: JSON Lines on stdout - progress, then exactly one result: `account <email>` and
// `scopes <scope> ...` on success, or a failure in its own words.
//
// The access token is used once, for one GET to userinfo, and is never written anywhere: not to
// stdout, not to stderr, not to a file. The Host hands a fresh token each run; a run never refreshes
// one, and never asks anybody for one.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

const defaultUserinfo = "https://openidconnect.googleapis.com/v1/userinfo"

// slot is the manifest's one connection slot.
const slot = "account"

type connection struct {
	Provider    string   `json:"provider"`
	Account     string   `json:"account"`
	AccessToken string   `json:"accessToken"`
	ExpiresAt   string   `json:"expiresAt"`
	Scopes      []string `json:"scopes"`
}

type request struct {
	Config struct {
		UserinfoURL *string `json:"userinfoUrl"`
	} `json:"config"`
	Connections map[string]connection `json:"connections"`
}

type (
	progress struct {
		T      string `json:"t"`
		Status string `json:"status"`
	}
	succeeded struct {
		T      string `json:"t"`
		OK     bool   `json:"ok"`
		Output string `json:"output"`
	}
	failed struct {
		T     string `json:"t"`
		OK    bool   `json:"ok"`
		Error string `json:"error"`
	}
)

var client = &http.Client{Timeout: 20 * time.Second}

func main() { os.Exit(run(context.Background(), os.Stdin, os.Stdout)) }

func run(ctx context.Context, in io.Reader, out io.Writer) int {
	emit := func(record any) {
		line, _ := json.Marshal(record)
		_, _ = out.Write(append(line, '\n'))
	}
	fail := func(words string) int {
		emit(failed{"result", false, words})
		return 1
	}

	var req request
	raw, err := io.ReadAll(in)
	if err != nil || json.Unmarshal(raw, &req) != nil {
		return fail("the request on stdin could not be read")
	}

	conn, ok := req.Connections[slot]
	if !ok || conn.AccessToken == "" {
		// The Host blocks a run whose required slot is unbound, so this is a Host older than
		// connections, or a slot renamed in the manifest without a new binding.
		return fail("no connection is bound to the account slot; a person binds one in the member's settings")
	}

	url := defaultUserinfo
	if req.Config.UserinfoURL != nil && *req.Config.UserinfoURL != "" {
		url = *req.Config.UserinfoURL
	}

	emit(progress{"progress", "asking " + conn.Provider + " who the connection's account is"})

	email, err := whoami(ctx, url, conn.AccessToken)
	if err != nil {
		return fail(err.Error())
	}

	scopes := strings.Join(conn.Scopes, " ")
	if scopes == "" {
		scopes = "(none reported)"
	}
	emit(succeeded{"result", true, "account " + email + "\nscopes " + scopes})
	return 0
}

// whoami sends the token to userinfo and returns the account's email. An error never carries the
// token or the response body, only the status.
func whoami(ctx context.Context, url, token string) (string, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return "", fmt.Errorf("userinfoUrl is not a URL")
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return "", fmt.Errorf("userinfo could not be reached")
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusUnauthorized {
		return "", fmt.Errorf("userinfo refused the access token (401); reconnect the connection from Admin, Connections")
	}
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("userinfo answered %d", resp.StatusCode)
	}

	var info struct {
		Email string `json:"email"`
		Sub   string `json:"sub"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&info); err != nil {
		return "", fmt.Errorf("userinfo's answer could not be read")
	}
	if info.Email != "" {
		return info.Email, nil
	}
	if info.Sub != "" {
		return "subject " + info.Sub + " (no email: the connection lacks the email scope)", nil
	}
	return "", fmt.Errorf("userinfo named no account")
}

package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// connectRequest is <data>/connections/.connect: the body of the route each op stands for.
type connectRequest struct {
	Request     string   `json:"request"`
	Op          string   `json:"op"`
	Provider    string   `json:"provider,omitempty"`
	Scopes      []string `json:"scopes,omitempty"`
	Name        string   `json:"name,omitempty"`
	ReconnectID string   `json:"reconnectId,omitempty"`
	RedirectURI string   `json:"redirectUri,omitempty"`
	State       string   `json:"state,omitempty"`
	Code        string   `json:"code,omitempty"`
	ID          string   `json:"id,omitempty"`
	// Unbind confirms a remove of a connection members use: their bindings go with it.
	Unbind bool `json:"unbind,omitempty"`
	// Flow is "device" on a start that signs in with a code; FlowID names that sign-in on a read.
	Flow   string `json:"flow,omitempty"`
	FlowID string `json:"flowId,omitempty"`
	// Tenant is a Microsoft connection's own directory on a start: a tenant ID or a domain. Left
	// out, a new connection signs in through the provider's tenant and a reconnect keeps its own.
	Tenant string `json:"tenant,omitempty"`
	// Error and ErrorDescription are the provider's refusal on the redirect, told to the Host on a
	// complete instead of a code: it answers the sentence a person reads.
	Error            string `json:"error,omitempty"`
	ErrorDescription string `json:"errorDescription,omitempty"`
}

// connectReport is <data>/connections/.connect-report.json: the Host's answer, with the status
// the route would give and, on a refusal, its sentence.
type connectReport struct {
	Request     string        `json:"request"`
	Status      int           `json:"status"`
	Error       *string       `json:"error"`
	Start       *connectStart `json:"start"`
	Flow        *connectFlow  `json:"flow"`
	Connection  *connection   `json:"connection"`
	Connections []connection  `json:"connections"`
	UsedBy      []connUse     `json:"usedBy"`
}

// connectStart is a start's answer: where to send the browser, or, for a sign-in with a code, the
// code to enter and where. The provider's device code stays on the Host.
type connectStart struct {
	AuthorizationURL string `json:"authorizationUrl"`
	State            string `json:"state"`
	RedirectURI      string `json:"redirectUri"`
	ExpiresAt        string `json:"expiresAt"`
	FlowID           string `json:"flowId"`
	UserCode         string `json:"userCode"`
	VerificationURI  string `json:"verificationUri"`
}

// connectFlow is a read of a sign-in with a code: waiting, done (with the connection), refused or
// expired, in the Host's sentence, with the code to enter now.
type connectFlow struct {
	State      string      `json:"state"`
	Sentence   string      `json:"sentence"`
	Connection *connection `json:"connection"`
	// The code to enter now, where, and until when: a second code for the same sign-in replaces
	// the first here while the flow waits.
	UserCode        string `json:"userCode"`
	VerificationURI string `json:"verificationUri"`
	ExpiresAt       string `json:"expiresAt"`
}

// connection is one entry of GET /api/connections. It never carries a token.
type connection struct {
	ID           string    `json:"id"`
	Name         string    `json:"name"`
	Provider     string    `json:"provider"`
	ProviderKind string    `json:"providerKind"`
	Account      string    `json:"account"`
	Scopes       []string  `json:"scopes"`
	ConnectedAt  string    `json:"connectedAt"`
	RefreshedAt  *string   `json:"refreshedAt"`
	Status       string    `json:"status"`
	StatusReason *string   `json:"statusReason"`
	UsedBy       []connUse `json:"usedBy"`

	// Kind is oauth, imap (a mailbox), website, apikey or login (a username and password). A
	// mailbox has its servers in place of scopes; the Host never answers its password, only whether
	// one is set.
	Kind        string      `json:"kind,omitempty"`
	Username    string      `json:"username,omitempty"`
	Imap        *mailServer `json:"imap,omitempty"`
	Smtp        *mailServer `json:"smtp,omitempty"`
	PasswordSet *bool       `json:"passwordSet,omitempty"`

	// A website's, API key's or login's preset and fields. A masked field says only whether it is
	// set: the Host never answers its value.
	Preset string            `json:"preset,omitempty"`
	Fields []credentialField `json:"fields,omitempty"`
}

type credentialField struct {
	Name   string  `json:"name"`
	Label  string  `json:"label"`
	Masked bool    `json:"masked"`
	Value  *string `json:"value"`
	Set    bool    `json:"set"`
}

type mailServer struct {
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Security string `json:"security"`
}

func (c connection) mailbox() bool { return c.Kind == "imap" }

// credential is a website, an API key or a username and password: fields, no sign-in.
func (c connection) credential() bool {
	return c.Kind == "website" || c.Kind == "apikey" || c.Kind == "login"
}

// kind is the connection's kind, oauth for a Host older than kinds.
func (c connection) kind() string {
	if c.Kind == "" {
		return "oauth"
	}
	return c.Kind
}

// detail is what the list shows beside the status: a mailbox's servers, a credential's fields (a
// masked one only as set), an account's scopes.
func (c connection) detail() string {
	if c.mailbox() && c.Imap != nil && c.Smtp != nil {
		return fmt.Sprintf("%s:%d %s, %s:%d %s", c.Imap.Host, c.Imap.Port, c.Imap.Security, c.Smtp.Host, c.Smtp.Port, c.Smtp.Security)
	}
	if c.credential() {
		var fields []string
		for _, f := range c.Fields {
			switch {
			case !f.Set:
				fields = append(fields, f.Name+"=not set")
			case f.Masked || f.Value == nil:
				fields = append(fields, f.Name+"=set")
			default:
				fields = append(fields, f.Name+"="+*f.Value)
			}
		}
		return orDash(strings.Join(fields, " "))
	}
	return orDash(strings.Join(c.Scopes, " "))
}

type connUse struct {
	Team   string `json:"team"`
	Member string `json:"member"`
	Label  string `json:"label"`
	Slot   string `json:"slot"`
}

func (c connection) statusText() string {
	if c.Status == "ok" {
		return "ok"
	}
	if c.mailbox() {
		if c.StatusReason != nil && *c.StatusReason != "" {
			return "needs a new app password (" + *c.StatusReason + ")"
		}
		return "needs a new app password"
	}
	if c.StatusReason != nil && *c.StatusReason != "" {
		return "needs reconnect (" + *c.StatusReason + ")"
	}
	return "needs reconnect"
}

func (c connection) usedByText() string {
	var names []string
	for _, u := range c.UsedBy {
		names = append(names, u.Team+"/"+u.Member+" ("+u.Slot+")")
	}
	return strings.Join(names, ", ")
}

func (r connectReport) ok() bool { return r.Status >= 200 && r.Status < 300 }

// refusal is the Host's own sentence, or what did not happen when it gave none.
func (r connectReport) refusal(what string) error {
	if r.Error != nil && *r.Error != "" {
		return errors.New(*r.Error)
	}
	return fmt.Errorf("%s (status %d)", what, r.Status)
}

// storedSince is the connection a complete stored after all, when the Host answered too late: a
// connection of provider that was not in before, or the reconnected one changed. Nil when there
// is none, or when the Host cannot list.
func storedSince(ctx context.Context, e engine.Engine, before []connection, provider, reconnectID string) *connection {
	after, err := listConnections(ctx, e)
	if err != nil {
		return nil
	}
	was := map[string]connection{}
	for _, c := range before {
		was[c.ID] = c
	}
	for i, c := range after {
		old, existed := was[c.ID]
		if reconnectID != "" && c.ID == reconnectID && existed && !sameConnection(old, c) {
			return &after[i]
		}
		if reconnectID == "" && !existed && c.Provider == provider {
			return &after[i]
		}
	}
	return nil
}

func sameConnection(a, b connection) bool {
	x, _ := json.Marshal(a)
	y, _ := json.Marshal(b)
	return string(x) == string(y)
}

// exchange hands one request to the Host and waits connectWait for the report that answers it.
func exchange(ctx context.Context, e engine.Engine, req connectRequest) (connectReport, error) {
	return exchangeWithin(ctx, e, req, connectWait)
}

// exchangeWithin hands one request to the Host and waits for the report that answers it. A request
// no Host answered within wait is withdrawn, so a Host started later never carries it out.
func exchangeWithin(ctx context.Context, e engine.Engine, req connectRequest, wait time.Duration) (connectReport, error) {
	req.Request = newNonce()
	body, _ := json.Marshal(req)
	// ON STDIN, never argv: a complete request carries the authorization code.
	x := hostExchange{root: connectionsRoot, request: connectRequestScript, report: connectReportScript, withdraw: connectWithdrawScript, onStdin: true, poll: connectPoll}
	answer, err := x.ask(ctx, e, string(body), req.Request, wait)
	if errors.Is(err, errNoAnswer) {
		return connectReport{}, fmt.Errorf("%w within %s, and the request was withdrawn. "+
			"An image from before connections does not answer; `yawble update` brings the instance current", errUnanswered, wait)
	}
	if err != nil {
		return connectReport{}, err
	}
	var r connectReport
	_ = json.Unmarshal([]byte(answer), &r)
	return r, nil
}

func listConnections(ctx context.Context, e engine.Engine) ([]connection, error) {
	r, err := exchange(ctx, e, connectRequest{Op: "list"})
	if err != nil {
		return nil, err
	}
	if !r.ok() {
		return nil, r.refusal("the Host did not list the connections")
	}
	if r.Connections == nil {
		return []connection{}, nil
	}
	return r.Connections, nil
}

// findConnection is the one with that id, else the one named `name`, ignoring case as the Host does.
func findConnection(list []connection, name string) (connection, error) {
	var found []connection
	for _, c := range list {
		if c.ID == name {
			return c, nil
		}
		if strings.EqualFold(strings.TrimSpace(c.Name), strings.TrimSpace(name)) {
			found = append(found, c)
		}
	}
	switch len(found) {
	case 1:
		return found[0], nil
	case 0:
		var names []string
		for _, c := range list {
			names = append(names, fmt.Sprintf("%q", c.Name))
		}
		known := "there are none"
		if len(names) > 0 {
			known = "there are: " + strings.Join(names, ", ")
		}
		return connection{}, fmt.Errorf("there is no connection named %q; %s", name, known)
	default:
		var ids []string
		for _, c := range found {
			ids = append(ids, c.ID)
		}
		return connection{}, fmt.Errorf("more than one connection is named %q; name one by its id: %s", name, strings.Join(ids, ", "))
	}
}

// sameName is the connection with this name, to reconnect, or nil. Every name is its own, ignoring
// case: a name another provider's connection has is reconnected too, and the Host says which provider it takes.
func sameName(list []connection, provider, name string) *connection {
	for i, c := range list {
		if strings.EqualFold(strings.TrimSpace(c.Name), strings.TrimSpace(name)) {
			return &list[i]
		}
	}
	return nil
}

// splitScopes takes --scopes as given (repeated or comma-separated) and splits spaces too, each once.
func splitScopes(given []string) []string {
	seen := map[string]bool{}
	var scopes []string
	for _, s := range given {
		for _, scope := range strings.FieldsFunc(s, func(r rune) bool { return r == ',' || r == ' ' || r == '\t' || r == '\n' }) {
			if !seen[scope] {
				seen[scope] = true
				scopes = append(scopes, scope)
			}
		}
	}
	return scopes
}

// The scripts run as root in the container. The request arrives on stdin, is written beside the
// request file with harness-only modes and moved in, so the Host never reads half of it and no
// agent can read it at all.
const (
	// $1 root; stdin: the request's JSON.
	connectRequestScript = `set -e
umask 077
mkdir -p "$1"
chown harness "$1"
chmod 0700 "$1"
cat > "$1/.connect.tmp"
chown harness "$1/.connect.tmp"
chmod 0600 "$1/.connect.tmp"
mv -f "$1/.connect.tmp" "$1/.connect"`

	// $1 root.
	connectReportScript = `cat "$1/.connect-report.json" 2>/dev/null || true`

	// $1 root, $2 nonce: removes the request if it is still this one.
	connectWithdrawScript = `grep -qF "$2" "$1/.connect" 2>/dev/null && rm -f "$1/.connect"; true`
)

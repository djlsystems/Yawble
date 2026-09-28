package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"io"
	"net"
	"net/http"
	"regexp"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// connectionsRoot is where the CLI and the Host trade connect requests: <data root>/connections,
// harness-only (0700), because a request carries an authorization code.
const connectionsRoot = dataRoot + "/connections"

var (
	// connectWait is how long the Host has to answer one request; the browser wait is separate.
	connectWait = 20 * time.Second
	// completeWait is longer: the Host exchanges the code (up to 30 s) and asks the provider who
	// the account is (up to 30 s more) before it answers a complete.
	completeWait = 75 * time.Second
	connectPoll  = 500 * time.Millisecond
	// browserWait is the most `connect` waits for the provider to send the browser back. The
	// Host's state lasts 10 minutes, so waiting longer could only end in a refusal.
	browserWait = 10 * time.Minute
)

// providerPattern is a provider id as the Host names one: google, microsoft, custom-<id>, at most
// 40 characters in all.
var providerPattern = regexp.MustCompile(`^(google|microsoft|custom-[a-z0-9][a-z0-9-]{0,32})$`)

func newConnectCommand(deps Deps) *cobra.Command {
	var scopes []string
	var name string
	var port int
	cmd := &cobra.Command{
		Use:   "connect <provider>",
		Short: "Connect an account at an OAuth service (google, microsoft, custom-<id>) for plugins to use",
		Long: "Connects an account at an OAuth provider to the running instance, from this computer's browser. " +
			"The Host starts the flow and keeps the PKCE verifier; this command listens on http://127.0.0.1:<free port> (sent to Microsoft as http://localhost:<port>), " +
			"opens the browser at the provider's consent page, catches the code the provider sends back, and hands it to the " +
			"Host, which does the exchange. The client secret never leaves the Host and no token reaches this computer.\n\n" +
			"Use it when the provider will not accept the instance's own address as a redirect (a tunnel, a private address). " +
			"The provider's client is set up first in the web UI, Admin > Connections. Connecting again with --name of an " +
			"existing connection of the same provider reconnects it, adding the scopes asked to those it has.\n\n" +
			"It reaches the Host the way `plugin install --from-instance` does: through the container engine, by a request " +
			"file the Host answers. Nothing is signed in and no port of the instance is used.",
		Example: "  yawble connect google --scopes https://mail.google.com/ --name \"Work mail\"\n" +
			"  yawble connect microsoft --scopes offline_access,https://outlook.office.com/SMTP.Send\n" +
			"  yawble connect list\n" +
			"  yawble connect remove \"Work mail\"",
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			provider := strings.ToLower(strings.TrimSpace(args[0]))
			if !providerPattern.MatchString(provider) {
				return UsageError{fmt.Sprintf("%q is not a provider: name google, microsoft, or a custom provider's id (custom-<id>); `yawble connect list` shows the connections there are", args[0])}
			}
			if port < 0 || port > 65535 {
				return UsageError{fmt.Sprintf("--port %d is not a port", port)}
			}
			return connect(cmd.Context(), cmd.OutOrStdout(), deps, provider, splitScopes(scopes), strings.TrimSpace(name), port)
		},
	}
	cmd.Flags().StringSliceVar(&scopes, "scopes", nil, "scopes to ask for besides the provider's own, separated by commas")
	cmd.Flags().StringVar(&name, "name", "", "the connection's name (default: the account's); an existing one of this provider is reconnected")
	cmd.Flags().IntVar(&port, "port", 0, "the loopback port to listen on (default: a free one); for a client that needs the redirect registered exactly")
	cmd.AddCommand(newConnectListCommand(deps), newConnectRemoveCommand(deps))
	return cmd
}

func newConnectListCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "list",
		Short:   "Each connection on the instance: name, provider, account, status, scopes and who uses it",
		Example: "  yawble connect list\n  yawble connect list --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			ctx, out := cmd.Context(), cmd.OutOrStdout()
			e, err := runningEngine(ctx, deps, "connect list")
			if err != nil {
				return err
			}
			list, err := listConnections(ctx, e)
			if err != nil {
				return err
			}
			if asJSON {
				enc := json.NewEncoder(out)
				enc.SetIndent("", "  ")
				return enc.Encode(list)
			}
			if len(list) == 0 {
				fmt.Fprintln(out, "no account is connected; yawble connect <provider>, or Admin > Connections in the web UI")
				return nil
			}
			w := tabwriter.NewWriter(out, 0, 4, 2, ' ', 0)
			fmt.Fprintln(w, "NAME\tPROVIDER\tACCOUNT\tSTATUS\tSCOPES\tUSED BY")
			for _, c := range list {
				fmt.Fprintf(w, "%s\t%s\t%s\t%s\t%s\t%s\n", c.Name, c.Provider, c.Account, c.statusText(), orDash(strings.Join(c.Scopes, " ")), orDash(c.usedByText()))
			}
			return w.Flush()
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	return cmd
}

func newConnectRemoveCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "remove <name>",
		Short: "Disconnect a connection: revoke it at the provider where it can be, and delete its tokens",
		Long: "Disconnects the connection named <name> (or with that id). The Host refuses while any member uses it " +
			"and names them; unbind it in those members' settings first.",
		Example: "  yawble connect remove \"Work mail\"",
		Args:    cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			ctx, out := cmd.Context(), cmd.OutOrStdout()
			e, err := runningEngine(ctx, deps, "connect remove")
			if err != nil {
				return err
			}
			list, err := listConnections(ctx, e)
			if err != nil {
				return err
			}
			target, err := findConnection(list, args[0])
			if err != nil {
				return err
			}
			r, err := exchange(ctx, e, connectRequest{Op: "remove", ID: target.ID})
			if err != nil {
				return err
			}
			if !r.ok() {
				return r.refusal("the Host did not disconnect " + target.Name)
			}
			fmt.Fprintf(out, "disconnected %s (%s at %s); its tokens are deleted\n", target.Name, target.Account, target.Provider)
			return nil
		},
	}
}

// connect runs one flow: start on the Host, the browser round trip on this computer, complete on
// the Host.
func connect(ctx context.Context, out io.Writer, deps Deps, provider string, scopes []string, name string, port int) error {
	e, err := runningEngine(ctx, deps, "connect")
	if err != nil {
		return err
	}

	request := connectRequest{Op: "start", Provider: provider, Scopes: scopes}
	// What is stored before the flow, so a complete the Host answered too late can still be told
	// apart from one it refused.
	before, err := listConnections(ctx, e)
	if err != nil {
		return err
	}
	if name != "" {
		if existing := sameName(before, provider, name); existing != nil {
			request.ReconnectID = existing.ID
			fmt.Fprintf(out, "reconnecting %s (%s)\n", existing.Name, existing.Account)
		} else {
			request.Name = name
		}
	}

	listener, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return fmt.Errorf("cannot listen on 127.0.0.1:%d for the provider's redirect: %w", port, err)
	}
	defer listener.Close()
	request.RedirectURI = loopbackRedirect(provider, listener.Addr().(*net.TCPAddr).Port)

	started, err := exchange(ctx, e, request)
	if err != nil {
		return err
	}
	if !started.ok() || started.Start == nil {
		return started.refusal("the Host did not start the connection")
	}
	flow := *started.Start

	fmt.Fprintf(out, "opening the %s consent page in your browser; if it does not open, open this address yourself:\n\n  %s\n\n", provider, flow.AuthorizationURL)
	fmt.Fprintf(out, "waiting for the provider to send the browser back to %s (Ctrl+C to give up)\n", request.RedirectURI)
	openBrowser(ctx, deps, flow.AuthorizationURL)

	wait := browserWait
	if expires, err := time.Parse(time.RFC3339, flow.ExpiresAt); err == nil {
		if until := time.Until(expires); until > 0 && until < wait {
			wait = until
		}
	}
	code, err := awaitCode(ctx, listener, flow.State, wait)
	if err != nil {
		return err
	}

	var c *connection
	done, err := exchangeWithin(ctx, e, connectRequest{Op: "complete", State: flow.State, Code: code}, completeWait)
	switch {
	case errors.Is(err, errUnanswered):
		// The Host may have taken the code and still be storing it: look before saying it failed.
		c = storedSince(ctx, e, before, provider, request.ReconnectID)
		if c == nil {
			return err
		}
	case err != nil:
		return err
	case !done.ok() || done.Connection == nil:
		return done.refusal("the Host did not store the connection")
	default:
		c = done.Connection
	}
	verb := "connected"
	if request.ReconnectID != "" {
		verb = "reconnected"
	}
	fmt.Fprintf(out, "%s %s: %s at %s, scopes %s\n", verb, c.Name, c.Account, c.Provider, orDash(strings.Join(c.Scopes, " ")))
	fmt.Fprintln(out, "bind it to a plugin member in that member's settings in the web UI")
	return nil
}

// callbackResult is what the loopback listener caught: a code, or the provider's refusal.
type callbackResult struct {
	code string
	err  error
}

// awaitCode serves the provider's redirect on listener and answers the code it carries. A request
// whose state is not this flow's is answered 400 and ignored: it is not the provider's answer to
// this flow. An `error` from the provider ends the wait with the provider's words.
func awaitCode(ctx context.Context, listener net.Listener, state string, wait time.Duration) (string, error) {
	caught := make(chan callbackResult, 1)
	deliver := func(r callbackResult) {
		select {
		case caught <- r:
		default:
		}
	}
	server := &http.Server{
		ReadHeaderTimeout: 10 * time.Second,
		Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			q := r.URL.Query()
			if r.URL.Path != "/" || (q.Get("code") == "" && q.Get("error") == "") {
				http.NotFound(w, r)
				return
			}
			if q.Get("state") != state {
				page(w, http.StatusBadRequest, "This is not the answer to the connection yawble is waiting for. Close this tab and go back to the terminal.")
				return
			}
			if reason := q.Get("error"); reason != "" {
				if d := q.Get("error_description"); d != "" {
					reason += ": " + d
				}
				page(w, http.StatusOK, "The provider did not connect the account: "+reason+". Go back to the terminal.")
				deliver(callbackResult{err: fmt.Errorf("the provider did not connect the account: %s", reason)})
				return
			}
			page(w, http.StatusOK, "Got it. yawble is finishing the connection; you can close this tab and go back to the terminal.")
			deliver(callbackResult{code: q.Get("code")})
		}),
	}
	go func() { _ = server.Serve(listener) }()
	defer func() {
		shut, cancel := context.WithTimeout(context.Background(), time.Second)
		defer cancel()
		_ = server.Shutdown(shut)
	}()

	timer := time.NewTimer(wait)
	defer timer.Stop()
	select {
	case r := <-caught:
		return r.code, r.err
	case <-timer.C:
		return "", fmt.Errorf("the provider did not send the browser back within %s; the flow has expired, run the command again", wait.Round(time.Second))
	case <-ctx.Done():
		return "", ctx.Err()
	}
}

// page answers the browser with one sentence. Never the code: the page may be left open.
func page(w http.ResponseWriter, status int, sentence string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	fmt.Fprintf(w, "<!doctype html><meta charset=\"utf-8\"><title>yawble connect</title><p style=\"font-family:sans-serif\">%s</p>\n", html.EscapeString(sentence))
}

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
}

// connectReport is <data>/connections/.connect-report.json: the Host's answer, with the status
// the route would give and, on a refusal, its sentence.
type connectReport struct {
	Request     string        `json:"request"`
	Status      int           `json:"status"`
	Error       *string       `json:"error"`
	Start       *connectStart `json:"start"`
	Connection  *connection   `json:"connection"`
	Connections []connection  `json:"connections"`
	UsedBy      []connUse     `json:"usedBy"`
}

type connectStart struct {
	AuthorizationURL string `json:"authorizationUrl"`
	State            string `json:"state"`
	RedirectURI      string `json:"redirectUri"`
	ExpiresAt        string `json:"expiresAt"`
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

// loopbackRedirect is the redirect URI this computer's listener answers, which is always bound to
// 127.0.0.1. Microsoft's Entra takes a loopback redirect registered under platform "Web" only as
// `http://localhost` (it ignores the port), and compares the host literally, so microsoft is sent
// localhost; every other provider is sent 127.0.0.1.
func loopbackRedirect(provider string, port int) string {
	host := "127.0.0.1"
	if provider == "microsoft" {
		host = "localhost"
	}
	return fmt.Sprintf("http://%s:%d/", host, port)
}

// errUnanswered is a request the Host did not answer in time; it was withdrawn.
var errUnanswered = errors.New("the Host did not answer the connect request")

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
	if _, err := e.ExecInput(ctx, instance.ContainerName, string(body)+"\n", "sh", "-c", connectRequestScript, "sh", connectionsRoot); err != nil {
		return connectReport{}, err
	}
	deadline := time.Now().Add(wait)
	for {
		res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", connectReportScript, "sh", connectionsRoot)
		if err != nil {
			return connectReport{}, err
		}
		var r connectReport
		if json.Unmarshal([]byte(strings.TrimSpace(res.Stdout)), &r) == nil && r.Request == req.Request {
			return r, nil
		}
		if time.Now().After(deadline) {
			_, _ = e.Exec(ctx, instance.ContainerName, "sh", "-c", connectWithdrawScript, "sh", connectionsRoot, req.Request)
			return connectReport{}, fmt.Errorf("%w within %s, and the request was withdrawn. "+
				"An image from before connections does not answer; `yawble update` brings the instance current", errUnanswered, wait)
		}
		select {
		case <-ctx.Done():
			_, _ = e.Exec(context.Background(), instance.ContainerName, "sh", "-c", connectWithdrawScript, "sh", connectionsRoot, req.Request)
			return connectReport{}, ctx.Err()
		case <-time.After(connectPoll):
		}
	}
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

// findConnection is the one named `name`, or with that id.
func findConnection(list []connection, name string) (connection, error) {
	var found []connection
	for _, c := range list {
		if c.ID == name || c.Name == name {
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

// sameName is the connection of this provider with this name, to reconnect, or nil.
func sameName(list []connection, provider, name string) *connection {
	for i, c := range list {
		if c.Provider == provider && c.Name == name {
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

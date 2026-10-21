package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"regexp"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/spf13/cobra"
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
	// deviceRead is how often a sign-in with a code is read while it waits; the Host does the
	// waiting at the provider.
	deviceRead = 3 * time.Second
	// deviceGrace is how long past the code's expiry the Host has to say it expired.
	deviceGrace = 30 * time.Second
)

// listen opens the loopback listener the provider's redirect comes back to; tests count it.
var listen = net.Listen

// providerPattern is a provider id as the Host names one: google, microsoft, custom-<id>, at most
// 40 characters in all.
var providerPattern = regexp.MustCompile(`^(google|microsoft|custom-[a-z0-9][a-z0-9-]{0,32})$`)

func newConnectCommand(deps Deps) *cobra.Command {
	var scopes []string
	var name string
	var port int
	var device bool
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
			"With --device (Microsoft), it signs in with a code instead: it prints a link and a code to enter there, from any " +
			"browser on any device, and waits for the Host to say the sign-in is done. No port is opened and nothing comes back " +
			"to this computer. A provider with no sign-in with a code refuses it, and says so.\n\n" +
			"It reaches the Host the way `plugin install --from-instance` does: through the container engine, by a request " +
			"file the Host answers. Nothing is signed in and no port of the instance is used.",
		Example: "  yawble connect google --scopes https://mail.google.com/ --name \"Work mail\"\n" +
			"  yawble connect microsoft --scopes offline_access,https://outlook.office.com/SMTP.Send\n" +
			"  yawble connect microsoft --device --scopes Mail.Send\n" +
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
			if device {
				if cmd.Flags().Changed("port") {
					return UsageError{"--device signs in with a code and opens no port; leave out --port"}
				}
				return connectDevice(cmd.Context(), cmd.OutOrStdout(), deps, provider, splitScopes(scopes), strings.TrimSpace(name))
			}
			return connect(cmd.Context(), cmd.OutOrStdout(), deps, provider, splitScopes(scopes), strings.TrimSpace(name), port)
		},
	}
	cmd.Flags().StringSliceVar(&scopes, "scopes", nil, "scopes to ask for besides the provider's own, separated by commas")
	cmd.Flags().StringVar(&name, "name", "", "the connection's name (default: the account's); an existing one of this provider is reconnected")
	cmd.Flags().IntVar(&port, "port", 0, "the loopback port to listen on (default: a free one); for a client that needs the redirect registered exactly")
	cmd.Flags().BoolVar(&device, "device", false, "sign in with a code entered at the provider's page, from any browser (Microsoft); opens no port")
	cmd.AddCommand(newConnectListCommand(deps), newConnectRemoveCommand(deps))
	return cmd
}

func newConnectListCommand(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "list",
		Short:   "Each connection on the instance: name, provider, account, status, scopes (a mailbox's servers) and who uses it",
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
			fmt.Fprintln(w, "NAME\tPROVIDER\tACCOUNT\tSTATUS\tSCOPES OR SERVERS\tUSED BY")
			for _, c := range list {
				fmt.Fprintf(w, "%s\t%s\t%s\t%s\t%s\t%s\n", c.Name, c.Provider, c.Account, c.statusText(), c.detail(), orDash(c.usedByText()))
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
			if target.mailbox() {
				fmt.Fprintf(out, "disconnected %s (%s, a mailbox); its app password is deleted\n", target.Name, target.Account)
				return nil
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

	listener, err := listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
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

// errUnanswered is a request the Host did not answer in time; it was withdrawn.
var errUnanswered = errors.New("the Host did not answer the connect request")

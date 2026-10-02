package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// agentCredentialsRoot is where the CLI and the Host trade agent credential requests:
// <data root>/agent-credentials, the Host's own (0700), because a set request carries the value.
const agentCredentialsRoot = dataRoot + "/agent-credentials"

var (
	// credentialWait is how long the Host has to answer one request before it is withdrawn.
	credentialWait = 60 * time.Second
	credentialPoll = 500 * time.Millisecond
)

// maxCredentialBytes is the longest value the Host stores; a longer one is refused here first.
const maxCredentialBytes = 8 * 1024

// credentialRequest is one request to the Host. Value rides only on a set, and only on stdin.
type credentialRequest struct {
	Request string `json:"request"`
	Action  string `json:"action"`
	Agent   string `json:"agent"`
	Kind    string `json:"kind,omitempty"`
	Value   string `json:"value,omitempty"`
	Source  string `json:"source,omitempty"`
}

// credentialReport is the Host's answer: the route's status and its body.
type credentialReport struct {
	Request string          `json:"request"`
	Status  int             `json:"status"`
	Body    json.RawMessage `json:"body"`
}

// credentialBody is the route's answer to a set, a clear or a source change. It never carries
// the value.
type credentialBody struct {
	Agent   string  `json:"agent"`
	Command string  `json:"command"`
	Source  string  `json:"source"`
	Set     *bool   `json:"set"`
	SetBy   *string `json:"setBy"`
	SetAt   *string `json:"setAt"`
	Error   string  `json:"error"`
}

func newAgentsCredentialCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "credential",
		Short: "Set or clear the credential an agent CLI is issued; the value is never shown",
		Long: "One credential is stored per CLI command (claude, codex, grok, copilot) and is shared by every " +
			"preset that runs that command. A preset uses it only when its source is issued " +
			"(`yawble agents source <preset> issued`); otherwise it signs in through the shared home. " +
			"The value is read from a hidden prompt at a terminal or from the first line of stdin, never " +
			"from the command line, and travels to the instance on stdin. You are responsible for your " +
			"provider's terms when one credential is used by many runs.",
		Example: "  yawble agents credential set claude-headless          # asks, typing hidden\n" +
			"  printf '%s\\n' \"$KEY\" | yawble agents credential set codex\n" +
			"  yawble agents credential set claude --token          # claude takes an API key or a token\n" +
			"  yawble agents credential clear claude",
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newAgentsCredentialSet(deps), newAgentsCredentialClear(deps))
	return cmd
}

// oneName refuses anything but a single preset or command name: a second argument, or a name
// carrying '=', could only be a value, and a value never comes from the command line.
func oneName(verb string) cobra.PositionalArgs {
	return func(_ *cobra.Command, args []string) error {
		switch {
		case len(args) == 0:
			return UsageError{fmt.Sprintf("name the preset or command: yawble agents credential %s <preset|command>", verb)}
		case len(args) > 1 || strings.Contains(args[0], "="):
			return UsageError{"a credential is never taken from the command line, where it would stay in your shell's history; " +
				"nothing was sent. Type it at the prompt or pipe it in: <command> | yawble agents credential set <preset|command>"}
		}
		return nil
	}
}

func newAgentsCredentialSet(deps Deps) *cobra.Command {
	var token, apiKey bool
	cmd := &cobra.Command{
		Use:   "set <preset|command>",
		Short: "Set or replace the credential of a preset's command, from a hidden prompt or stdin",
		Args:  oneName("set"),
		RunE: func(cmd *cobra.Command, args []string) error {
			name := args[0]
			if token && apiKey {
				return UsageError{"--token and --api-key are two kinds; give one"}
			}
			value, err := readCredentialValue(deps, name)
			if err != nil {
				return err
			}
			if err := checkCredentialValue(value); err != nil {
				return err
			}
			e, err := runningEngine(cmd.Context(), deps, "agents credential set")
			if err != nil {
				return err
			}
			// Neither flag leaves the kind to the Host, which takes the only one a CLI declares
			// (codex, grok, copilot) and asks for one where there are two (claude).
			kind := ""
			switch {
			case token:
				kind = "token"
			case apiKey:
				kind = "apiKey"
			}
			body, err := askCredentialHost(cmd.Context(), e, credentialRequest{Action: "set", Agent: name, Kind: kind, Value: value})
			if err != nil {
				if kind == "" && strings.Contains(err.Error(), "the kind must be") {
					return fmt.Errorf("%w Choose it with --api-key or --token", err)
				}
				return err
			}
			command := body.commandOr(name)
			fmt.Fprintf(cmd.OutOrStdout(), "The %s credential is set%s. It is shared by every preset that runs %s; "+
				"a preset uses it when its source is issued (yawble agents source <preset> issued).\n",
				command, body.byText(), command)
			return nil
		},
	}
	cmd.Flags().BoolVar(&token, "token", false, "the value is a token (an OAuth or GitHub token); needed only where the CLI takes two kinds")
	cmd.Flags().BoolVar(&apiKey, "api-key", false, "the value is an API key; needed only where the CLI takes two kinds")
	return cmd
}

func newAgentsCredentialClear(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "clear <preset|command>",
		Short: "Clear the credential of a preset's command",
		Args:  oneName("clear"),
		RunE: func(cmd *cobra.Command, args []string) error {
			e, err := runningEngine(cmd.Context(), deps, "agents credential clear")
			if err != nil {
				return err
			}
			body, err := askCredentialHost(cmd.Context(), e, credentialRequest{Action: "clear", Agent: args[0]})
			if err != nil {
				return err
			}
			command := body.commandOr(args[0])
			fmt.Fprintf(cmd.OutOrStdout(), "The %s credential is cleared. Every preset that runs %s with source issued "+
				"has none until one is set; its member runs do not start meanwhile.\n", command, command)
			return nil
		},
	}
}

func newAgentsSourceCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "source <preset> home|issued",
		Short: "Choose how a preset signs in: the shared home, or its command's issued credential",
		Long: "home signs in through the instance's shared home, as before. issued gives each run the credential " +
			"set with `yawble agents credential set` for the preset's command, and a home of its own; an " +
			"issued member run whose credential is not set does not start.",
		Example: "  yawble agents source claude-headless issued\n  yawble agents source claude-headless home",
		Args:    cobra.ExactArgs(2),
		RunE: func(cmd *cobra.Command, args []string) error {
			preset, source := args[0], args[1]
			if source != "home" && source != "issued" {
				return UsageError{fmt.Sprintf("the source is home or issued, not %q", source)}
			}
			e, err := runningEngine(cmd.Context(), deps, "agents source")
			if err != nil {
				return err
			}
			body, err := askCredentialHost(cmd.Context(), e, credentialRequest{Action: "source", Agent: preset, Source: source})
			if err != nil {
				return err
			}
			if body.Source != "" {
				source = body.Source
			}
			line := fmt.Sprintf("%s signs in through %s", preset, map[string]string{"home": "the shared home", "issued": "its issued credential"}[source])
			if source == "issued" && body.Set != nil && !*body.Set {
				line += "; none is set yet, so its member runs do not start until one is (yawble agents credential set " + preset + ")"
			}
			fmt.Fprintln(cmd.OutOrStdout(), line+".")
			return nil
		},
	}
}

// readCredentialValue is the value from a hidden prompt at a terminal, or the first line of stdin.
// With neither it is a usage error naming both, never a wait on a stdin nobody is typing into.
func readCredentialValue(deps Deps, name string) (string, error) {
	if deps.Interactive {
		if deps.ReadSecret == nil {
			return "", UsageError{fmt.Sprintf("cannot ask for the credential here; pipe it in: <command> | yawble agents credential set %s", name)}
		}
		return deps.ReadSecret(fmt.Sprintf("Credential for %s (typing is hidden): ", name))
	}
	if deps.Stdin == nil {
		return "", UsageError{fmt.Sprintf("no credential for %s: pipe it in (<command> | yawble agents credential set %s) or run this in a terminal to be asked", name, name)}
	}
	line, err := readLine(deps.Stdin)
	if err != nil && !errors.Is(err, io.EOF) {
		return "", err
	}
	return line, nil
}

// checkCredentialValue refuses what the Host would refuse, before anything is sent. The message
// never repeats the value or any part of it.
func checkCredentialValue(value string) error {
	value = strings.TrimRight(value, "\r\n")
	switch {
	case strings.TrimSpace(value) == "":
		return UsageError{"the credential is empty; nothing was sent (yawble agents credential clear removes one)"}
	case len(value) > maxCredentialBytes:
		return UsageError{fmt.Sprintf("the credential is longer than %d bytes; nothing was sent", maxCredentialBytes)}
	case strings.ContainsAny(value, "\r\n\x00"):
		return UsageError{"the credential holds a line break or a NUL; nothing was sent"}
	}
	return nil
}

// askCredentialHost hands one request to the Host on exec stdin - never in exec's arguments - and
// waits credentialWait for the report carrying its nonce. A request no Host answered is withdrawn,
// so a Host started later never carries it out.
func askCredentialHost(ctx context.Context, e engine.Engine, req credentialRequest) (credentialBody, error) {
	req.Request = newNonce()
	req.Value = strings.TrimSpace(req.Value)
	body, _ := json.Marshal(req)
	if _, err := e.ExecInput(ctx, instance.ContainerName, string(body)+"\n", "sh", "-c", credentialRequestScript, "sh", agentCredentialsRoot); err != nil {
		return credentialBody{}, err
	}
	withdraw := func(ctx context.Context) {
		_, _ = e.Exec(ctx, instance.ContainerName, "sh", "-c", credentialWithdrawScript, "sh", agentCredentialsRoot, req.Request)
	}
	deadline := time.Now().Add(credentialWait)
	for {
		res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", credentialReportScript, "sh", agentCredentialsRoot)
		if err != nil {
			withdraw(context.WithoutCancel(ctx))
			return credentialBody{}, err
		}
		var r credentialReport
		if json.Unmarshal([]byte(strings.TrimSpace(res.Stdout)), &r) == nil && r.Request == req.Request {
			var b credentialBody
			_ = json.Unmarshal(r.Body, &b)
			if r.Status < 200 || r.Status > 299 {
				if b.Error != "" {
					return credentialBody{}, errors.New(b.Error)
				}
				return credentialBody{}, fmt.Errorf("the Host refused the %s request (status %d)", req.Action, r.Status)
			}
			return b, nil
		}
		if time.Now().After(deadline) {
			withdraw(ctx)
			return credentialBody{}, fmt.Errorf("the Host did not answer the %s request within %s, and the request was withdrawn. "+
				"An image from before issued credentials does not answer; `yawble update` brings the instance current", req.Action, credentialWait)
		}
		select {
		case <-ctx.Done():
			withdraw(context.WithoutCancel(ctx))
			return credentialBody{}, ctx.Err()
		case <-time.After(credentialPoll):
		}
	}
}

func (b credentialBody) commandOr(name string) string {
	if b.Command != "" {
		return b.Command
	}
	return name
}

func (b credentialBody) byText() string {
	if b.SetBy == nil || *b.SetBy == "" {
		return ""
	}
	text := " by " + *b.SetBy
	if b.SetAt != nil && *b.SetAt != "" {
		text += " at " + *b.SetAt
	}
	return text
}

// The scripts run as root in the container. The request arrives on stdin, is written beside the
// request file with Host-only modes and moved in, so the Host never reads half of it and no agent
// can read it at all.
const (
	// $1 root; stdin: the request's JSON.
	credentialRequestScript = `set -e
umask 077
mkdir -p "$1"
chown harness "$1"
chmod 0700 "$1"
cat > "$1/.request.tmp"
chown harness "$1/.request.tmp"
chmod 0600 "$1/.request.tmp"
mv -f "$1/.request.tmp" "$1/.request"`

	// $1 root.
	credentialReportScript = `cat "$1/.request-report.json" 2>/dev/null || true`

	// $1 root, $2 nonce: removes the request if it is still this one.
	credentialWithdrawScript = `grep -qF "$2" "$1/.request" 2>/dev/null && rm -f "$1/.request"; true`
)

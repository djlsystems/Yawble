package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/spf13/cobra"
)

// linkRoot is where the CLI and the Host trade attach requests: <data root>/link, harness-only
// (0700), because an attach request carries the one-time token.
const linkRoot = dataRoot + "/link"

var (
	// attachWait is how long the Host has to answer: an attach waits on the management service,
	// which the Host gives 30 seconds.
	attachWait = 45 * time.Second
	attachPoll = 500 * time.Millisecond
)

// linkRequest is <data>/link/.attach.
type linkRequest struct {
	Request string `json:"request"`
	Op      string `json:"op"`
	URL     string `json:"url,omitempty"`
	Token   string `json:"token,omitempty"`
}

// linkReport is <data>/link/.attach-report.json: the Host's answer and the sentence a person reads.
// It never carries the token or the credential.
type linkReport struct {
	Request  string      `json:"request"`
	Status   int         `json:"status"`
	Error    *string     `json:"error"`
	Sentence *string     `json:"sentence"`
	Link     *linkStatus `json:"link"`
}

// linkStatus is the stored link as the Host shows it.
type linkStatus struct {
	ServiceURL   string  `json:"serviceUrl"`
	ServiceName  string  `json:"serviceName"`
	InstanceName string  `json:"instanceName"`
	AttachedAt   string  `json:"attachedAt"`
	State        string  `json:"state"`
	StateSince   string  `json:"stateSince"`
	Reason       *string `json:"reason"`
	LastReportAt *string `json:"lastReportAt"`
}

const notAttached = "Not attached."

func newAttachCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "attach <service-url> <token>",
		Short: "Link this instance to a management service with the one-time token it gave you",
		Long: "Attaches the running instance to a management service. The Host spends the one-time token at " +
			"<service-url>/api/link/attach for the link's credential, which it keeps encrypted; the token and the " +
			"credential are never shown, logged or put on a command line. A refused token or a service that cannot be " +
			"reached stores nothing, and an earlier link stays as it was.\n\n" +
			"The service URL must be https://, or http:// only to a loopback or private address.\n\n" +
			"An instance with no CLI attaches at start instead from HARNESS_LINK_URL and HARNESS_LINK_TOKEN, once, " +
			"and only while it is not attached.\n\n" +
			"It reaches the Host the way `connect` does: through the container engine, by a request file the Host answers.",
		Example: "  yawble attach https://manage.example.com 3f9c2a7e\n" +
			"  yawble attach status\n" +
			"  yawble detach",
		Args: cobra.ExactArgs(2),
		RunE: func(cmd *cobra.Command, args []string) error {
			url, token := strings.TrimSpace(args[0]), strings.TrimSpace(args[1])
			if url == "" || token == "" {
				return UsageError{"give the service's URL and the token it issued: yawble attach <service-url> <token>"}
			}
			return askLink(cmd.Context(), cmd.OutOrStdout(), deps, "attach", linkRequest{Op: "attach", URL: url, Token: token})
		},
	}
	cmd.AddCommand(newAttachStatusCommand(deps))
	return cmd
}

func newAttachStatusCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:     "status",
		Short:   "Which management service this instance is attached to, the link's state and its last report",
		Example: "  yawble attach status",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			ctx, out := cmd.Context(), cmd.OutOrStdout()
			e, err := runningEngine(ctx, deps, "attach status")
			if err != nil {
				return err
			}
			r, err := linkExchange(ctx, e, linkRequest{Op: "status"})
			if err != nil {
				return err
			}
			if !r.ok() {
				return r.refusal("the Host did not say whether it is attached")
			}
			if r.Link == nil {
				fmt.Fprintln(out, notAttached)
				return nil
			}
			return printLink(out, *r.Link)
		},
	}
}

func newDetachCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "detach",
		Short: "Unlink this instance from its management service and forget the link's credential",
		Long: "Detaches the running instance from its management service: the Host closes the link and forgets " +
			"its credential. Attaching again needs a new token from the service.",
		Example: "  yawble detach",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			return askLink(cmd.Context(), cmd.OutOrStdout(), deps, "detach", linkRequest{Op: "detach"})
		},
	}
}

// askLink hands one attach or detach to the Host and prints its sentence, or fails with it.
func askLink(ctx context.Context, out io.Writer, deps Deps, command string, req linkRequest) error {
	e, err := runningEngine(ctx, deps, command)
	if err != nil {
		return err
	}
	r, err := linkExchange(ctx, e, req)
	if err != nil {
		return err
	}
	if !r.ok() {
		return r.refusal("the Host did not " + req.Op)
	}
	if r.Sentence != nil && *r.Sentence != "" {
		fmt.Fprintln(out, *r.Sentence)
	}
	return nil
}

func printLink(out io.Writer, l linkStatus) error {
	last := "never"
	if l.LastReportAt != nil && *l.LastReportAt != "" {
		last = *l.LastReportAt
	}
	w := tabwriter.NewWriter(out, 0, 4, 2, ' ', 0)
	fmt.Fprintf(w, "Service:\t%s (%s)\n", l.ServiceName, l.ServiceURL)
	fmt.Fprintf(w, "Instance:\t%s\n", l.InstanceName)
	fmt.Fprintf(w, "State:\t%s since %s\n", l.State, l.StateSince)
	if l.Reason != nil && *l.Reason != "" {
		fmt.Fprintf(w, "Reason:\t%s\n", *l.Reason)
	}
	fmt.Fprintf(w, "Last report:\t%s\n", last)
	return w.Flush()
}

func (r linkReport) ok() bool { return r.Status >= 200 && r.Status < 300 }

// refusal is the Host's own sentence - the service's, verbatim, for a refused attach - or what did
// not happen when it gave none.
func (r linkReport) refusal(what string) error {
	if r.Error != nil && *r.Error != "" {
		return errors.New(*r.Error)
	}
	return fmt.Errorf("%s (status %d)", what, r.Status)
}

// errAttachUnanswered is an attach request the Host did not answer in time; it was withdrawn.
var errAttachUnanswered = errors.New("the Host did not answer the attach request")

// linkExchange hands one request to the Host and waits attachWait for the report that answers it.
func linkExchange(ctx context.Context, e engine.Engine, req linkRequest) (linkReport, error) {
	req.Request = newNonce()
	body, _ := json.Marshal(req)
	// ON STDIN, never argv: an attach request carries the token.
	x := hostExchange{root: linkRoot, request: attachRequestScript, report: attachReportScript, withdraw: attachWithdrawScript, onStdin: true, poll: attachPoll}
	answer, err := x.ask(ctx, e, string(body), req.Request, attachWait)
	if errors.Is(err, errNoAnswer) {
		return linkReport{}, fmt.Errorf("%w within %s, and the request was withdrawn. "+
			"An image from before the management link does not answer; `yawble update` brings the instance current", errAttachUnanswered, attachWait)
	}
	if err != nil {
		return linkReport{}, err
	}
	var r linkReport
	_ = json.Unmarshal([]byte(answer), &r)
	return r, nil
}

// The scripts run as root in the container, as connect's do: the request arrives on stdin, is
// written beside the request file with harness-only modes and moved in.
const (
	// $1 root; stdin: the request's JSON.
	attachRequestScript = `set -e
umask 077
mkdir -p "$1"
chown harness "$1"
chmod 0700 "$1"
cat > "$1/.attach.tmp"
chown harness "$1/.attach.tmp"
chmod 0600 "$1/.attach.tmp"
mv -f "$1/.attach.tmp" "$1/.attach"`

	// $1 root.
	attachReportScript = `cat "$1/.attach-report.json" 2>/dev/null || true`

	// $1 root, $2 nonce: removes the request if it is still this one.
	attachWithdrawScript = `grep -qF "$2" "$1/.attach" 2>/dev/null && rm -f "$1/.attach"; true`
)

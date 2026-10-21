package cli

import (
	"context"
	"encoding/json"
	"strings"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// hostExchange is the one way the CLI hands the Host a request it cannot make over the API: a
// request file written into a folder only the Host and root can write, answered by a report file
// carrying the request's nonce. A plugin install, a plugin rescan, a solution install and a
// connection all go this way; each names its scripts and its folder, and this is the loop.
//
// A request no Host answered within its wait, or one interrupted, is withdrawn, so a Host started
// later never carries it out; an exchange with no withdraw script is one whose request left behind
// is harmless. The withdraw runs with the cancellation lifted, since it is the cancel being tidied.
type hostExchange struct {
	// root is the folder in the instance: $1 of every script.
	root string
	// request writes the request: $2 is the body, or the body comes on stdin when onStdin is set.
	request string
	// report prints the report file, or nothing while there is none.
	report string
	// withdraw removes the request when it still carries $2, the nonce; empty when none is needed.
	withdraw string
	// onStdin hands the body on stdin rather than as $2: a body that carries a credential.
	onStdin bool
	// poll is how long to wait between two reads of the report.
	poll time.Duration
}

// ask writes the request and waits for the report whose "request" field is nonce, answering the
// report's text. A report that never comes is errNoAnswer after the request was withdrawn; an
// interruption is the context's error, likewise after the withdraw.
func (x hostExchange) ask(ctx context.Context, e engine.Engine, body, nonce string, wait time.Duration) (string, error) {
	var err error
	if x.onStdin {
		_, err = e.ExecInput(ctx, instance.ContainerName, body+"\n", "sh", "-c", x.request, "sh", x.root)
	} else {
		_, err = e.Exec(ctx, instance.ContainerName, "sh", "-c", x.request, "sh", x.root, body)
	}
	if err != nil {
		return "", err
	}

	deadline := time.Now().Add(wait)
	for {
		res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", x.report, "sh", x.root)
		if err != nil {
			return "", err
		}
		var r struct {
			Request string `json:"request"`
		}
		text := strings.TrimSpace(res.Stdout)
		if json.Unmarshal([]byte(text), &r) == nil && r.Request == nonce {
			return text, nil
		}
		if time.Now().After(deadline) {
			x.take(ctx, e, nonce)
			return "", errNoAnswer
		}
		select {
		case <-ctx.Done():
			x.take(ctx, e, nonce)
			return "", ctx.Err()
		case <-time.After(x.poll):
		}
	}
}

// take withdraws the request when the exchange has a withdraw script, with the cancellation lifted.
func (x hostExchange) take(ctx context.Context, e engine.Engine, nonce string) {
	if x.withdraw == "" {
		return
	}
	_, _ = e.Exec(context.WithoutCancel(ctx), instance.ContainerName, "sh", "-c", x.withdraw, "sh", x.root, nonce)
}

package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"
)

// connectDevice runs one sign-in with a code: start on the Host, which asks the provider for a code
// and keeps the provider's device code itself; print the link and the code; read the flow until the
// Host says it is done, refused or expired. No listener, no browser, no redirect.
func connectDevice(ctx context.Context, out io.Writer, deps Deps, provider string, scopes []string, name string) error {
	e, err := runningEngine(ctx, deps, "connect")
	if err != nil {
		return err
	}

	request := connectRequest{Op: "start", Provider: provider, Scopes: scopes, Flow: "device"}
	if name != "" {
		before, err := listConnections(ctx, e)
		if err != nil {
			return err
		}
		if existing := sameName(before, provider, name); existing != nil {
			request.ReconnectID = existing.ID
			fmt.Fprintf(out, "reconnecting %s (%s)\n", existing.Name, existing.Account)
		} else {
			request.Name = name
		}
	}

	started, err := exchange(ctx, e, request)
	if err != nil {
		return err
	}
	if !started.ok() || started.Start == nil {
		return started.refusal("the Host did not start the sign-in with a code")
	}
	flow := *started.Start
	if flow.FlowID == "" || flow.UserCode == "" || flow.VerificationURI == "" {
		return errors.New("the instance does not sign in with a code yet; `yawble update` brings it current")
	}

	wait := browserWait
	expiry := ""
	if expires, err := time.Parse(time.RFC3339, flow.ExpiresAt); err == nil {
		left := time.Until(expires)
		wait = max(left, 0) + deviceGrace
		expiry = fmt.Sprintf(" The code expires in %s.", left.Round(time.Minute))
	}
	fmt.Fprintf(out, "to sign in at %s, open this address in any browser:\n\n  %s\n\nand enter the code:\n\n  %s\n\n", provider, flow.VerificationURI, flow.UserCode)
	fmt.Fprintf(out, "waiting for you to sign in and agree there (Ctrl+C to stop waiting).%s\n", expiry)

	deadline := time.Now().Add(wait)
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(deviceRead):
		}
		read, err := exchange(ctx, e, connectRequest{Op: "flow", FlowID: flow.FlowID})
		if err != nil {
			return err
		}
		if !read.ok() || read.Flow == nil {
			return read.refusal("the Host did not say how the sign-in is going")
		}
		switch read.Flow.State {
		case "waiting":
			// A SECOND CODE: the Host now waits on another code for the same sign-in; say so and
			// print it, and wait until that code expires.
			if f := read.Flow; f.UserCode != "" && f.VerificationURI != "" && f.UserCode != flow.UserCode {
				flow.UserCode, flow.VerificationURI = f.UserCode, f.VerificationURI
				if f.Sentence != "" {
					fmt.Fprintln(out, f.Sentence)
				}
				fmt.Fprintf(out, "\nopen this address in any browser:\n\n  %s\n\nand enter the new code:\n\n  %s\n\n", f.VerificationURI, f.UserCode)
				if expires, err := time.Parse(time.RFC3339, f.ExpiresAt); err == nil {
					deadline = time.Now().Add(max(time.Until(expires), 0) + deviceGrace)
				}
			}
			if time.Now().After(deadline) {
				return errors.New("the code has expired and the Host has not said so; run the command again for a new code")
			}
			continue
		case "done":
			c := read.Flow.Connection
			if c == nil {
				return errors.New("the Host says the sign-in is done but named no connection; `yawble connect list` shows what is stored")
			}
			verb := "connected"
			if request.ReconnectID != "" {
				verb = "reconnected"
			}
			fmt.Fprintf(out, "%s %s: %s at %s, scopes %s\n", verb, c.Name, c.Account, c.Provider, orDash(strings.Join(c.Scopes, " ")))
			fmt.Fprintln(out, "bind it to a plugin member in that member's settings in the web UI")
			return nil
		default:
			if read.Flow.Sentence != "" {
				return errors.New(read.Flow.Sentence)
			}
			return fmt.Errorf("the sign-in ended %s and nothing was connected", read.Flow.State)
		}
	}
}

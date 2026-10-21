package cli

import (
	"context"
	"fmt"
	"html"
	"net"
	"net/http"
	"time"
)

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

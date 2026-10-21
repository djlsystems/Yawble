package cli

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

func exchangeUnderTest() hostExchange {
	return hostExchange{
		root:     "/data/plugins",
		request:  "printf '%s' \"$2\" > \"$1/.ask\"",
		report:   "cat \"$1/.ask-report.json\" 2>/dev/null || true",
		withdraw: "grep -qF \"$2\" \"$1/.ask\" 2>/dev/null && rm -f \"$1/.ask\"; true",
		poll:     time.Millisecond,
	}
}

func TestAnExchangeReturnsTheReportThatAnswersItsNonce(t *testing.T) {
	s := engine.NewScripted()
	s.OnSequence("podman exec -e HARNESS_WORKER_KEY= yawble sh -c cat \"$1/.ask-report.json\"", engine.Result{Stdout: `{"request":"older","status":1}`}, engine.Result{Stdout: `{"request":"n1","status":200}`})
	x := exchangeUnderTest()

	report, err := x.ask(context.Background(), engine.NewPodman(s), `{"request":"n1"}`, "n1", time.Second)

	if err != nil || !strings.Contains(report, `"status":200`) {
		t.Fatalf("report %q err %v", report, err)
	}
	if !strings.Contains(s.Calls[0], "/data/plugins {\"request\":\"n1\"}") {
		t.Errorf("the request was not written with its body: %q", s.Calls[0])
	}
	for _, call := range s.Calls {
		if strings.Contains(call, "rm -f") {
			t.Errorf("an answered request was withdrawn: %q", call)
		}
	}
}

func TestAnExchangeNobodyAnswersWithdrawsItsRequestAndSaysNoAnswer(t *testing.T) {
	s := engine.NewScripted()
	x := exchangeUnderTest()

	_, err := x.ask(context.Background(), engine.NewPodman(s), `{"request":"n2"}`, "n2", 0)

	if !errors.Is(err, errNoAnswer) {
		t.Fatalf("err %v", err)
	}
	if !strings.Contains(s.Calls[len(s.Calls)-1], "rm -f") || !strings.Contains(s.Calls[len(s.Calls)-1], " n2") {
		t.Errorf("the request was not withdrawn last: %q", s.Calls[len(s.Calls)-1])
	}
}

func TestACancelledExchangeWithdrawsItsRequestAndReturnsTheCancel(t *testing.T) {
	s := engine.NewScripted()
	x := exchangeUnderTest()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	_, err := x.ask(ctx, engine.NewPodman(s), `{"request":"n3"}`, "n3", time.Minute)

	if !errors.Is(err, context.Canceled) {
		t.Fatalf("err %v", err)
	}
	if !strings.Contains(s.Calls[len(s.Calls)-1], "rm -f") || !strings.Contains(s.Calls[len(s.Calls)-1], " n3") {
		t.Errorf("the request was not withdrawn: %q", s.Calls[len(s.Calls)-1])
	}
}

func TestAnExchangeWithNoWithdrawLeavesItsRequestWhenNobodyAnswers(t *testing.T) {
	s := engine.NewScripted()
	x := exchangeUnderTest()
	x.withdraw = ""

	_, err := x.ask(context.Background(), engine.NewPodman(s), "n4", "n4", 0)

	if !errors.Is(err, errNoAnswer) {
		t.Fatalf("err %v", err)
	}
	for _, call := range s.Calls {
		if strings.Contains(call, "rm -f") {
			t.Errorf("a request with no withdraw was withdrawn: %q", call)
		}
	}
}

func TestAnExchangeOnStdinNeverPutsTheBodyOnACommandLine(t *testing.T) {
	s := engine.NewScripted()
	x := exchangeUnderTest()
	x.onStdin = true
	s.On("podman exec -e HARNESS_WORKER_KEY= yawble sh -c cat \"$1/.ask-report.json\"", engine.Result{Stdout: `{"request":"n5"}`})

	_, err := x.ask(context.Background(), engine.NewPodman(s), `{"request":"n5","code":"secret-code"}`, "n5", time.Second)

	if err != nil {
		t.Fatal(err)
	}
	for _, call := range s.Calls {
		if strings.Contains(call, "secret-code") {
			t.Errorf("the body reached a command line: %q", call)
		}
	}
	if len(s.Inputs) != 1 || !strings.Contains(s.Inputs[0], "secret-code") {
		t.Errorf("the body was not handed on stdin: %v", s.Inputs)
	}
}

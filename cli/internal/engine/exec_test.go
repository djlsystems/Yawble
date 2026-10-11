package engine

import (
	"bytes"
	"context"
	"os/exec"
	"runtime"
	"strings"
	"testing"
	"time"
)

// xdg-open started a browser that was not yet running (the first `yawble up` after a reboot); the
// browser kept the opener's output pipes and `yawble up` waited on them until it closed. A program
// that has exited is finished, whatever a child it left behind still holds.
func TestAProgramThatExitsIsDoneWhileAChildItLeftHoldsItsOutput(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("needs a POSIX sh that backgrounds a child holding stdout")
	}
	if _, err := exec.LookPath("sh"); err != nil {
		t.Skip("no sh on PATH")
	}
	r := ExecRunner()
	ctx := context.Background()

	cases := map[string]func() (string, error){
		"Run": func() (string, error) {
			res, err := r.Run(ctx, "sh", "-c", "sleep 30 & echo opened")
			return res.Stdout, err
		},
		"Stream": func() (string, error) {
			var out bytes.Buffer
			_, err := r.Stream(ctx, &out, "sh", "-c", "sleep 30 & echo opened")
			return out.String(), err
		},
		"RunPipe": func() (string, error) {
			res, err := r.RunPipe(ctx, strings.NewReader(""), nil, "sh", "-c", "sleep 30 & echo opened")
			return res.Stdout, err
		},
	}
	for name, run := range cases {
		t.Run(name, func(t *testing.T) {
			start := time.Now()
			out, err := run()
			if err != nil {
				t.Fatalf("err = %v, want none: the program exited 0", err)
			}
			if !strings.Contains(out, "opened") {
				t.Fatalf("stdout = %q, want what the program wrote before it exited", out)
			}
			if took := time.Since(start); took > 10*time.Second {
				t.Fatalf("took %s: it waited for the child holding the pipe, not for the program", took)
			}
		})
	}
}

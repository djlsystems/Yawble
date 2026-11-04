package instance_test

import (
	"bytes"
	"context"
	"strings"
	"testing"
	"time"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// timerless is an engine whose own health check timer never fires: its record of every worker
// stays "starting" until the check is run by hand (`podman healthcheck run`), and a run of a
// check that passes flips the record to healthy, as Podman does. fails names the containers
// whose check really fails. Everything else is the scripted engine.
type timerless struct {
	*engine.Scripted
	fails   map[string]bool
	checked map[string]bool
}

func newTimerless(s *engine.Scripted, fails ...string) *timerless {
	t := &timerless{Scripted: s, fails: map[string]bool{}, checked: map[string]bool{}}
	for _, name := range fails {
		t.fails[name] = true
	}
	return t
}

const podmanHealth = "podman container inspect --format {{.State.Health.Status}} "

func (t *timerless) Run(ctx context.Context, name string, args ...string) (engine.Result, error) {
	line := name + " " + strings.Join(args, " ")
	if c, ok := strings.CutPrefix(line, "podman healthcheck run "); ok {
		t.Calls = append(t.Calls, line)
		if t.fails[c] {
			return engine.Result{Stdout: "starting\n", ExitCode: 1}, nil
		}
		t.checked[c] = true
		return engine.Result{Stdout: "healthy\n"}, nil
	}
	if c, ok := strings.CutPrefix(line, podmanHealth); ok && strings.HasPrefix(c, instance.ContainerName+"-worker-") {
		t.Calls = append(t.Calls, line)
		if t.checked[c] {
			return engine.Result{Stdout: "healthy\n"}, nil
		}
		return engine.Result{Stdout: "starting\n"}, nil
	}
	return t.Scripted.Run(ctx, name, args...)
}

// The engine's record stays "starting" until the check is run: up runs it on the first poll,
// takes its pass, says the worker is connected and returns long before the timeout.
func TestUpRunsAWorkersHealthCheckItselfWhenTheEngineNeverDoes(t *testing.T) {
	defer instance.SetPollingForTests(10*time.Millisecond, time.Minute)()
	st := keyed()
	st.Workers = 2
	tl := newTimerless(fresh("podman", podmanHealth, 0))
	var out bytes.Buffer
	started := time.Now()
	if err := instance.Up(context.Background(), engine.NewPodman(tl), st, healthy, &out); err != nil {
		t.Fatal(err)
	}
	if took := time.Since(started); took > 5*time.Second {
		t.Errorf("up took %s: it waited instead of running the check", took)
	}
	for _, w := range []string{"yawble-worker-1", "yawble-worker-2"} {
		if n := countPrefix(tl.Calls, "podman healthcheck run "+w); n != 1 {
			t.Errorf("%s: check run %d times, want once (it passed on the first)", w, n)
		}
		if !strings.Contains(out.String(), w+" is connected to control\n") {
			t.Errorf("out %q", out.String())
		}
	}
}

// A check that really fails, run by hand on every poll, still times out with the sentence a
// worker that never connects has always had.
func TestAWorkerWhoseCheckReallyFailsStillTimesOutWithTheSameSentence(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, 50*time.Millisecond)()
	tl := newTimerless(fresh("podman", podmanHealth, 0), "yawble-worker-1")
	tl.On(engines[0].inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|\n"})
	err := instance.Up(context.Background(), engine.NewPodman(tl), keyed(), healthy, &bytes.Buffer{})
	want := "yawble-worker-1 did not connect to control within 50ms; read `yawble logs worker-1`"
	if err == nil || err.Error() != want {
		t.Fatalf("err %v, want %q", err, want)
	}
	if n := countPrefix(tl.Calls, "podman healthcheck run yawble-worker-1"); n < 2 {
		t.Errorf("check run %d times, want once a poll", n)
	}
}

// Docker runs its checks in its daemon: nothing is run by hand, its record is the answer, and a
// worker that never reads healthy times out with the same sentence.
func TestDockerHealthIsTheDaemonsRecordAndNothingIsRunByHand(t *testing.T) {
	defer instance.SetPollingForTests(time.Millisecond, time.Minute)()
	health := engines[1].health
	s := fresh("docker", health, 0)
	starting := engine.Result{Stdout: "starting\n"}
	s.OnSequence(health+"yawble-worker-1", starting, starting, engine.Result{Stdout: "healthy\n"})
	var out bytes.Buffer
	if err := instance.Up(context.Background(), engine.NewDocker(s), keyed(), healthy, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "yawble-worker-1 is connected to control") {
		t.Errorf("out %q", out.String())
	}
	for _, c := range s.Calls {
		if strings.Contains(c, "healthcheck") {
			t.Errorf("docker ran %q", c)
		}
	}

	defer instance.SetPollingForTests(time.Millisecond, 50*time.Millisecond)()
	s = fresh("docker", health, 0)
	s.On(health+"yawble-worker-1", starting)
	s.On(engines[1].inspect+"yawble-worker-1", engine.Result{Stdout: "running|x|\n"})
	err := instance.Up(context.Background(), engine.NewDocker(s), keyed(), healthy, &bytes.Buffer{})
	if want := "yawble-worker-1 did not connect to control within 50ms; read `yawble logs worker-1`"; err == nil || err.Error() != want {
		t.Errorf("err %v, want %q", err, want)
	}
}

// status never says a worker is starting or unhealthy only because the engine's timer did not
// run: its check is run, its answer shown, and what the engine read is kept beside it.
func TestStatusRunsTheCheckWhenTheEngineStillReadsStarting(t *testing.T) {
	tl := newTimerless(engine.NewScripted(), "yawble-worker-2")
	tl.On(engines[0].inspect+"yawble-worker-", engine.Result{Stdout: "running|x|\n"})
	st := keyed()
	st.Workers = 2
	ws, err := instance.Workers(context.Background(), engine.NewPodman(tl), st)
	if err != nil {
		t.Fatal(err)
	}
	if len(ws) != 2 {
		t.Fatalf("workers %+v", ws)
	}
	if ws[0].Health != engine.HealthHealthy || ws[0].EngineHealth != engine.HealthStarting {
		t.Errorf("worker-1 %+v: want healthy by the check run, the engine reading starting", ws[0])
	}
	if ws[1].Health != engine.HealthStarting || ws[1].EngineHealth != engine.HealthStarting {
		t.Errorf("worker-2 %+v: a check that fails inside its start period is starting", ws[1])
	}
}

// An engine that reads healthy is taken at its word: nothing is run.
func TestAHealthyRecordRunsNoCheck(t *testing.T) {
	s := engine.NewScripted()
	s.On(podmanHealth+"yawble-worker-1", engine.Result{Stdout: "healthy\n"})
	h, said, err := instance.CurrentHealth(context.Background(), engine.NewPodman(s), "yawble-worker-1")
	if err != nil || h != engine.HealthHealthy || said != "" {
		t.Errorf("h %q said %q err %v", h, said, err)
	}
	if n := countPrefix(s.Calls, "podman healthcheck run"); n != 0 {
		t.Errorf("ran the check %d times", n)
	}
}

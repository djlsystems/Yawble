package doctor

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// WorkersRecord is the report's `workers` section: control's own record of each worker that
// connected, with its runs and the Host's measured figures. Control writes it; nothing here
// derives a figure of its own.
type WorkersRecord struct {
	RecordedAt string         `json:"recordedAt"`
	Items      []WorkerRecord `json:"items"`
}

// WorkerRecord is one worker. State is "connected" or "dropped".
type WorkerRecord struct {
	ID             string          `json:"id"`
	Version        string          `json:"version"`
	State          string          `json:"state"`
	Draining       bool            `json:"draining"`
	ConnectedSince *string         `json:"connectedSince"`
	DroppedAt      *string         `json:"droppedAt"`
	Runs           []WorkerRun     `json:"runs"`
	Capacity       *WorkerCapacity `json:"capacity"`
}

type WorkerRun struct {
	Run    string `json:"run"`
	Team   string `json:"team"`
	Member string `json:"member"`
}

// WorkerCapacity is the Host's measurement of a worker: its CPUs, memory in use and limit, and
// the runs it holds at once. NotMeasured: the worker could not say.
type WorkerCapacity struct {
	CPUs             *int   `json:"cpus"`
	MemoryLimitBytes *int64 `json:"memoryLimitBytes"`
	MemoryInUseBytes *int64 `json:"memoryInUseBytes"`
	Bound            *int   `json:"bound"`
	NotMeasured      bool   `json:"notMeasured"`
}

// Worker finds a worker by id in the record; nil when control recorded none of that id.
func (r *WorkersRecord) Worker(id string) *WorkerRecord {
	if r == nil {
		return nil
	}
	for i := range r.Items {
		if r.Items[i].ID == id {
			return &r.Items[i]
		}
	}
	return nil
}

// StateText is the worker's state in one word: connected, draining or dropped.
func (w WorkerRecord) StateText() string {
	if w.State == "connected" && w.Draining {
		return "draining"
	}
	return w.State
}

// RunsText is "2 runs (team/member, team/member)", "no runs".
func (w WorkerRecord) RunsText() string {
	if len(w.Runs) == 0 {
		return "no runs"
	}
	names := make([]string, len(w.Runs))
	for i, r := range w.Runs {
		names[i] = r.Team + "/" + r.Member
	}
	unit := "runs"
	if len(w.Runs) == 1 {
		unit = "run"
	}
	return fmt.Sprintf("%d %s (%s)", len(w.Runs), unit, strings.Join(names, ", "))
}

// FiguresText is the Host's measured figures for the worker, or "not measured".
func (c *WorkerCapacity) FiguresText() string {
	if c == nil || c.NotMeasured {
		return "not measured"
	}
	var parts []string
	if c.MemoryInUseBytes != nil && c.MemoryLimitBytes != nil {
		parts = append(parts, fmt.Sprintf("memory %d of %d MB", *c.MemoryInUseBytes>>20, *c.MemoryLimitBytes>>20))
	} else if c.MemoryLimitBytes != nil {
		parts = append(parts, fmt.Sprintf("memory limit %d MB", *c.MemoryLimitBytes>>20))
	}
	if c.CPUs != nil {
		parts = append(parts, fmt.Sprintf("%d CPUs", *c.CPUs))
	}
	if c.Bound != nil {
		parts = append(parts, fmt.Sprintf("holds %d runs at once", *c.Bound))
	}
	if len(parts) == 0 {
		return "not measured"
	}
	return strings.Join(parts, ", ")
}

// ErrNoWorkerRecord is a report that carries no workers record: an older Host, or control has
// recorded none yet.
var ErrNoWorkerRecord = errors.New("control's report carries no record of its workers")

// RunsOf reads one worker from control's report, its runs and whether it is draining, for
// `yawble workers` to drain and wait on.
func RunsOf(fetch func(context.Context) (*HostReport, error)) instance.RunsOf {
	return func(ctx context.Context, id string) (instance.Recorded, error) {
		r, err := fetch(ctx)
		if err != nil {
			return instance.Recorded{}, err
		}
		if r.Workers == nil {
			return instance.Recorded{}, ErrNoWorkerRecord
		}
		w := r.Workers.Worker(id)
		if w == nil {
			return instance.Recorded{}, nil
		}
		rec := instance.Recorded{Runs: make([]instance.Run, len(w.Runs)), Draining: w.Draining, Connected: w.State == "connected"}
		for i, run := range w.Runs {
			rec.Runs[i] = instance.Run{Run: run.Run, Team: run.Team, Member: run.Member}
		}
		return rec, nil
	}
}

// WorkerObserved is one worker container as the engine sees it, with the engine's own stats.
type WorkerObserved struct {
	instance.WorkerStatus
	Stats    *engine.Stats
	StatsErr error
}

// WorkerChecks are the doctor's rows for the workers: one per worker container, the engine's
// stats beside the Host's figures for it; a warning for a worker that runs and is not connected,
// one whose image is not control's worker image, one that is stopped, and a container count that
// is not the configured count.
func WorkerChecks(workers []WorkerObserved, r *HostReport, configured int, workerRef string) []Check {
	var checks []Check
	var record *WorkersRecord
	if r != nil {
		record = r.Workers
	}
	count := 0
	for _, w := range workers {
		if w.Container == engine.StateAbsent {
			checks = append(checks, Check{w.ID, Warn, w.Name + " does not exist", "yawble up makes it"})
			continue
		}
		count++
		rec := record.Worker(w.ID)
		host := "control has no record of it"
		if rec != nil {
			host = fmt.Sprintf("%s, version %s, %s; Host: %s", rec.StateText(), rec.Version, rec.RunsText(), rec.Capacity.FiguresText())
		}
		stats := "engine: not running"
		switch {
		case w.StatsErr != nil:
			stats = "engine: not measured: " + w.StatsErr.Error()
		case w.Stats != nil:
			stats = "engine: " + w.Stats.Summary()
		}
		detail := host + "; " + stats
		switch {
		case w.Container == engine.StateStopped:
			checks = append(checks, Check{w.ID, Warn, w.Name + " is stopped; " + detail, "yawble doctor --fix starts it"})
		case workerRef != "" && w.Image != "" && w.Image != workerRef:
			checks = append(checks, Check{w.ID, Warn, fmt.Sprintf("%s runs %s, not %s, control's worker image; %s", w.Name, w.Image, workerRef, detail), "yawble up replaces it"})
		case rec == nil || rec.State != "connected":
			checks = append(checks, Check{w.ID, Warn, w.Name + " runs but is not connected to control; " + detail, "yawble logs " + w.ID + " says why"})
		default:
			checks = append(checks, Check{w.ID, OK, detail, ""})
		}
	}
	if count != configured {
		checks = append(checks, Check{"workers", Warn, fmt.Sprintf("%d worker container(s), yawble's config asks for %d", count, configured), fmt.Sprintf("yawble workers %d applies the count", configured)})
	}
	return checks
}

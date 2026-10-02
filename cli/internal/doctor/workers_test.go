package doctor_test

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
)

// Control's record of its workers, in the shape the Host writes it, decodes into each worker's
// state, runs and measured figures; a draining worker reads draining, a dropped one dropped.
func TestTheWorkersRecordDecodes(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join("testdata", "host-doctor-workers.json"))
	if err != nil {
		t.Fatal(err)
	}
	r, err := doctor.ParseHostReport("Host log: /data/logs/x\n" + string(raw))
	if err != nil {
		t.Fatal(err)
	}
	if r.Workers == nil || r.Workers.RecordedAt != "2026-10-05T10:00:00Z" || len(r.Workers.Items) != 3 {
		t.Fatalf("workers %+v", r.Workers)
	}
	w1, w2, w3 := r.Workers.Worker("worker-1"), r.Workers.Worker("worker-2"), r.Workers.Worker("worker-3")
	if w1 == nil || w1.StateText() != "connected" || w1.RunsText() != "1 run (acme/Ada)" || w1.Capacity.FiguresText() != "memory 0 of 8192 MB, 4 CPUs, holds 3 runs at once" {
		t.Errorf("worker-1 %+v %q %q", w1, w1.RunsText(), w1.Capacity.FiguresText())
	}
	if w2 == nil || w2.StateText() != "draining" || w2.RunsText() != "no runs" || w2.Capacity.FiguresText() != "not measured" {
		t.Errorf("worker-2 %+v", w2)
	}
	if w3 == nil || w3.StateText() != "dropped" || w3.Capacity.FiguresText() != "not measured" || w3.DroppedAt == nil {
		t.Errorf("worker-3 %+v", w3)
	}
	if r.Workers.Worker("worker-9") != nil {
		t.Error("a worker control never recorded was found")
	}

	runsOf := doctor.RunsOf(func(context.Context) (*doctor.HostReport, error) { return &r, nil })
	if runs, err := runsOf(context.Background(), "worker-1"); err != nil || len(runs) != 1 || runs[0].Team != "acme" || runs[0].Member != "Ada" || runs[0].Run != "r-17" {
		t.Errorf("runs of worker-1: %+v %v", runs, err)
	}
	if runs, err := runsOf(context.Background(), "worker-9"); err != nil || len(runs) != 0 {
		t.Errorf("a worker with no record holds no runs: %+v %v", runs, err)
	}
}

// A report with no workers record - an older Host, or the single-process form - is not "no runs":
// the runs are not known, and `yawble workers` stops nothing on that.
func TestAReportWithNoWorkersRecordLeavesTheRunsUnknown(t *testing.T) {
	r, err := doctor.ParseHostReport(`{"at":"x","agents":[],"workers":null}`)
	if err != nil || r.Workers != nil {
		t.Fatalf("%+v %v", r.Workers, err)
	}
	runsOf := doctor.RunsOf(func(context.Context) (*doctor.HostReport, error) { return &r, nil })
	if _, err := runsOf(context.Background(), "worker-1"); err != doctor.ErrNoWorkerRecord {
		t.Errorf("err %v", err)
	}
}

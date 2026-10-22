package doctor_test

import (
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// The preview row says the configured range and the ports of it published, as control's label
// records them: a port skipped because it was taken is information; none published is a warning.
func TestThePreviewRowShowsTheRangeAndThePortsPublished(t *testing.T) {
	for _, c := range []struct {
		name             string
		configured, made string
		published        []int
		container        engine.State
		verdict          doctor.Verdict
		detail           string
	}{
		{"all published", "28081-28083", "28081-28083", []int{28081, 28082, 28083}, engine.StateRunning, doctor.OK, "range 28081-28083; published 28081-28083"},
		{"one taken", "28081-28083", "28081-28083", []int{28081, 28083}, engine.StateRunning, doctor.OK, "range 28081-28083; published 28081,28083 (not 28082: in use when the instance was made)"},
		{"none free", "28081-28083", "28081-28083", []int{}, engine.StateRunning, doctor.Warn, "none published"},
		{"off", "", "", []int{}, engine.StateRunning, doctor.Skip, "previews are off (previewPorts is empty)"},
		{"off but published", "", "28081-28083", []int{28081}, engine.StateRunning, doctor.Warn, "still publishes 28081"},
		{"made before the range", "28081-28083", "", []int{}, engine.StateRunning, doctor.Warn, "the instance publishes none, made with none"},
		{"no container", "28081-28083", "", nil, engine.StateAbsent, doctor.Skip, "nothing published until the instance is made"},
	} {
		o := healthyObserved()
		o.Container = c.container
		o.PreviewPorts, o.PreviewMadeWith, o.PreviewPublished = c.configured, c.made, c.published
		row := find(t, doctor.HostChecks(o), "preview")
		if row.Verdict != c.verdict || !strings.Contains(row.Detail, c.detail) {
			t.Errorf("%s: %+v", c.name, row)
		}
	}
}

// The row is a key row: the short list always shows it.
func TestTheShortListAlwaysShowsThePreviewRow(t *testing.T) {
	shown, _ := doctor.Short(doctor.HostChecks(healthyObserved()))
	for _, c := range shown {
		if c.Name == "preview" {
			return
		}
	}
	t.Errorf("no preview row in %+v", shown)
}

package cli_test

import (
	"regexp"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/engine"
)

// `up` publishes a preview range beside the Host's port, on 0.0.0.0 like it, on both engines: on
// Podman the pod publishes it, on Docker control does. A port taken on this computer is skipped
// with one line and never stops the instance, and control is told the ports actually published in
// HARNESS_PUBLISHED_PREVIEW_PORTS.

const defaultPreview = "-p 0.0.0.0:28081:28081 -p 0.0.0.0:28082:28082 -p 0.0.0.0:28083:28083 -p 0.0.0.0:28084:28084 -p 0.0.0.0:28085:28085 -p 0.0.0.0:28086:28086 -p 0.0.0.0:28087:28087 -p 0.0.0.0:28088:28088 -p 0.0.0.0:28089:28089 -p 0.0.0.0:28090:28090"

// previewUp runs a first `up` on a fresh machine with the engine named, the config's own lines
// in saved and the ports in taken held by something else. It answers the line that publishes
// (Podman's pod create, Docker's control run), control's run, and the output.
func previewUp(t *testing.T, program, saved string, taken ...int) (publishing, control, out string) {
	t.Helper()
	s := engine.NewScripted()
	s.On(program+" container inspect", engine.Result{Stderr: "Error: No such container: yawble", ExitCode: 125})
	s.On(healthLine(program, "yawble-worker-1"), engine.Result{Stdout: "healthy\n"})
	if program == "podman" {
		s.On("podman volume exists", engine.Result{ExitCode: 1})
		s.On("podman pod exists", engine.Result{ExitCode: 1})
	} else {
		s.On("docker info", engine.Result{Stdout: "12884901888|10\n"})
		s.On("docker image inspect", engine.Result{})
	}
	deps := stubbed(s)
	deps.LookPath = lookPath(program)
	deps.PortFree = takenBy(taken...)
	deps.ConfigDir = t.TempDir()
	writeConfig(t, deps.ConfigDir, "memory = \"8g\"\ncpus = 4\ngithubAsked = true\n"+saved)
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("%s: exit %d: %s %s", program, code, out, errOut)
	}
	if !strings.Contains(out, "Yawble is up at") {
		t.Errorf("%s: the instance did not start: %q", program, out)
	}
	runs := callsContaining(s, program+" run -d --name yawble ")
	if len(runs) != 1 {
		t.Fatalf("%s: control runs %q:\n%s", program, runs, calls(s))
	}
	control = runs[0]
	publishing = control
	if program == "podman" {
		creates := callsContaining(s, "podman pod create")
		if len(creates) != 1 {
			t.Fatalf("pod creates %q:\n%s", creates, calls(s))
		}
		publishing = creates[0]
	}
	return publishing, control, out
}

var publishFlag = regexp.MustCompile(`-p 0\.0\.0\.0:(\d+):(\d+)`)

// previewPublished is every port a line publishes to the same port inside, but the Host's.
func previewPublished(line string) []string {
	var ports []string
	for _, m := range publishFlag.FindAllStringSubmatch(line, -1) {
		if m[1] == m[2] && m[1] != "8080" {
			ports = append(ports, m[1])
		}
	}
	return ports
}

// publishedVariable is the value of HARNESS_PUBLISHED_PREVIEW_PORTS on control's run, and whether it is there.
func publishedVariable(control string) (string, bool) {
	m := regexp.MustCompile(` -e HARNESS_PUBLISHED_PREVIEW_PORTS=(\S*) `).FindStringSubmatch(control)
	if m == nil {
		return "", false
	}
	return m[1], true
}

func TestUpPublishesTheDefaultPreviewRangeUnderPodmanAndDockerWithNoSetting(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		publishing, control, _ := previewUp(t, program, "")
		if !strings.Contains(publishing, "-p 0.0.0.0:8080:8080 "+defaultPreview+" ") && !strings.HasSuffix(publishing, "-p 0.0.0.0:8080:8080 "+defaultPreview) {
			t.Errorf("%s does not publish the default range beside the Host's port on 0.0.0.0:\n%s", program, publishing)
		}
		if got, _ := publishedVariable(control); got != "28081,28082,28083,28084,28085,28086,28087,28088,28089,28090" {
			t.Errorf("%s: control is told %q", program, got)
		}
	}
}

func TestAChangedPreviewRangeIsPublished(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		publishing, control, _ := previewUp(t, program, "previewPorts = \"9001-9003,9010\"\n")
		if got := strings.Join(previewPublished(publishing), ","); got != "9001,9002,9003,9010" {
			t.Errorf("%s publishes %q:\n%s", program, got, publishing)
		}
		if got, _ := publishedVariable(control); got != "9001,9002,9003,9010" {
			t.Errorf("%s: control is told %q", program, got)
		}
	}
}

func TestAnEmptyPreviewPortsSettingPublishesNone(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		publishing, control, _ := previewUp(t, program, "previewPorts = \"\"\n")
		if got := previewPublished(publishing); len(got) != 0 || !strings.Contains(publishing, "-p 0.0.0.0:8080:8080") {
			t.Errorf("%s publishes preview ports %q with previews off:\n%s", program, got, publishing)
		}
		if _, there := publishedVariable(control); there {
			t.Errorf("%s: control is told of preview ports with previews off:\n%s", program, control)
		}
	}
}

func TestATakenPreviewPortIsSkippedWithItsLineAndTheInstanceStarts(t *testing.T) {
	const line = "preview port 28083 is already in use on this computer; skipped (the instance starts without it)\n"
	for _, program := range []string{"podman", "docker"} {
		publishing, control, out := previewUp(t, program, "", 28083)
		if strings.Count(out, line) != 1 || strings.Count(out, "already in use") != 1 {
			t.Errorf("%s: not one line for the taken port: %q", program, out)
		}
		if strings.Contains(publishing, "28083") || len(previewPublished(publishing)) != 9 {
			t.Errorf("%s publishes the taken port, or not the others:\n%s", program, publishing)
		}
		if got, _ := publishedVariable(control); strings.Contains(got, "28083") {
			t.Errorf("%s: control is told of the taken port: %q", program, got)
		}
	}
}

func TestThePublishedPreviewPortsVariableCarriesExactlyThePublishedPorts(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		publishing, control, _ := previewUp(t, program, "", 28081, 28085, 28090)
		got, there := publishedVariable(control)
		if !there || got != "28082,28083,28084,28086,28087,28088,28089" {
			t.Errorf("%s: HARNESS_PUBLISHED_PREVIEW_PORTS=%q", program, got)
		}
		if published := strings.Join(previewPublished(publishing), ","); published != got {
			t.Errorf("%s publishes %q but tells control %q", program, published, got)
		}
	}
	// Every port taken: none published, and the variable is absent.
	_, control, _ := previewUp(t, "podman", "previewPorts = \"9001-9002\"\n", 9001, 9002)
	if _, there := publishedVariable(control); there {
		t.Errorf("control is told of preview ports though none was published:\n%s", control)
	}
}

func TestConfigReadsTheDefaultPreviewRangeUntilSetAndRefusesOneThatIsNotARange(t *testing.T) {
	dir := t.TempDir()
	get := func() string {
		code, out, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "get", "previewPorts")
		if code != 0 {
			t.Fatalf("get: exit %d %s", code, errOut)
		}
		return strings.TrimSpace(out)
	}
	if got := get(); got != "28081-28090" {
		t.Errorf("unset reads %q", got)
	}
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "previewPorts", "9001-9005"); code != 0 {
		t.Fatalf("set: exit %d %s", code, errOut)
	}
	if got := get(); got != "9001-9005" {
		t.Errorf("set reads %q", got)
	}
	if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "previewPorts", ""); code != 0 {
		t.Fatalf("set empty: exit %d %s", code, errOut)
	}
	if got := get(); got != "" {
		t.Errorf("turned off reads %q", got)
	}
	for _, bad := range []string{"9005-9001", "abc", "0-10", "70000", "2000-20000"} {
		if code, _, errOut := run(t, cli.Deps{ConfigDir: dir}, "config", "set", "previewPorts", bad); code != 2 || !strings.Contains(errOut, "previewPorts") {
			t.Errorf("%q: exit %d %s", bad, code, errOut)
		}
	}
}

// `doctor` shows the configured range and the ports actually published, in its short list.
func TestDoctorShowsThePreviewRangeAndThePortsPublished(t *testing.T) {
	code, out, errOut := run(t, stubbed(runningScript()), "doctor")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "range 28081-28090; published 28081-28090") {
		t.Errorf("doctor does not show the preview range and what was published:\n%s", out)
	}
	code, out, _ = run(t, stubbed(runningScript()), "doctor", "--json")
	if v, detail, _ := verdict(t, parseDoctor(t, out), "preview"); code != 0 || v != "ok" || !strings.Contains(detail, "published 28081-28090") {
		t.Errorf("json preview row: %s %q", v, detail)
	}
}

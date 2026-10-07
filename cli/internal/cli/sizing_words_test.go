package cli_test

import (
	"regexp"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/cli"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// The first up's size screen is read by a person who is not a developer: how many agent runs can
// work at once is one plain sentence, and every place the first up names that number names the
// same one. No setting keys and no talk of the Host's rule.

// runCounts is every number the output gives for how many runs work at once.
func runCounts(out string) []string {
	var got []string
	for _, re := range []*regexp.Regexp{
		regexp.MustCompile(`Up to (\d+) agent runs? can work at once`),
		regexp.MustCompile(`running limit (\d+)`),
		regexp.MustCompile(`(\d+) at once`),
	} {
		for _, m := range re.FindAllStringSubmatch(out, -1) {
			got = append(got, m[1])
		}
	}
	return got
}

func developerWords(t *testing.T, out string) {
	t.Helper()
	for _, banned := range []string{"wip.memoryPerRunMb", "Host's rule", "Host's default", "yawble doctor names it", "key control and the workers share"} {
		if strings.Contains(out, banned) {
			t.Errorf("the first up says %q:\n%s", banned, out)
		}
	}
}

func TestTheSizeScreenSaysHowManyRunsInOneSentenceAndTheSameNumberEverywhere(t *testing.T) {
	_, deps := dockerUp("darwin")
	deps.ConfigDir = t.TempDir()
	deps.Interactive, deps.Stdin = true, strings.NewReader("\n\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	// 6144 MB at 2048 MB a run holds 3; 8 CPUs with one kept free allow 7: memory decides.
	sentence := "Up to 3 agent runs can work at once, because each run is given 2048 MB of memory and the worker has 6144 MB."
	if !strings.Contains(out, sentence) {
		t.Errorf("the screen lacks the sentence %q:\n%s", sentence, out)
	}
	if !strings.Contains(out, "running limit 3)") {
		t.Errorf("the started line does not repeat the number it just showed:\n%s", out)
	}
	counts := runCounts(out)
	if len(counts) < 2 {
		t.Fatalf("the number was said %d times, want the screen and the started line:\n%s", len(counts), out)
	}
	for _, n := range counts {
		if n != "3" {
			t.Errorf("the first up names %s runs at once beside 3:\n%s", n, out)
		}
	}
	developerWords(t, out)
}

func TestAChangedSizeIsSaidWithItsOwnNumberAndStartedWithIt(t *testing.T) {
	_, deps := dockerUp("windows")
	deps.ConfigDir = t.TempDir()
	deps.Interactive, deps.Stdin = true, strings.NewReader("8g\n4\n")
	code, out, errOut := run(t, deps, "up", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	// 4 CPUs with one kept free allow 3; 8192 MB holds 4: the CPUs decide.
	saved := "Up to 3 agent runs can work at once, because the worker has 4 CPUs and one is kept free for Yawble itself."
	if !strings.Contains(out, saved) || !strings.Contains(out, "running limit 3)") {
		t.Errorf("after the answers, the sentence %q and the started line should agree:\n%s", saved, out)
	}
	developerWords(t, out)
}

func TestUnattendedFirstUpSaysTheSameNumberItStartsWith(t *testing.T) {
	_, deps := dockerUp("darwin")
	deps.ConfigDir = t.TempDir()
	code, out, errOut := run(t, deps, "up", "--yes", "--no-browser")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if !strings.Contains(out, "Up to 3 agent runs can work at once") || !strings.Contains(out, "running limit 3)") {
		t.Errorf("--yes should say the number once and start with it:\n%s", out)
	}
	for _, n := range runCounts(out) {
		if n != "3" {
			t.Errorf("names %s beside 3:\n%s", n, out)
		}
	}
	developerWords(t, out)
}

func TestTheSizeScreenNamesTheEnginesShareOfThisComputerAndWhereToGiveItMore(t *testing.T) {
	for _, c := range []struct {
		name    string
		machine instance.Machine
		share   string
		more    string
	}{
		{"podman machine", instance.Machine{MemoryBytes: 11444 << 20, CPUs: 8, Measured: true, Source: "podman machine", Kind: instance.KindPodmanMachine},
			"11444 MB memory and 8 CPUs: the Podman machine's share of this computer, not all of it", "podman machine set --memory"},
		{"docker desktop", instance.Machine{MemoryBytes: 12288 << 20, CPUs: 10, Measured: true, Source: "docker info", Kind: instance.KindDockerDesktop},
			"12288 MB memory and 10 CPUs: Docker Desktop's share of this computer, not all of it", "Docker Desktop's Settings > Resources"},
		{"linux", instance.Machine{MemoryBytes: 16384 << 20, CPUs: 8, Measured: true, Source: "this computer", Kind: instance.KindLinux},
			"16384 MB memory and 8 CPUs: all of this computer", "there is nothing to enlarge"},
	} {
		memoryMB, cpus := instance.Proposed(c.machine)
		screen := cli.FirstUpScreen(c.machine, memoryMB, cpus)
		if !strings.Contains(screen, c.share) || !strings.Contains(screen, c.more) {
			t.Errorf("%s: the screen should say %q and %q:\n%s", c.name, c.share, c.more, screen)
		}
		if strings.Contains(screen, "the engine has") {
			t.Errorf("%s: \"the engine has\" does not say whose share it is:\n%s", c.name, screen)
		}
		developerWords(t, screen)
	}
}

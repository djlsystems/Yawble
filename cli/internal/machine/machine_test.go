package machine_test

import (
	"context"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/machine"
)

const inspectLine = "podman machine inspect --format {{.Name}}|{{.State}}|{{.Rootful}}|{{.Resources.Memory}}|{{.Resources.CPUs}}"

func TestLinuxHasNoMachineAndRunsNothing(t *testing.T) {
	s := engine.NewScripted()
	info, err := machine.Inspect(context.Background(), s, "linux")
	if err != nil || info.Applies || len(s.Calls) != 0 {
		t.Errorf("info %+v err %v calls %q", info, err, s.Calls)
	}
}

// Measured on Windows: inspect says 2048 MB, which WSL ignores; the VM's own free -m is the bound.
func TestWindowsReadsTheMeasuredLineAndTheRealMemoryFromWSL(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspectLine, engine.Result{Stdout: "podman-machine-default|running|false|2048|10\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "               total        used        free\nMem:           15688        1470       13039\nSwap:           4096           0        4095\n"})
	s.On("wsl --status", engine.Result{Stdout: "Default Distribution: podman-machine-default\n"})
	info, err := machine.Inspect(context.Background(), s, "windows")
	if err != nil {
		t.Fatal(err)
	}
	if !info.Applies || !info.Exists || !info.Running || info.Rootful || info.Name != "podman-machine-default" || info.CPUs != 10 {
		t.Errorf("info %+v", info)
	}
	if info.MemoryMB != 15688 || info.MemorySource != "wsl" {
		t.Errorf("memory %d from %q, want 15688 from wsl", info.MemoryMB, info.MemorySource)
	}
	if info.WSL == nil || !*info.WSL {
		t.Errorf("wsl %v", info.WSL)
	}
}

func TestMacUsesTheMachinesOwnMemoryAndAsksNothingOfWSL(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspectLine, engine.Result{Stdout: "podman-machine-default|running|true|8192|4\n"})
	info, err := machine.Inspect(context.Background(), s, "darwin")
	if err != nil {
		t.Fatal(err)
	}
	if !info.Rootful || info.MemoryMB != 8192 || info.MemorySource != "machine" || info.WSL != nil {
		t.Errorf("info %+v", info)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "wsl") {
			t.Errorf("ran %q on darwin", c)
		}
	}
}

func TestAStoppedWindowsMachineDoesNotAskWSLForMemory(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspectLine, engine.Result{Stdout: "podman-machine-default|stopped|false|2048|10\n"})
	s.On("wsl --status", engine.Result{})
	info, err := machine.Inspect(context.Background(), s, "windows")
	if err != nil || info.Running || info.MemoryMB != 0 || info.MemorySource != "" {
		t.Errorf("info %+v err %v", info, err)
	}
	for _, c := range s.Calls {
		if strings.HasPrefix(c, "wsl -d") {
			t.Errorf("asked a stopped VM for its memory: %q", c)
		}
	}
}

func TestAMissingMachineIsNotAnError(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman machine inspect", engine.Result{Stderr: "Error: podman-machine-default: VM does not exist", ExitCode: 125})
	info, err := machine.Inspect(context.Background(), s, "darwin")
	if err != nil || !info.Applies || info.Exists {
		t.Errorf("info %+v err %v", info, err)
	}
}

// Review Focus 5: a broken podman is not "create the machine".
func TestABrokenInspectIsAnErrorCarryingPodmansSentence(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman machine inspect", engine.Result{Stderr: "Error: cannot read machine config: permission denied", ExitCode: 125})
	_, err := machine.Inspect(context.Background(), s, "windows")
	if err == nil || !strings.Contains(err.Error(), "permission denied") {
		t.Errorf("err %v", err)
	}
}

func TestWSLMissingIsMeasuredAsFalse(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspectLine, engine.Result{Stderr: "VM does not exist", ExitCode: 125})
	s.On("wsl --status", engine.Result{Stderr: "not recognized", ExitCode: 1})
	info, _ := machine.Inspect(context.Background(), s, "windows")
	if info.WSL == nil || *info.WSL {
		t.Errorf("wsl %v", info.WSL)
	}
}

func TestTheDrivingCommandsAreExactly(t *testing.T) {
	s := engine.NewScripted()
	ctx := context.Background()
	_ = machine.Init(ctx, s, 8192, 4)
	_ = machine.Init(ctx, s, 0, 0)
	_ = machine.Start(ctx, s)
	_ = machine.Stop(ctx, s)
	_ = machine.SetRootless(ctx, s)
	want := []string{
		"podman machine init --rootful=false --memory 8192 --cpus 4",
		"podman machine init --rootful=false",
		"podman machine start",
		"podman machine stop",
		"podman machine set --rootful=false",
	}
	if strings.Join(s.Calls, "\n") != strings.Join(want, "\n") {
		t.Errorf("calls:\n%s\nwant:\n%s", strings.Join(s.Calls, "\n"), strings.Join(want, "\n"))
	}
}

func TestAFailedStartCarriesPodmansSentence(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman machine start", engine.Result{Stderr: "Error: unable to start: WSL is not installed", ExitCode: 125})
	err := machine.Start(context.Background(), s)
	if err == nil || !strings.Contains(err.Error(), "WSL is not installed") {
		t.Errorf("err %v", err)
	}
}

// The computer's own RAM, which bounds what the machine may be given: CIM on Windows, sysctl on
// macOS, nothing on Linux (there is no machine to size), 0 for anything that cannot be read.
func TestHostMemoryIsTheComputersRAM(t *testing.T) {
	ctx := context.Background()
	s := engine.NewScripted()
	s.On("powershell", engine.Result{Stdout: "12884901888\r\n"}) // 12 GB
	if mb := machine.HostMemoryMB(ctx, s, "windows"); mb != 12288 {
		t.Errorf("windows: %d MB, calls %q", mb, s.Calls)
	}
	if c := strings.Join(s.Calls, "\n"); !strings.Contains(c, "Win32_ComputerSystem") || !strings.Contains(c, "TotalPhysicalMemory") {
		t.Errorf("windows asks CIM: %q", c)
	}
	s = engine.NewScripted()
	s.On("sysctl -n hw.memsize", engine.Result{Stdout: "68719476736\n"})
	if mb := machine.HostMemoryMB(ctx, s, "darwin"); mb != 65536 {
		t.Errorf("darwin: %d MB", mb)
	}
	s = engine.NewScripted()
	if mb := machine.HostMemoryMB(ctx, s, "linux"); mb != 0 || len(s.Calls) != 0 {
		t.Errorf("linux: %d MB, calls %q", mb, s.Calls)
	}
	s = engine.NewScripted()
	s.On("powershell", engine.Result{Stderr: "denied", ExitCode: 1})
	if mb := machine.HostMemoryMB(ctx, s, "windows"); mb != 0 {
		t.Errorf("unreadable: %d MB", mb)
	}
}

// On Windows `podman machine inspect` can report fewer CPUs than the computer has (2 on a
// 4-processor VM), a figure WSL ignores exactly as it ignores the memory one; trusting it would
// cap the container and the running limit too low. The machine's own nproc is the real count.
func TestWindowsReadsTheRealCPUCountFromWSL(t *testing.T) {
	s := engine.NewScripted()
	s.On(inspectLine, engine.Result{Stdout: "podman-machine-default|running|false|2048|2\n"})
	s.On("wsl -d podman-machine-default -e free -m", engine.Result{Stdout: "Mem: 5924 1 1\n"})
	s.On("wsl -d podman-machine-default -e nproc", engine.Result{Stdout: "4\n"})
	info, err := machine.Inspect(context.Background(), s, "windows")
	if err != nil || info.CPUs != 4 {
		t.Errorf("cpus %d err %v, want 4 from nproc", info.CPUs, err)
	}
	s = engine.NewScripted()
	s.On(inspectLine, engine.Result{Stdout: "podman-machine-default|running|false|8192|6\n"})
	if info, _ := machine.Inspect(context.Background(), s, "darwin"); info.CPUs != 6 || strings.Contains(strings.Join(s.Calls, "\n"), "nproc") {
		t.Errorf("macOS keeps the machine's own setting: %d, calls %q", info.CPUs, s.Calls)
	}
}

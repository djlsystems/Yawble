package doctor

import "testing"

// The "container got" words say a CPU count is the container's own only when its cgroup said it;
// the engine's figure and the processors seen are named as the running limit's count instead.
func TestContainerGotNamesWhereTheCpuCountCameFrom(t *testing.T) {
	cpus, mb := 14, int64(4096)
	for _, c := range []struct {
		source, bound string
		memory        *int64
		want          string
	}{
		{"cgroup", "cpu", &mb, "the container got 4096 MB, 14 CPUs (the Host's cgroup reading)"},
		{"", "cpu", nil, "the container got no memory limit, 14 CPUs (the Host's cgroup reading)"},
		{"engine", "cpu", nil, "the container got no memory limit and no CPU limit its cgroup says (the running limit counts 14 CPUs reported by the engine)"},
		{"processors", "cpu", &mb, "the container got 4096 MB and no CPU limit its cgroup says (the running limit counts 14 processors the Host sees)"},
		{"processors", "workers", nil, "the container got no memory limit and no CPU limit its cgroup says (the running limit counts 14 processors the worker sees)"},
	} {
		r := &HostReport{Wip: &HostWip{Limit: &WipLimit{Cpus: &cpus, MemoryLimitMb: c.memory, CpusSource: c.source, Bound: c.bound}}}
		if got := containerGot(r); got != c.want {
			t.Errorf("%s/%s:\n got %q\nwant %q", c.source, c.bound, got, c.want)
		}
	}
}

package engine

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"strconv"
	"strings"
)

// Stats is the engine's own one-shot reading of a container's resource use: the view from
// outside, which still answers when the Host inside cannot. Every figure is what the engine
// printed; a nil pointer or an empty string is a figure the engine did not report, which is
// "not measured", never zero.
type Stats struct {
	// Command is the command line the figures came from, without the container: "podman stats"
	// or "docker stats". Output names it so a person knows whose numbers these are.
	Command string `json:"command"`
	// NotRunning is the engine saying the container is not running (or not there): no figures.
	NotRunning bool `json:"notRunning,omitempty"`
	// CPUPercent is of one CPU, as both engines print it: 250% is two and a half CPUs busy.
	CPUPercent *float64 `json:"cpuPercent"`
	// MemoryUsage and MemoryLimit are as the engine printed them ("1.2GB", "512MiB"); the Bytes
	// fields are the same read as bytes, nil when the text could not be read.
	MemoryUsage      string   `json:"memoryUsage,omitempty"`
	MemoryLimit      string   `json:"memoryLimit,omitempty"`
	MemoryUsageBytes *int64   `json:"memoryUsageBytes"`
	MemoryLimitBytes *int64   `json:"memoryLimitBytes"`
	MemoryPercent    *float64 `json:"memoryPercent"`
	PIDs             *int     `json:"pids"`
	NetIO            string   `json:"netIO,omitempty"`
	BlockIO          string   `json:"blockIO,omitempty"`
}

// Summary is the figures on one line, each one that was not reported said so:
// "cpu 12.3%  memory 1.2GB / 12.88GB (9%)  pids 412". The caller adds where they came from.
func (s Stats) Summary() string {
	if s.NotRunning {
		return "the container is not running"
	}
	parts := make([]string, 0, 3)
	if s.CPUPercent != nil {
		parts = append(parts, fmt.Sprintf("cpu %.1f%%", *s.CPUPercent))
	} else {
		parts = append(parts, "cpu not measured")
	}
	memory := "memory "
	switch {
	case s.MemoryUsage != "" && s.MemoryLimit != "":
		memory += s.MemoryUsage + " / " + s.MemoryLimit
	case s.MemoryUsage != "":
		memory += s.MemoryUsage
	case s.MemoryPercent == nil:
		memory += "not measured"
	}
	if s.MemoryPercent != nil {
		if memory != "memory " {
			memory += " "
		}
		memory += fmt.Sprintf("(%.0f%%)", *s.MemoryPercent)
	}
	parts = append(parts, memory)
	if s.PIDs != nil {
		parts = append(parts, fmt.Sprintf("pids %d", *s.PIDs))
	} else {
		parts = append(parts, "pids not measured")
	}
	return strings.Join(parts, "  ")
}

// statsArgs is the command line each engine is asked, the container name last. Podman prints a
// JSON array of objects with snake_case keys; Docker one JSON object per line with its template
// names. parseStats reads both, and the other engine's keys too, since versions have differed.
func statsArgs(program, name string) []string {
	if program == "docker" {
		return []string{"stats", "--no-stream", "--format", "{{json .}}", name}
	}
	return []string{"stats", "--no-stream", "--format", "json", name}
}

// stats runs one engine's stats and reads the answer. A container the engine says is not
// running or not there is NotRunning, not an error: there is nothing to measure, and that is
// what the caller says. Any other failure is an error carrying the engine's own sentence.
func stats(ctx context.Context, r Runner, program, name string) (Stats, error) {
	st := Stats{Command: program + " stats"}
	res, err := r.Run(ctx, program, statsArgs(program, name)...)
	if err != nil {
		return st, &NotRunnable{Err: err}
	}
	if res.ExitCode != 0 {
		lower := strings.ToLower(res.Stderr)
		if strings.Contains(lower, "not running") || strings.Contains(lower, "no such") || strings.Contains(lower, "not found") {
			st.NotRunning = true
			return st, nil
		}
		return st, fmt.Errorf("%s stats %s: %s (exit %d)", program, name, strings.TrimSpace(res.Stderr), res.ExitCode)
	}
	return parseStats(st, res.Stdout)
}

// parseStats reads the first object of a JSON array, or the first line that is a JSON object.
// Values may be strings ("1.23%", "412") or numbers, depending on the engine's version.
func parseStats(st Stats, stdout string) (Stats, error) {
	fields, err := firstStatsObject(stdout)
	if err != nil {
		return st, fmt.Errorf("%s answered something that is not its JSON: %w", st.Command, err)
	}
	if fields == nil {
		// Podman answers an empty list for a container that exists but is not running.
		st.NotRunning = true
		return st, nil
	}
	pick := func(keys ...string) string {
		for k, v := range fields {
			for _, want := range keys {
				if strings.EqualFold(k, want) {
					return valueText(v)
				}
			}
		}
		return ""
	}
	cpu := pick("cpu_percent", "CPUPerc", "cpu")
	memUsage := pick("mem_usage", "MemUsage")
	memPercent := pick("mem_percent", "MemPerc")
	pids := pick("pids", "PIDs")
	st.NetIO = measured(pick("net_io", "NetIO"))
	st.BlockIO = measured(pick("block_io", "BlockIO"))

	// Docker shows a stopped container as a row of placeholders, "--" or zeros with no limit:
	// a running container always has a memory limit, the machine's when no other.
	if allUnmeasured(cpu, memUsage, memPercent, pids) || (strings.ReplaceAll(memUsage, " ", "") == "0B/0B" && (pids == "0" || pids == "")) {
		return Stats{Command: st.Command, NotRunning: true}, nil
	}

	st.CPUPercent = percent(cpu)
	st.MemoryPercent = percent(memPercent)
	if memUsage = measured(memUsage); memUsage != "" {
		used, limit, hasLimit := strings.Cut(memUsage, "/")
		st.MemoryUsage = strings.TrimSpace(used)
		st.MemoryUsageBytes = bytesOf(st.MemoryUsage)
		if hasLimit {
			st.MemoryLimit = strings.TrimSpace(limit)
			st.MemoryLimitBytes = bytesOf(st.MemoryLimit)
		}
	}
	if n, err := strconv.Atoi(strings.TrimSpace(pids)); err == nil && measured(pids) != "" {
		st.PIDs = &n
	}
	return st, nil
}

// firstStatsObject is the first stats object in stdout, nil for an empty answer or empty list.
func firstStatsObject(stdout string) (map[string]any, error) {
	text := strings.TrimSpace(stdout)
	if text == "" {
		return nil, fmt.Errorf("an empty answer")
	}
	if strings.HasPrefix(text, "[") {
		var list []map[string]any
		if err := json.Unmarshal([]byte(text), &list); err != nil {
			return nil, err
		}
		if len(list) == 0 {
			return nil, nil
		}
		return list[0], nil
	}
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if !strings.HasPrefix(line, "{") {
			continue
		}
		var obj map[string]any
		if err := json.Unmarshal([]byte(line), &obj); err != nil {
			return nil, err
		}
		return obj, nil
	}
	return nil, fmt.Errorf("no JSON object in %q", firstLine(text))
}

func firstLine(s string) string {
	line, _, _ := strings.Cut(s, "\n")
	if len(line) > 80 {
		line = line[:80] + "..."
	}
	return line
}

func valueText(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case float64:
		return strconv.FormatFloat(x, 'f', -1, 64)
	case nil:
		return ""
	default:
		return fmt.Sprint(x)
	}
}

// measured is the text unless it is one of the engines' placeholders for no reading: "--",
// "-- / --", "N/A".
func measured(s string) string {
	s = strings.TrimSpace(s)
	if strings.Trim(s, "-/ ") == "" || strings.EqualFold(s, "n/a") {
		return ""
	}
	return s
}

func allUnmeasured(values ...string) bool {
	for _, v := range values {
		if measured(v) != "" {
			return false
		}
	}
	return true
}

// percent reads "12.34%" or 12.34; nil when absent or unreadable.
func percent(s string) *float64 {
	s = strings.TrimSuffix(measured(s), "%")
	if s == "" {
		return nil
	}
	f, err := strconv.ParseFloat(strings.TrimSpace(s), 64)
	if err != nil || math.IsNaN(f) || math.IsInf(f, 0) {
		return nil
	}
	return &f
}

// byteUnits are the suffixes both engines print: decimal (Podman, Docker's limits in kB, MB...)
// and binary (Docker's memory in MiB, GiB).
var byteUnits = []struct {
	suffix string
	factor float64
}{
	{"kib", 1 << 10}, {"mib", 1 << 20}, {"gib", 1 << 30}, {"tib", 1 << 40},
	{"kb", 1e3}, {"mb", 1e6}, {"gb", 1e9}, {"tb", 1e12},
	{"b", 1},
}

// bytesOf reads "1.2GB", "512MiB", "0B"; nil when unreadable.
func bytesOf(s string) *int64 {
	text := strings.ToLower(strings.ReplaceAll(measured(s), " ", ""))
	if text == "" {
		return nil
	}
	for _, u := range byteUnits {
		if num, ok := strings.CutSuffix(text, u.suffix); ok {
			f, err := strconv.ParseFloat(num, 64)
			if err != nil || f < 0 {
				return nil
			}
			n := int64(math.Round(f * u.factor))
			return &n
		}
	}
	return nil
}

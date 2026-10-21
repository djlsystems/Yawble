package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"sort"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// hostReport is .rescan-report.json, which PluginRescanRequests writes.
type hostReport struct {
	Request string `json:"request"`
	Plugins []struct {
		ID      string `json:"id"`
		Version string `json:"version"`
	} `json:"plugins"`
	Refused []struct {
		ID     string `json:"id"`
		Reason string `json:"reason"`
	} `json:"refused"`
	Members []struct {
		Plugin string `json:"plugin"`
		Team   string `json:"team"`
		Member string `json:"member"`
	} `json:"members"`
}

func (r hostReport) installed(id string) (string, bool) {
	for _, p := range r.Plugins {
		if p.ID == id {
			return p.Version, true
		}
	}
	return "", false
}

func (r hostReport) refusal(id string) (string, bool) {
	for _, p := range r.Refused {
		if p.ID == id {
			return p.Reason, true
		}
	}
	return "", false
}

func (r hostReport) hiredOn(id string) []string {
	var names []string
	for _, m := range r.Members {
		if m.Plugin == id {
			names = append(names, m.Team+"/"+m.Member)
		}
	}
	return names
}

// rescan asks the Host to read the plugins again and waits for the report that answers this
// request, so what is printed is the Host's verdict now and never an older one.
func rescan(ctx context.Context, e engine.Engine) (hostReport, error) {
	nonce := newNonce()
	// A rescan request left behind is harmless: a Host that finds it later rescans. So no withdraw.
	x := hostExchange{root: pluginsRoot, request: requestScript, report: reportScript, poll: rescanPoll}
	answer, err := x.ask(ctx, e, nonce, nonce, rescanWait)
	if errors.Is(err, errNoAnswer) {
		return hostReport{}, fmt.Errorf("the Host did not answer the plugin rescan within %s. An image from before `yawble plugin` does not; "+
			"the files are in place, and `yawble down` then `yawble up` loads them", rescanWait)
	}
	if err != nil {
		return hostReport{}, err
	}
	var r hostReport
	_ = json.Unmarshal([]byte(answer), &r)
	return r, nil
}

// layoutEntry is one plugin directory on the volume.
type layoutEntry struct {
	Active   string
	Versions []string
}

// inUse is the version the Host loads: `active`, or the only version when there is no `active`.
func (l layoutEntry) inUse() string {
	if l.Active == "" && len(l.Versions) == 1 {
		return l.Versions[0]
	}
	return l.Active
}

func readLayout(ctx context.Context, e engine.Engine) (map[string]layoutEntry, error) {
	res, err := e.Exec(ctx, instance.ContainerName, "sh", "-c", layoutScript, "sh", pluginsRoot)
	if err != nil {
		return nil, err
	}
	layout := map[string]layoutEntry{}
	for _, line := range strings.Split(strings.ReplaceAll(res.Stdout, "\r\n", "\n"), "\n") {
		fields := strings.Split(line, "\t")
		if len(fields) < 2 || fields[0] == "" {
			continue
		}
		versions := append([]string{}, fields[2:]...)
		sort.Strings(versions)
		layout[fields[0]] = layoutEntry{Active: fields[1], Versions: versions}
	}
	return layout, nil
}

// pluginRow is one line of `plugin list`.
type pluginRow struct {
	ID      string `json:"id"`
	Version string `json:"version"`
	Active  bool   `json:"active"`
	// State is installed or refused for the version in use, kept for the others, and refused for a
	// plugin directory the Host could not read a version from at all.
	State  string `json:"state"`
	Reason string `json:"reason,omitempty"`
}

func pluginRows(layout map[string]layoutEntry, report hostReport) []pluginRow {
	ids := make([]string, 0, len(layout))
	for id := range layout {
		ids = append(ids, id)
	}
	for _, r := range report.Refused {
		if _, ok := layout[r.ID]; !ok {
			ids = append(ids, r.ID)
		}
	}
	sort.Strings(ids)
	var rows []pluginRow
	for _, id := range ids {
		entry := layout[id]
		reason, refused := report.refusal(id)
		inUse := entry.inUse()
		if len(entry.Versions) == 0 || (refused && !slices.Contains(entry.Versions, inUse)) {
			rows = append(rows, pluginRow{ID: id, Version: "-", State: "refused", Reason: reason})
		}
		for _, v := range entry.Versions {
			row := pluginRow{ID: id, Version: v, Active: v == inUse, State: "kept"}
			if row.Active {
				row.State = "installed"
				if refused {
					row.State, row.Reason = "refused", reason
				} else if _, ok := report.installed(id); !ok {
					row.State = "not listed by the Host"
				}
			}
			rows = append(rows, row)
		}
	}
	if rows == nil {
		rows = []pluginRow{}
	}
	return rows
}

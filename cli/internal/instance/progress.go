package instance

import (
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"sync"
)

// progress is the log as a person waiting for a start sees it: whole lines, only those that say
// what the start is doing, indented under the waiting note. Until show() it holds them back.
type progress struct {
	mu      sync.Mutex
	out     io.Writer
	live    bool
	held    []string
	partial string
}

func (p *progress) Write(b []byte) (int, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	text := p.partial + string(b)
	lines := strings.Split(text, "\n")
	p.partial = lines[len(lines)-1]
	for _, line := range lines[:len(lines)-1] {
		p.add(line)
	}
	return len(b), nil
}

// add keeps one line if it means something; the caller holds the lock.
func (p *progress) add(line string) {
	msg := progressLine(line)
	if msg == "" {
		return
	}
	if p.live {
		fmt.Fprintln(p.out, "  "+msg)
	} else {
		p.held = append(p.held, msg)
	}
}

// show prints what was held and every later line as it comes.
func (p *progress) show() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.live = true
	for _, msg := range p.held {
		fmt.Fprintln(p.out, "  "+msg)
	}
	p.held = nil
}

// flush takes a last line that ended without a newline.
func (p *progress) flush() {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.partial != "" {
		p.add(p.partial)
		p.partial = ""
	}
}

// progressLine is the part of one log line worth showing, or "". The entrypoint's own lines
// ("volume: ...", "agent cli: ...") are kept whole; of the Host's JSON lines only its
// agent-launch decision is; apt's output and every request line are dropped.
func progressLine(line string) string {
	line = strings.TrimSpace(line)
	switch {
	case strings.HasPrefix(line, "volume:"), strings.HasPrefix(line, "agent cli:"):
		return line
	case strings.HasPrefix(line, "{"):
		var entry struct{ Category, Message string }
		if json.Unmarshal([]byte(line), &entry) == nil && entry.Category == "Harness.Host" && strings.HasPrefix(entry.Message, "Agent launch") {
			return entry.Message
		}
	}
	return ""
}

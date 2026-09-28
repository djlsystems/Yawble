// SAMPLE ECHO, GO: the Go template for a plugin member, protocol harness.member/1. It behaves
// exactly as samples/plugins/sample-echo (the .NET template) does, instruction for instruction.
//
// In:  ONE JSON request on stdin - { protocol, member, causation, work: [{ seq, instruction, ... }],
//
//	config: { mode }, secrets: { token? }, ... }.
//
// Out: JSON Lines on stdout - progress, then blocked / handback when asked, then its declared event
//
//	`done` (plugin.sample-echo-go.done, with the output's length), then exactly one result.
//
// Each instruction is transformed by `config.mode`: `upper` (the default) or `reverse`. Prefixes:
//
//	fail:<words>      the run fails with those words (result ok:false, exit 1)
//	block:<words>     that item is blocked with those words; the others still run
//	handback:<words>  the words are handed back, then transformed like any other
//	sleep:<seconds>   waits, to exercise Stop and the idle clock
//	publish:<type>    also publishes an event of that type - for trying what the Host refuses
//	quiet:<rest>      the run's result is marked quiet (wakes nobody on completion); <rest> is
//	                  then read as any other instruction, so `quiet:fail:x` is a quiet failure
//
// The same input always gives the same output.
//
// Self-contained: it uses the standard library only, and is built with CGO_ENABLED=0 into one static
// binary per processor (build.sh), so it needs nothing from the image.
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

type request struct {
	Work []struct {
		Instruction *string         `json:"instruction"`
		Payload     json.RawMessage `json:"payload"`
	} `json:"work"`
	Config struct {
		Mode *string `json:"mode"`
	} `json:"config"`
	Secrets struct {
		Token *string `json:"token"`
	} `json:"secrets"`
}

// Records are structs, not maps, so their keys come out in the order the .NET sample writes them.
type (
	progress struct {
		T      string `json:"t"`
		Status string `json:"status"`
	}
	blocked struct {
		T      string `json:"t"`
		Item   int    `json:"item"`
		Reason string `json:"reason"`
	}
	handback struct {
		T         string `json:"t"`
		Delivered string `json:"delivered"`
	}
	publish struct {
		T       string `json:"t"`
		Type    string `json:"type"`
		Payload struct {
			Length int `json:"length"`
		} `json:"payload"`
	}
	succeeded struct {
		T      string `json:"t"`
		OK     bool   `json:"ok"`
		Output string `json:"output"`
		Quiet  bool   `json:"quiet"`
	}
	failed struct {
		T     string `json:"t"`
		OK    bool   `json:"ok"`
		Error string `json:"error"`
		Quiet bool   `json:"quiet"`
	}
)

func main() { os.Exit(run(os.Stdin, os.Stdout)) }

func run(in io.Reader, out io.Writer) int {
	emit := func(record any) {
		line, _ := json.Marshal(record)
		_, _ = out.Write(append(line, '\n'))
	}

	var req request
	if raw, err := io.ReadAll(in); err == nil {
		_ = json.Unmarshal(raw, &req)
	}
	mode := "upper"
	if req.Config.Mode != nil {
		mode = *req.Config.Mode
	}

	emit(progress{"progress", fmt.Sprintf("transforming %d message(s) (%s)", len(req.Work), mode)})

	var results []string
	quiet := false

	for i, item := range req.Work {
		text := ""
		if item.Instruction != nil {
			text = *item.Instruction
		} else if len(item.Payload) > 0 {
			var compact bytes.Buffer
			if json.Compact(&compact, item.Payload) == nil {
				text = compact.String()
			}
		}

		if take(&text, "quiet:") {
			quiet = true
		}

		if take(&text, "fail:") {
			emit(failed{"result", false, "asked to fail: " + text, quiet})
			return 1
		}

		if take(&text, "block:") {
			emit(blocked{"blocked", i + 1, text})
			continue
		}

		if take(&text, "sleep:") {
			seconds, _ := strconv.ParseFloat(text, 64)
			time.Sleep(time.Duration(seconds * float64(time.Second)))
			text = "slept " + text + "s"
		}

		if take(&text, "publish:") {
			p := publish{T: "publish", Type: text}
			p.Payload.Length = utf8.RuneCountInString(text)
			emit(p)
			text = "published " + text
		}

		if take(&text, "handback:") {
			emit(handback{"handback", text})
		}

		if mode == "reverse" {
			results = append(results, reverse(text))
		} else {
			results = append(results, strings.ToUpper(text))
		}
	}

	if req.Secrets.Token != nil {
		results = append(results, fmt.Sprintf("token length %d", utf8.RuneCountInString(*req.Secrets.Token)))
	}

	output := strings.Join(results, "\n")
	done := publish{T: "publish", Type: "done"}
	done.Payload.Length = utf8.RuneCountInString(output)
	emit(done)
	emit(succeeded{"result", true, output, quiet})
	return 0
}

// take removes prefix from text and trims what is left, when text starts with it.
func take(text *string, prefix string) bool {
	if !strings.HasPrefix(*text, prefix) {
		return false
	}
	*text = strings.TrimSpace((*text)[len(prefix):])
	return true
}

func reverse(s string) string {
	runes := []rune(s)
	for i, j := 0, len(runes)-1; i < j; i, j = i+1, j-1 {
		runes[i], runes[j] = runes[j], runes[i]
	}
	return string(runes)
}

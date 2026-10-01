package cli_test

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

const agentsStdout = "{\"agents\":[" +
	"{\"agent\":\"claude\",\"installed\":true,\"version\":\"2.1.280\",\"authenticated\":true,\"detail\":\"saved login\",\"credentialVariable\":\"ANTHROPIC_API_KEY\"}," +
	"{\"agent\":\"codex\",\"installed\":true,\"version\":\"0.156.0\",\"authenticated\":false,\"detail\":\"The status command exited 1.\",\"credentialVariable\":\"OPENAI_API_KEY\"}," +
	"{\"agent\":\"copilot\",\"installed\":true,\"version\":null,\"authenticated\":null,\"detail\":\"no probe\",\"credentialVariable\":\"GH_TOKEN\"}," +
	"{\"agent\":\"agy\",\"installed\":false,\"version\":null,\"authenticated\":null,\"detail\":\"'agy' is not on PATH.\",\"credentialVariable\":\"GEMINI_API_KEY\"}," +
	"{\"agent\":\"newcomer\",\"installed\":true,\"version\":\"9.9\",\"authenticated\":true,\"detail\":\"via NEW_KEY\",\"credentialVariable\":\"NEW_KEY\"}" +
	"]}\n"

func TestAgentsRendersEveryAgentTheReportNamesWithHowToSignIn(t *testing.T) {
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: "prose\n" + agentsStdout})
	code, out, errOut := run(t, stubbed(s), "agents")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	for _, want := range []string{
		"claude", "signed in   yes",
		"codex", "signed in   NO", "Concierge on codex", "OPENAI_API_KEY",
		"copilot", "signed in   not measured",
		"agy", "installed   no",
		// Review Focus 4: an agent the CLI has never heard of is listed like the others.
		"newcomer", "9.9",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
}

func TestAgentsJSONCarriesTheHint(t *testing.T) {
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: agentsStdout})
	code, out, _ := run(t, stubbed(s), "agents", "--json")
	if code != 0 {
		t.Fatalf("exit %d: %s", code, out)
	}
	var got []struct {
		Agent string `json:"agent"`
		Hint  string `json:"hint"`
	}
	if err := json.Unmarshal([]byte(out), &got); err != nil {
		t.Fatalf("not json: %v\n%s", err, out)
	}
	if len(got) != 5 || got[1].Agent != "codex" || !strings.Contains(got[1].Hint, "OPENAI_API_KEY") || got[0].Hint != "" {
		t.Errorf("got %+v", got)
	}
}

func TestAgentsOnAStoppedInstanceExitsOneAndSaysUpFirst(t *testing.T) {
	s := engine.NewScripted()
	s.On("podman version", engine.Result{Stdout: "6.0.2\n"})
	s.On("podman container inspect", engine.Result{Stdout: "exited|" + testImage + "|" + currentLabel() + "\n"})
	code, _, errOut := run(t, stubbed(s), "agents")
	if code != 1 || !strings.Contains(errOut, "yawble up") {
		t.Errorf("exit %d stderr %q", code, errOut)
	}
}

// Step 10 (D): `yawble agents` lists agents only. An entry the Host marks as a plugin or as running
// no model is not listed, in text or JSON.
func TestAgentsListsAgentsOnly(t *testing.T) {
	others := strings.Replace(agentsStdout, "]}\n",
		",{\"agent\":\"plugin:sample-echo\",\"kind\":\"plugin\",\"installed\":true,\"version\":null,\"authenticated\":false,\"detail\":\"\"}"+
			",{\"agent\":\"echo\",\"kind\":\"agent\",\"languageModel\":false,\"installed\":true,\"version\":null,\"authenticated\":false,\"detail\":\"\"}]}\n", 1)
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: others})
	code, out, errOut := run(t, stubbed(s), "agents")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Contains(out, "sample-echo") || strings.Contains(out, "\necho\n") {
		t.Errorf("a plugin or a non-model preset is listed as an agent:\n%s", out)
	}

	s = runningScript()
	s.On(doctorExec, engine.Result{Stdout: others})
	_, out, _ = run(t, stubbed(s), "agents", "--json")
	var got []struct {
		Agent string `json:"agent"`
	}
	if err := json.Unmarshal([]byte(out), &got); err != nil {
		t.Fatalf("not json: %v\n%s", err, out)
	}
	if len(got) != 5 {
		t.Errorf("want the five agents, got %+v", got)
	}
}

// B002N: each agent's launch check is shown beside "signed in": ok, failed with its exit code and
// stderr tail, not checked as such, and absent (an older Host) as not known, never ok.
func TestAgentsShowsEachLaunchBesideTheSignIn(t *testing.T) {
	launches := strings.NewReplacer(
		`"credentialVariable":"ANTHROPIC_API_KEY"}`, `"credentialVariable":"ANTHROPIC_API_KEY","launch":{"result":"ok","exitCode":0,"stderrTail":null,"detail":null}}`,
		`"credentialVariable":"OPENAI_API_KEY"}`, `"credentialVariable":"OPENAI_API_KEY","launch":{"result":"failed","exitCode":137,"stderrTail":"fatal: out of memory\nAborted","detail":"started as agent under prlimit --data"}}`,
		`"credentialVariable":"GH_TOKEN"}`, `"credentialVariable":"GH_TOKEN","launch":{"result":"not checked","exitCode":null,"stderrTail":null,"detail":"the preset declares no free invocation"}}`,
	).Replace(agentsStdout)
	s := runningScript()
	s.On(doctorExec, engine.Result{Stdout: launches})
	code, out, errOut := run(t, stubbed(s), "agents")
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	blocks := map[string][]string{}
	for _, block := range strings.Split(out, "\n\n") {
		name, _, _ := strings.Cut(block, "\n")
		blocks[name] = strings.Split(block, "\n")
	}
	want := map[string][]string{
		"claude":   {"  launch      ok"},
		"codex":    {"  launch      FAILED, exit 137", "  launch why  started as agent under prlimit --data", "  stderr      fatal: out of memory", "  stderr      Aborted"},
		"copilot":  {"  launch      not checked", "  launch why  the preset declares no free invocation"},
		"newcomer": {"  launch      not known"},
	}
	for agent, lines := range want {
		block := strings.Join(blocks[agent], "\n")
		for _, line := range lines {
			if !strings.Contains(block, line+"\n") && !strings.HasSuffix(block, line) {
				t.Errorf("%s lacks %q:\n%s", agent, line, block)
			}
		}
	}
	if strings.Contains(strings.Join(blocks["agy"], "\n"), "launch") {
		t.Errorf("an agent that is not installed has no launch line:\n%s", out)
	}
}

// The doctor's agents row carries the launch beside the sign-in, and a failed launch warns.
func TestDoctorShowsEachLaunchBesideTheSignIn(t *testing.T) {
	signedIn := strings.Replace(doctorStdout, `"authenticated":false,"detail":"exit 1"`, `"authenticated":true,"detail":"ok"`, 1)
	for _, c := range []struct{ codex, want, verdict string }{
		{`"launch":{"result":"ok","exitCode":0,"stderrTail":null,"detail":null}`, "codex signed in, launch ok", "ok"},
		{`"launch":{"result":"failed","exitCode":134,"stderrTail":"Aborted","detail":null}`, "codex signed in, launch FAILED, exit 134", "warn"},
		{`"launch":{"result":"not checked","exitCode":null,"stderrTail":null,"detail":"no free invocation"}`, "codex signed in, launch not checked", "ok"},
		{`"launch":null`, "codex signed in, launch not known", "ok"},
	} {
		stdout := strings.Replace(signedIn, `"credentialVariable":"OPENAI_API_KEY"}`, `"credentialVariable":"OPENAI_API_KEY",`+c.codex+`}`, 1)
		stdout = strings.Replace(stdout, `"credentialVariable":"ANTHROPIC_API_KEY"}`, `"credentialVariable":"ANTHROPIC_API_KEY","launch":{"result":"ok","exitCode":0,"stderrTail":null,"detail":null}}`, 1)
		s := runningScript()
		s.On(doctorExec, engine.Result{Stdout: stdout})
		_, out, _ := run(t, stubbed(s), "doctor", "--json")
		v, detail, fix := verdict(t, parseDoctor(t, out), "agents")
		if v != c.verdict || detail != "claude signed in, launch ok · "+c.want {
			t.Errorf("agents row %s %q, want %s %q", v, detail, c.verdict, c.want)
		}
		if v == "warn" && fix != "yawble agents" {
			t.Errorf("a failed launch should point at yawble agents, got %q", fix)
		}
	}
}

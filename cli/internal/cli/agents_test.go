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

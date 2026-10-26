package cli_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/djlsystems/yawble/cli/internal/engine"
)

// sqlServerReport is the last line a Host on SQL Server prints for --doctor: the provider, and no
// backups folder.
const sqlServerReport = "{\"at\":\"2026-10-24T10:00:00+00:00\",\"dataRoot\":{\"path\":\"/data\",\"writable\":true,\"freeBytes\":1000000000000}," +
	"\"database\":{\"path\":\"sql.example.net/yawble\",\"exists\":true,\"schema\":{\"applied\":[\"a\"],\"pending\":[],\"unknown\":[],\"accepted\":true},\"error\":null," +
	"\"provider\":\"sqlserver\",\"server\":\"sql.example.net\",\"name\":\"yawble\"},\"backups\":null,\"versionsRecordedAt\":null,\"agents\":[]}\n"

const sqlServerLine = "the database is on SQL Server and is not in this backup: its backups are the SQL Server's own (point-in-time restore)"

func doctorLine(program string) string {
	return program + " exec -e HARNESS_WORKER_KEY= yawble dotnet /app/Harness.Host.dll --doctor"
}

// A running instance whose doctor reports SQL Server: one line says the database is the server's
// own and not in the archive, the manifest records that rather than an unreadable schema, and no
// sqlite3 helper is run against a file that is not there.
func TestBackupOnSQLServerSaysTheDatabaseIsNotInTheArchive(t *testing.T) {
	for _, program := range []string{"podman", "docker"} {
		t.Run(program, func(t *testing.T) {
			s := backupScript(t, program, "running", "")
			s.On(doctorLine(program), engine.Result{Stdout: sqlServerReport})
			deps := backupDeps(t, s, program)
			path := filepath.Join(t.TempDir(), "b.tar.gz")
			code, out, errOut := run(t, deps, "backup", "--output", path)
			if code != 0 {
				t.Fatalf("exit %d: %s %s", code, out, errOut)
			}
			if strings.Count(out, sqlServerLine) != 1 {
				t.Errorf("want the SQL Server line once: %q", out)
			}
			if strings.Contains(calls(s), helper(program, "sqlite3", true)) {
				t.Errorf("sqlite3 was run on SQL Server:\n%s", calls(s))
			}
			m, _ := readArchive(t, path)
			if m["schemaNote"] != sqlServerLine || strings.Contains(stringOf(m["schemaNote"]), "could not be read") {
				t.Errorf("schemaNote %v", m["schemaNote"])
			}
			if steps, _ := json.Marshal(m["schemaSteps"]); string(steps) != `[]` {
				t.Errorf("schemaSteps %s", steps)
			}
		})
	}
}

// A stopped instance has no doctor to ask: the HARNESS_DATABASE yawble hands the container decides.
func TestBackupOfAStoppedSQLServerInstanceReadsTheSetting(t *testing.T) {
	s := backupScript(t, "podman", "exited", "")
	deps := backupDeps(t, s, "podman")
	if err := os.WriteFile(filepath.Join(deps.ConfigDir, "env"), []byte("HARNESS_DATABASE=sqlserver\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "b.tar.gz")
	code, out, errOut := run(t, deps, "backup", "--output", path)
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Count(out, sqlServerLine) != 1 || strings.Contains(calls(s), helper("podman", "sqlite3", true)) {
		t.Errorf("out %q\n%s", out, calls(s))
	}
}

// SQLITE AS IT ALWAYS WAS: a running SQLite instance (its doctor says nothing of SQL Server) reads
// its schema steps with sqlite3 and says nothing about SQL Server.
func TestBackupOnSQLiteIsUnchanged(t *testing.T) {
	s := backupScript(t, "podman", "running", "")
	s.On(doctorLine("podman"), engine.Result{Stdout: strings.Replace(sqlServerReport, `"provider":"sqlserver",`, "", 1)})
	deps := backupDeps(t, s, "podman")
	path := filepath.Join(t.TempDir(), "b.tar.gz")
	code, out, errOut := run(t, deps, "backup", "--output", path)
	if code != 0 {
		t.Fatalf("exit %d: %s %s", code, out, errOut)
	}
	if strings.Contains(out, "SQL Server") || !strings.Contains(calls(s), helper("podman", "sqlite3", true)) {
		t.Errorf("out %q\n%s", out, calls(s))
	}
	m, _ := readArchive(t, path)
	if steps, _ := json.Marshal(m["schemaSteps"]); string(steps) != `["0001-initial","0002-teams"]` || m["schemaNote"] != nil {
		t.Errorf("schemaSteps %s, schemaNote %v", steps, m["schemaNote"])
	}
}

func stringOf(v any) string { s, _ := v.(string); return s }

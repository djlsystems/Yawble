package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/backup"
	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// secretsNote is said once by every command that writes a backup.
const secretsNote = "The backup holds secrets (sign-in keys, agent logins, tokens in clones): it is readable by you only; keep it that way."

// schemaQuery lists the database's applied schema steps. immutable=1 opens it on the read-only
// mount without the -shm file a WAL database otherwise needs; the instance is stopped, so the
// Host has checkpointed.
var schemaQuery = []string{"-readonly", "file:/data/messages.db?immutable=1", "SELECT id FROM schema_migrations ORDER BY applied_at, id"}

func newBackupCommand(deps Deps) *cobra.Command {
	var output string
	var full, yes bool
	cmd := &cobra.Command{
		Use:   "backup",
		Short: "Write the instance's data to one archive on this computer, to restore here or elsewhere",
		Long: "backup writes everything on the data volume - accounts, teams, documents, git clones, the " +
			"database and agent logins - to one .tar.gz on this computer, by default " +
			"yawble-backup-<yyyyMMdd-HHmmss>.tar.gz in the current folder. It works the same on Podman " +
			"and Docker: a short-lived container from the instance's own image reads the volume read-only.\n\n" +
			"A running instance is stopped while the archive is written, so it is consistent, and started " +
			"again afterwards; when agents are running they are named and backup asks first (--yes answers). " +
			"A stopped instance stays stopped.\n\n" +
			"Left out by default: what the instance reinstalls by itself at its next start - the package " +
			"caches (npm, pip, Go, NuGet), the headless browser, and the Claude, Codex, Copilot and Grok " +
			"programs. Agent logins and settings are kept. --full keeps everything.\n\n" +
			"The file holds secrets and is written readable by you only. yawble's own settings (engine, " +
			"port, keys set with `yawble secret set`) are not in it. `yawble restore <file>` puts it back.",
		Example: "  yawble backup\n  yawble backup --output ~/yawble.tar.gz\n  yawble backup --full --yes",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			path := output
			if path == "" {
				path = backup.FileName(deps.Now())
			}
			if _, err := os.Lstat(path); err == nil {
				return UsageError{path + " already exists; choose another name with --output"}
			}
			// Before the engine is touched: the instance is not stopped for a file that cannot be written.
			if err := backup.CheckWritable(path); err != nil {
				return fmt.Errorf("the backup cannot be written to %s: %w; nothing was stopped or changed", path, err)
			}
			e, s, _, err := prepare(deps)
			if err != nil {
				return err
			}
			return runBackup(cmd.Context(), deps, e, s, path, full, yes, cmd.OutOrStdout())
		},
	}
	cmd.Flags().StringVarP(&output, "output", "o", "", "the file to write (default yawble-backup-<yyyyMMdd-HHmmss>.tar.gz here)")
	cmd.Flags().BoolVar(&full, "full", false, "keep everything, the caches and the reinstallable agent programs too")
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "stop running agents without asking")
	return cmd
}

// runBackup is `backup` once the engine is known: stop a running instance (asking first when
// agents run), write the archive, start it again.
func runBackup(ctx context.Context, deps Deps, e engine.Engine, s instance.Settings, path string, full, yes bool, out io.Writer) error {
	if ok, err := e.VolumeExists(ctx, instance.VolumeName); err != nil {
		return err
	} else if !ok {
		return fmt.Errorf("there is no volume %s on %s, so there is nothing to back up", instance.VolumeName, e.Name())
	}
	info, err := e.Inspect(ctx, instance.ContainerName)
	if err != nil {
		return err
	}
	image, err := helperImage(ctx, e, info, s)
	if err != nil {
		return err
	}
	running := info.State == engine.StateRunning
	if running {
		if agents := agentProcesses(listAgentProcesses(ctx, e)); len(agents) > 0 {
			fmt.Fprintf(out, "backup stops %s while it writes the archive, then starts it again: this stops %s\n", instance.ContainerName, strings.Join(agents, ", "))
			ok, err := confirm(deps, yes, out, "Stop them and back up?")
			if err != nil {
				return err
			}
			if !ok {
				fmt.Fprintln(out, "nothing was backed up; the instance was not touched")
				return nil
			}
		}
		fmt.Fprintf(out, "stopping %s so the backup is consistent; it is started again afterwards\n", instance.ContainerName)
		if err := e.Stop(ctx, instance.ContainerName); err != nil {
			return err
		}
	} else {
		fmt.Fprintf(out, "%s is not running; it stays stopped\n", instance.ContainerName)
	}

	_, werr := writeBackup(ctx, deps, e, image, path, full, out)
	if running {
		fmt.Fprintf(out, "starting %s again\n", instance.ContainerName)
		if err := e.Start(ctx, instance.ContainerName); err != nil {
			return errors.Join(werr, fmt.Errorf("%s could not be started again (`yawble up` starts it): %w", instance.ContainerName, err))
		}
		if err := instance.WaitHealthy(ctx, e, instance.URL(instance.PortOf(info.Label, s.Port)), healthChecker(deps.HTTP), out); err != nil {
			return errors.Join(werr, err)
		}
	}
	return werr
}

// helperImage is the image the helper container runs: the instance's own, already on this
// machine, so nothing is pulled. Without a container, the one this yawble runs, if present.
func helperImage(ctx context.Context, e engine.Engine, info engine.ContainerInfo, s instance.Settings) (string, error) {
	image := info.Image
	if info.State == engine.StateAbsent || image == "" {
		image = s.Image
	}
	if image == "" {
		return "", instance.ErrNoImage
	}
	present, err := e.ImagePresent(ctx, image)
	if err != nil {
		return "", err
	}
	if !present {
		return "", fmt.Errorf("the image %s is not on this computer, and backup reads the volume with it; `yawble up` pulls it", image)
	}
	return image, nil
}

// writeBackup reads the volume through a helper container into path and says what it wrote. The
// instance must not be running.
func writeBackup(ctx context.Context, deps Deps, e engine.Engine, image, path string, full bool, out io.Writer) (backup.Manifest, error) {
	m := backup.Manifest{
		YawbleVersion: buildinfo.Version,
		Image:         image,
		ImageVersion:  backup.ImageVersion(image),
		Engine:        e.Name(),
		Processor:     goarchOf(deps),
		CreatedAt:     deps.Now().UTC(),
		Full:          full,
	}
	if v, err := e.Version(ctx); err == nil {
		m.EngineVersion = v
	}
	if !full {
		m.Excluded = backup.DefaultExcludes
	}
	if names, err := config.SecretNames(deps.ConfigDir); err == nil {
		m.Providers = names
	}
	res, err := e.RunHelper(ctx, engine.HelperSpec{Image: image, Volume: instance.VolumeName, Target: "/data", ReadOnly: true, Entrypoint: "sqlite3", Args: schemaQuery})
	if err != nil {
		m.SchemaNote = "the database's schema steps could not be read: " + err.Error()
	} else {
		for _, line := range strings.Split(res.Stdout, "\n") {
			if line = strings.TrimSpace(line); line != "" {
				m.SchemaSteps = append(m.SchemaSteps, line)
			}
		}
	}
	m, err = backup.Write(path, m, func(w io.Writer) error {
		_, err := e.RunHelper(ctx, engine.HelperSpec{Image: image, Volume: instance.VolumeName, Target: "/data", ReadOnly: true, Entrypoint: "tar", Args: backup.TarArgs("/data", full), Stdout: w})
		return err
	})
	if err != nil {
		return m, fmt.Errorf("the backup was not written: %w", err)
	}
	backup.Remember(deps.ConfigDir, path, m.CreatedAt)
	left := "caches and agent programs a start reinstalls were left out (--full keeps them)"
	if full {
		left = "everything included"
	}
	fmt.Fprintf(out, "wrote %s: %d files, %s of data; %s\n", path, m.Bytes.Files, humanBytes(m.Bytes.Data), left)
	fmt.Fprintln(out, secretsNote)
	return m, nil
}

func newRestoreCommand(deps Deps) *cobra.Command {
	var replace, yes bool
	cmd := &cobra.Command{
		Use:   "restore <file>",
		Short: "Put a backup into this computer's instance, on whichever engine it now uses, and start it",
		Long: "restore puts a backup made by `yawble backup` into the instance on this computer and this " +
			"engine, whichever made it: Podman to Docker, Windows to macOS, one computer to another. " +
			"Then it starts the instance the way `yawble up` does and waits for it to answer.\n\n" +
			"Into an empty instance it asks nothing. When the volume already holds data it refuses unless " +
			"--replace, which asks you to type the word replace (or --replace --yes from a script), and " +
			"first writes a backup of the current volume beside the file being restored.\n\n" +
			"A backup from a newer Yawble than this yawble runs is refused: run `yawble update` first. An " +
			"older one is fine; the instance moves its database forward at start.\n\n" +
			"yawble's own settings are not in a backup: restore ends by naming the keys the backed-up " +
			"instance had set with `yawble secret set` (never their values), so you can set them again.",
		Example: "  yawble restore yawble-backup-20260928-101500.tar.gz\n  yawble restore old.tar.gz --replace\n  yawble restore old.tar.gz --replace --yes",
		Args:    cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runRestore(cmd, deps, args[0], replace, yes)
		},
	}
	cmd.Flags().BoolVar(&replace, "replace", false, "replace data already on the volume (asks for the word replace; a backup of it is written first)")
	cmd.Flags().BoolVarP(&yes, "yes", "y", false, "answer yes to every question, --replace's included")
	return cmd
}

func runRestore(cmd *cobra.Command, deps Deps, path string, replace, yes bool) error {
	ctx, out := cmd.Context(), cmd.OutOrStdout()
	m, err := backup.ReadManifest(path)
	if err != nil {
		return err
	}
	// Before the engine is touched: a backup this image cannot open is refused outright.
	c, err := loadConfig(deps)
	if err != nil {
		return err
	}
	pinned, _, err := settingsFor(deps, c, instance.Machine{})
	if err != nil {
		return err
	}
	if pinned.Image == "" {
		return instance.ErrNoImage
	}
	if err := checkVersion(m, pinned.Image, out); err != nil {
		return err
	}

	e, s, err := upReady(cmd, deps, yes)
	if err != nil {
		return err
	}
	if ok, err := e.ImagePresent(ctx, s.Image); err != nil {
		return err
	} else if !ok {
		fmt.Fprintf(out, "pulling %s\n", s.Image)
		if err := e.Pull(ctx, s.Image, out); err != nil {
			return fmt.Errorf("the pull of %s failed: %w", s.Image, err)
		}
	}
	if ok, err := e.VolumeExists(ctx, instance.VolumeName); err != nil {
		return err
	} else if !ok {
		if err := e.CreateVolume(ctx, instance.VolumeName); err != nil {
			return err
		}
		fmt.Fprintf(out, "created volume %s\n", instance.VolumeName)
	}
	onVolume := func(readOnly bool, entrypoint string, args ...string) engine.HelperSpec {
		return engine.HelperSpec{Image: s.Image, Volume: instance.VolumeName, Target: "/data", ReadOnly: readOnly, Entrypoint: entrypoint, Args: args}
	}
	res, err := e.RunHelper(ctx, onVolume(true, "find", "/data", "-mindepth", "1", "-maxdepth", "1"))
	if err != nil {
		return err
	}
	holdsData := strings.TrimSpace(res.Stdout) != ""
	info, err := e.Inspect(ctx, instance.ContainerName)
	if err != nil {
		return err
	}
	running := info.State == engine.StateRunning

	if holdsData {
		if !replace {
			return fmt.Errorf("the volume %s already holds an instance's data, and restore does not overwrite it by itself. To replace it with %s, run again with --replace: a backup of the current volume is written beside the file first", instance.VolumeName, path)
		}
		agents := []string{}
		if running {
			agents = agentProcesses(listAgentProcesses(ctx, e))
		}
		if len(agents) > 0 {
			fmt.Fprintf(out, "restore stops %s: this stops %s\n", instance.ContainerName, strings.Join(agents, ", "))
		}
		if !yes {
			if !deps.Interactive || deps.Stdin == nil {
				return UsageError{"--replace needs the word replace typed at a terminal, or --yes with --replace from a script; nothing was changed"}
			}
			fmt.Fprintf(out, "Type replace to replace everything on %s (teams, accounts, documents, agent logins) with %s: ", instance.VolumeName, path)
			line, _ := readLine(deps.Stdin)
			if strings.TrimSpace(line) != "replace" {
				fmt.Fprintln(out, "not 'replace': nothing was changed")
				return nil
			}
		}
	}
	safety := ""
	if holdsData {
		safety = filepath.Join(filepath.Dir(path), strings.TrimSuffix(backup.FileName(deps.Now()), ".tar.gz")+"-before-restore.tar.gz")
		if abs, err := filepath.Abs(safety); err == nil {
			safety = abs
		}
		if err := backup.CheckWritable(safety); err != nil {
			return fmt.Errorf("the backup of the current volume cannot be written to %s: %w; nothing was stopped or changed", safety, err)
		}
	}
	if running {
		if err := e.Stop(ctx, instance.ContainerName); err != nil {
			return err
		}
		fmt.Fprintf(out, "stopped %s\n", instance.ContainerName)
	}
	if holdsData {
		fmt.Fprintf(out, "writing a backup of the current volume first: %s\n", safety)
		if _, err := writeBackup(ctx, deps, e, s.Image, safety, false, out); err != nil {
			return fmt.Errorf("%w; nothing was replaced", err)
		}
		if _, err := e.RunHelper(ctx, onVolume(false, "find", "/data", "-mindepth", "1", "-maxdepth", "1", "-exec", "rm", "-rf", "{}", "+")); err != nil {
			return fmt.Errorf("clearing %s failed (%w); %s", instance.VolumeName, err, recoverHint(safety))
		}
	}

	pr, pw := io.Pipe()
	counted := make(chan error, 1)
	go func() {
		_, err := backup.Extract(path, pw)
		pw.CloseWithError(err)
		counted <- err
	}()
	extract := onVolume(false, "tar", "-C", "/data", "--numeric-owner", "-xpf", "-")
	extract.Stdin = pr
	_, runErr := e.RunHelper(ctx, extract)
	pr.CloseWithError(runErr)
	if err := errors.Join(<-counted, runErr); err != nil {
		if safety != "" {
			return fmt.Errorf("restoring %s into %s failed: %w\nThe instance's data on %s was cleared first, so it is now incomplete. %s", path, instance.VolumeName, err, instance.VolumeName, recoverHint(safety))
		}
		return fmt.Errorf("restoring %s into %s failed: %w", path, instance.VolumeName, err)
	}
	fmt.Fprintf(out, "restored %s into %s: %d files, %s of data, backed up %s on %s (%s)\n", path, instance.VolumeName,
		m.Bytes.Files, humanBytes(m.Bytes.Data), m.CreatedAt.Local().Format("2006-01-02 15:04"), m.Engine, m.Processor)

	if err := instance.Up(ctx, e, s, healthChecker(deps.HTTP), out); err != nil {
		return err
	}
	providersNote(deps, m, out)
	return nil
}

// recoverHint names the backup restore wrote of the volume before clearing it, and the command
// that puts it back.
func recoverHint(safety string) string {
	arg := safety
	if strings.ContainsAny(arg, " '\"$`\\") {
		arg = "'" + strings.ReplaceAll(arg, "'", `'\''`) + "'"
	}
	return fmt.Sprintf("The backup of what was on it before is %s; to put it back, run:\n  yawble restore %s --replace", safety, arg)
}

// checkVersion refuses a backup from a newer Yawble than image: its database may hold schema
// steps this image does not know, and the Host would refuse to open it.
func checkVersion(m backup.Manifest, image string, out io.Writer) error {
	have := backup.ImageVersion(image)
	newer, comparable := backup.Newer(m.ImageVersion, have)
	if !comparable {
		// A local build's tag: the Host itself refuses a database from a newer build at start.
		fmt.Fprintf(out, "note: the backup's Yawble %q and this image's %q cannot be compared; the instance checks the database itself when it starts\n", m.ImageVersion, have)
		return nil
	}
	if newer {
		return fmt.Errorf("this backup was made by Yawble %s, newer than the Yawble %s this yawble runs; its database may hold changes this version does not know. Run `yawble update`, then restore again", m.ImageVersion, have)
	}
	return nil
}

// providersNote names the keys the backed-up instance had set with `yawble secret set`: names
// only, and which of them this computer already has.
func providersNote(deps Deps, m backup.Manifest, out io.Writer) {
	if len(m.Providers) == 0 {
		fmt.Fprintln(out, "The backed-up instance had no keys set with `yawble secret set`.")
		return
	}
	here := map[string]bool{}
	if names, err := config.SecretNames(deps.ConfigDir); err == nil {
		for _, n := range names {
			here[n] = true
		}
	}
	fmt.Fprintln(out, "yawble's settings are not in a backup. The backed-up instance used these keys; set any you still need with `yawble secret set NAME`, then `yawble up`:")
	for _, name := range m.Providers {
		line := "  " + name
		if p := backup.ProviderOf(name); p != "" {
			line += "  " + p
		}
		if here[name] {
			line += "  (already set on this computer)"
		}
		fmt.Fprintln(out, line)
	}
}

// humanBytes is a size for a person: 1.2 GB, 340 MB, 12 KB.
func humanBytes(n int64) string {
	const unit = 1000
	if n < unit {
		return fmt.Sprintf("%d bytes", n)
	}
	div, exp := int64(unit), 0
	for m := n / unit; m >= unit; m /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f %cB", float64(n)/float64(div), "kMGTPE"[exp])
}

// goarchOf is the processor the commands act for: deps.GOARCH in tests, else this one.
func goarchOf(deps Deps) string {
	if deps.GOARCH != "" {
		return deps.GOARCH
	}
	return runtime.GOARCH
}

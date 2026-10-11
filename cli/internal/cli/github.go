package cli

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/github"
)

// readLine reads one line from r a byte at a time. A buffered reader made per question would
// swallow the next answer with this one when answers are piped; this takes exactly one line.
func readLine(r io.Reader) (string, error) {
	var b strings.Builder
	one := make([]byte, 1)
	for {
		n, err := r.Read(one)
		if n == 1 {
			if one[0] == '\n' {
				return b.String() + "\n", nil
			}
			b.WriteByte(one[0])
		}
		if err != nil {
			return b.String(), err
		}
	}
}

// askYesNo asks a yes/no question at the terminal; anything but y or yes is no.
func askYesNo(deps Deps, out io.Writer, question string) bool {
	fmt.Fprint(out, question, " [y/N] ")
	line, _ := readLine(deps.Stdin)
	answer := strings.ToLower(strings.TrimSpace(line))
	return answer == "y" || answer == "yes"
}

// offerGitHub is the first `up`'s question: will teams use GitHub, and if
// so the guided setup. Asked once, at a terminal, without --yes, while no GH_TOKEN is set; the
// answer is remembered either way. Errors saving the answer are not fatal to `up`.
func offerGitHub(ctx context.Context, deps Deps, c config.Config, yes bool, out io.Writer) {
	if yes || !deps.Interactive || deps.Stdin == nil || c.GitHubAsked {
		return
	}
	if _, set, _ := config.SecretValue(deps.ConfigDir, "GH_TOKEN"); set {
		return
	}
	if askYesNo(deps, out, "Will your teams work with GitHub repos (clone private repos, push branches, open pull requests)?") {
		_, _ = setUpGitHub(ctx, deps, out)
	}
	_ = markGitHubAsked(deps)
}

// setUpGitHub gets a GitHub token, checks it with GitHub and saves it as the GH_TOKEN secret: the
// GitHub CLI's login when `gh` is there and the person takes it, else a fine-grained token the
// person creates and pastes at a hidden prompt. Answers whether one was saved.
func setUpGitHub(ctx context.Context, deps Deps, out io.Writer) (bool, error) {
	api := deps.ReleaseBaseURL

	if deps.LookPath != nil {
		if _, err := deps.LookPath("gh"); err == nil {
			res, err := runnerOf(deps).Run(ctx, "gh", "auth", "token")
			if token := strings.TrimSpace(res.Stdout); err == nil && res.ExitCode == 0 && token != "" {
				if askYesNo(deps, out, "Use your GitHub CLI login (gh auth token)? Quick, but it reaches all your repositories and stops working when you log out of gh.") {
					if saved := saveCheckedToken(ctx, deps, api, token, out); saved {
						return true, nil
					}
				}
			}
		}
	}

	fmt.Fprintln(out, "Create a fine-grained token at https://github.com/settings/personal-access-tokens/new")
	fmt.Fprintln(out, "  Repository access: the repositories your teams will use")
	fmt.Fprintln(out, "  Permissions: Contents - Read and write; Pull requests - Read and write")
	fmt.Fprintln(out, "  (Quicker but broader: a classic token with the repo scope, https://github.com/settings/tokens/new?scopes=repo&description=Yawble)")
	fmt.Fprintln(out, "  Teams that contribute to a project they cannot push to: "+github.ContributorNeeds+",")
	fmt.Fprintln(out, "  https://github.com/settings/tokens/new?scopes=public_repo&description=Yawble")
	if deps.ReadSecret == nil {
		return false, UsageError{"cannot ask for the token here; pipe it in: <command> | yawble secret set GH_TOKEN"}
	}
	for try := 0; try < 3; try++ {
		token, err := deps.ReadSecret("Paste the token (shown as *; Enter to skip): ")
		if err != nil {
			return false, err
		}
		token = strings.TrimSpace(token)
		if token == "" {
			fmt.Fprintln(out, "Skipped. Later: yawble github (or yawble secret set GH_TOKEN), then yawble up.")
			return false, nil
		}
		if saveCheckedToken(ctx, deps, api, token, out) {
			return true, nil
		}
	}
	fmt.Fprintln(out, "No token saved. Later: yawble github, then yawble up.")
	return false, nil
}

// saveCheckedToken asks GitHub about token and saves it when GitHub accepts it, or when GitHub
// cannot be reached (said as such). A rejected token is said and not saved.
func saveCheckedToken(ctx context.Context, deps Deps, api, token string, out io.Writer) bool {
	answer := github.Check(ctx, deps.HTTP, api, token)
	switch {
	case answer.Accepted():
		fmt.Fprintf(out, "GitHub accepts it (as %s).\n", answer.Login)
		if !answer.ContributorReady() {
			fmt.Fprintf(out, "It is a %s token; %s. Owned repositories work with it.\n", answer.Kind, github.ContributorNeeds)
		}
	case answer.Unreachable:
		fmt.Fprintln(out, "Could not reach GitHub to check it; saving it anyway (yawble doctor checks it later).")
	default:
		fmt.Fprintf(out, "GitHub rejects this token (HTTP %d): revoked, expired or mistyped.\n", answer.Status)
		return false
	}
	if err := config.SetSecret(deps.ConfigDir, "GH_TOKEN", token); err != nil {
		fmt.Fprintln(out, "Could not save it:", err)
		return false
	}
	fmt.Fprintln(out, "Saved as the GH_TOKEN secret.")
	return true
}

// markGitHubAsked records that the question was asked, in the config file read without the
// environment so no YAWBLE_* override is written into it.
func markGitHubAsked(deps Deps) error {
	raw, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return err
	}
	raw.GitHubAsked = true
	_, err = config.Save(deps.ConfigDir, raw)
	return err
}

// gitHubHint is the one line `up` ends with while no GH_TOKEN is set.
func gitHubHint(deps Deps, out io.Writer) {
	if _, set, _ := config.SecretValue(deps.ConfigDir, "GH_TOKEN"); !set {
		fmt.Fprintln(out, "GitHub is not set up (only teams that use GitHub need it): yawble github, then yawble up")
	}
}

func newGitHubCommand(deps Deps) *cobra.Command {
	return &cobra.Command{
		Use:   "github",
		Short: "Set up the GitHub token teams use to clone, push and open pull requests",
		Long: "github walks through getting a GitHub token - your GitHub CLI login, or a fine-grained " +
			"token pasted at a hidden prompt - checks it with GitHub, and saves it as the GH_TOKEN " +
			"secret. Run yawble up afterwards to hand it to the instance.",
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			if !deps.Interactive {
				return UsageError{"yawble github asks at a terminal; without one: <command> | yawble secret set GH_TOKEN"}
			}
			saved, err := setUpGitHub(cmd.Context(), deps, cmd.OutOrStdout())
			_ = markGitHubAsked(deps)
			if err != nil {
				return err
			}
			if !saved {
				return errors.New("no GitHub token was saved")
			}
			fmt.Fprintln(cmd.OutOrStdout(), "Run yawble up to hand it to the instance.")
			return nil
		},
	}
}

package cli

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/spf13/cobra"

	"github.com/djlsystems/yawble/cli/internal/remote"
)

func newRemoteCommand(deps Deps) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "remote",
		Short: "Put the instance on the internet through a tunnel sidecar (cloudflare, tailscale, ngrok)",
		Long: "A tunnel runs as a second container beside the Host, makes only outbound connections, " +
			"and starts and stops with the instance. Nothing is installed on this machine and no router " +
			"port is opened. cloudflare: a quick tunnel with no account, or a named tunnel with a token. " +
			"tailscale: private to your tailnet (--funnel opens it). ngrok: your authtoken, optionally a " +
			"static domain. The credential lives in remote.toml beside yawble's config, owner-only, and " +
			"reaches the sidecar through an owner-only env file, never on a command line.",
	}
	cmd.AddCommand(newRemoteEnable(deps), newRemoteDisable(deps), newRemoteStatus(deps))
	return cmd
}

func newRemoteEnable(deps Deps) *cobra.Command {
	var token, domain string
	var funnel bool
	cmd := &cobra.Command{
		Use:     "enable <cloudflare|tailscale|ngrok>",
		Short:   "Start (or replace) the tunnel sidecar and print the public URL",
		Example: "  yawble remote enable cloudflare\n  yawble remote enable cloudflare --token <tunnel token>\n  yawble remote enable tailscale --token tskey-auth-...\n  yawble remote enable ngrok --token <authtoken> --domain my.ngrok.app",
		Args:    cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			p, err := remote.ProviderNamed(args[0])
			if err != nil {
				return UsageError{err.Error()}
			}
			saved, had, err := remote.Load(deps.ConfigDir)
			if err != nil {
				return err
			}
			cred := remote.Credential{Token: token, Domain: domain, Funnel: funnel}
			if had && saved.Provider == p.Name() {
				if cred.Token == "" {
					cred.Token = saved.Credential.Token
				}
				if cred.Domain == "" {
					cred.Domain = saved.Credential.Domain
				}
				if !cmd.Flags().Changed("funnel") {
					cred.Funnel = saved.Credential.Funnel
				}
			}
			if p.NeedsCredential() && cred.Token == "" {
				what := map[string]string{"tailscale": "a Tailscale auth key (tskey-auth-...)", "ngrok": "your ngrok authtoken"}[p.Name()]
				if !deps.Interactive || deps.Stdin == nil {
					return UsageError{fmt.Sprintf("%s needs %s: pass it with --token", p.Name(), what)}
				}
				fmt.Fprintf(cmd.OutOrStdout(), "%s needs %s. Paste it (it is not shown again): ", p.Name(), what)
				line, _ := readLine(deps.Stdin)
				cred.Token = strings.TrimSpace(line)
				if cred.Token == "" {
					return UsageError{"no token given"}
				}
			}
			e, _, _, err := prepare(deps)
			if err != nil {
				return err
			}
			out := cmd.OutOrStdout()
			url, err := remote.Enable(cmd.Context(), e, p, cred, deps.ConfigDir, out)
			if err != nil {
				return err
			}
			if err := remote.Save(deps.ConfigDir, remote.Config{Provider: p.Name(), Credential: cred, LastURL: url}); err != nil {
				return err
			}
			switch {
			case url != "":
				fmt.Fprintf(out, "Yawble is on the internet at %s\n", url)
			case p.Name() == "cloudflare":
				fmt.Fprintln(out, "the tunnel is up; its hostname is the one configured for this tunnel in the Cloudflare dashboard")
			}
			return nil
		},
	}
	cmd.Flags().StringVar(&token, "token", "", "the provider's credential (kept in remote.toml, owner-only)")
	cmd.Flags().StringVar(&domain, "domain", "", "ngrok: a static domain you own")
	cmd.Flags().BoolVar(&funnel, "funnel", false, "tailscale: open the node to the public internet (Funnel)")
	return cmd
}

func newRemoteDisable(deps Deps) *cobra.Command {
	var forget bool
	cmd := &cobra.Command{
		Use:     "disable",
		Short:   "Stop and remove the tunnel sidecar; --forget also deletes the saved credential",
		Example: "  yawble remote disable\n  yawble remote disable --forget",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			e, _, _, err := prepare(deps)
			if err != nil {
				return err
			}
			if err := remote.Disable(cmd.Context(), e, deps.ConfigDir, forget); err != nil {
				return err
			}
			if forget {
				fmt.Fprintln(cmd.OutOrStdout(), "tunnel removed and its credential forgotten")
			} else {
				fmt.Fprintln(cmd.OutOrStdout(), "tunnel removed; the credential is kept for the next `yawble remote enable`")
			}
			return nil
		},
	}
	cmd.Flags().BoolVar(&forget, "forget", false, "also delete remote.toml and the sidecar's env file")
	return cmd
}

func newRemoteStatus(deps Deps) *cobra.Command {
	var asJSON bool
	cmd := &cobra.Command{
		Use:     "status",
		Short:   "The tunnel's provider, whether its sidecar runs, and the public URL",
		Example: "  yawble remote status\n  yawble remote status --json",
		Args:    cobra.NoArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			out := cmd.OutOrStdout()
			c, had, err := remote.Load(deps.ConfigDir)
			if err != nil {
				return err
			}
			if !had {
				if asJSON {
					return json.NewEncoder(out).Encode(map[string]any{"provider": nil})
				}
				fmt.Fprintln(out, "remote access is not enabled (yawble remote enable <cloudflare|tailscale|ngrok>)")
				return nil
			}
			p, err := remote.ProviderNamed(c.Provider)
			if err != nil {
				return err
			}
			e, _, _, err := prepare(deps)
			if err != nil {
				return err
			}
			st, err := remote.Status(cmd.Context(), e, p, c.LastURL)
			if err != nil {
				return err
			}
			if asJSON {
				enc := json.NewEncoder(out)
				enc.SetIndent("", "  ")
				return enc.Encode(st)
			}
			fmt.Fprintf(out, "provider  %s\n", st.Provider)
			fmt.Fprintf(out, "sidecar   %s\n", st.State)
			switch {
			case st.URL != "" && st.URLFromRecord:
				fmt.Fprintf(out, "url       %s (as recorded when it was enabled; the sidecar's log no longer shows it)\n", st.URL)
			case st.URL != "":
				fmt.Fprintf(out, "url       %s\n", st.URL)
			case st.Provider == "cloudflare" && c.Credential.Token != "":
				fmt.Fprintln(out, "url       the hostname configured for this tunnel in the Cloudflare dashboard")
			}
			return nil
		},
	}
	cmd.Flags().BoolVar(&asJSON, "json", false, "print JSON")
	return cmd
}

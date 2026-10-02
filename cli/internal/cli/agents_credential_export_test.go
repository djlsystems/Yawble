package cli

import "time"

// FastCredential makes the agent credential commands wait briefly for the Host. The returned func
// restores the defaults.
func FastCredential(wait time.Duration) func() {
	w, poll := credentialWait, credentialPoll
	credentialWait, credentialPoll = wait, time.Millisecond
	return func() { credentialWait, credentialPoll = w, poll }
}

// The scripts the agent credential commands run in the container, so a scripted Host can answer each.
const (
	CredentialRequestScript  = credentialRequestScript
	CredentialReportScript   = credentialReportScript
	CredentialWithdrawScript = credentialWithdrawScript
	AgentCredentialsRoot     = agentCredentialsRoot
)

// CredentialWaitDefault is the wait the commands give the Host when no test shortens it.
func CredentialWaitDefault() time.Duration { return credentialWait }

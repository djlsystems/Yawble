import { describe, expect, it } from 'vitest';
import { asksForApproval, offersSkip, questionMarkdown, questionOptions } from '../../src/renderer/src/questionFormat';

describe('a team question, formatted', () => {
  it('puts inline numbered items in a table, one per row, with the text around it kept', () => {
    const q = 'Do you approve this plan and team? Plan, in order: (1) baseline: run the tests and fix the stale README; '
      + '(2) Cloud hardening: CI, reproducible deploy; (3) cost visibility: token caps. Team: two Developers and a Tester. '
      + 'Reply approve, or tell me what to change.';
    const md = questionMarkdown(q);
    expect(md.startsWith('Do you approve this plan and team? Plan, in order:')).toBe(true);
    expect(md).toContain('| # | Item | Details |');
    expect(md).toContain('| 1 | baseline | run the tests and fix the stale README |');
    expect(md).toContain('| 2 | Cloud hardening | CI, reproducible deploy |');
    expect(md).toContain('| 3 | cost visibility | token caps. |');
    expect(md.endsWith('Team: two Developers and a Tester. Reply approve, or tell me what to change.')).toBe(true);
  });

  it('keeps items without labels in a one-column table, and escapes pipes', () => {
    const md = questionMarkdown('Which first? (1) the a|b fix (2) the docs');
    expect(md).toContain('| # | Item |');
    expect(md).toContain('| 1 | the a\\|b fix |');
    expect(md).toContain('| 2 | the docs |');
  });

  it('leaves a question that is already Markdown, or has no numbered run, as it is', () => {
    const listed = 'Approve?\n\n1. one\n2. two';
    expect(questionMarkdown(listed)).toBe(listed);
    expect(questionMarkdown('Is (1) enough here?')).toBe('Is (1) enough here?');
    expect(questionMarkdown('Pick (2) or (3)')).toBe('Pick (2) or (3)');
  });

  it('finds the options of a choice, one button label each', () => {
    const q = 'Your reply doesn\'t say which option you want. Which one?\n\n1. **Allow network access.** You allow api.nuget.org.\n2. **Run it yourself.** You run the tests.\n3. **Skip it** for now';
    expect(questionOptions(q).map((o) => [o.n, o.label])).toEqual([[1, 'Allow network access'], [2, 'Run it yourself'], [3, 'Skip it']]);
    expect(questionOptions('Which first? (1) the docs fix; (2) the tests').map((o) => o.label)).toEqual(['the docs fix', 'the tests']);
    // A plan to approve is not a choice: its numbered steps are not options.
    expect(questionOptions('Do you approve this plan? (1) tests (2) docs')).toEqual([]);
  });

  it('treats "how should we proceed… reply with 1, 2 or 3" as a choice, not an approval', () => {
    const q = 'TesterWren can\'t build or test.\n\nHow should we proceed?\n\n1. **Fix the network.** Allow those two hosts, then reply "retry".\n'
      + '2. **Offline packages.** Point me to a local cache.\n3. **Skip the build and test.** Not recommended.\n\nReply with 1, 2 (with the path), or 3.';
    expect(questionOptions(q).map((o) => o.label)).toEqual(['Fix the network', 'Offline packages', 'Skip the build and test']);
    expect(asksForApproval(q)).toBe(false);
    expect(asksForApproval('Do you approve this plan? (1) tests (2) docs')).toBe(true);
    expect(asksForApproval('Which region should Cloud use: eastus2 or westus?')).toBe(false);
  });

  it('numbered questions that each need an answer are not options', () => {
    const q = 'I need three answers from you.\n\n1. **Desktop repo URL.** What is the clone URL?\n2. **Live app vs main.bicep.** Which is right?\n'
      + '3. **Mail password.** Should it stay as it is?\n\nReply with the URL and your answers to 2 and 3.';
    expect(questionOptions(q)).toEqual([]);
    expect(asksForApproval(q)).toBe(false);
  });

  it('knows when a question asks for approval', () => {
    expect(asksForApproval('Do you approve this plan?')).toBe(true);
    expect(asksForApproval('Which region should Cloud use?')).toBe(false);
  });

  it('a set of proposals to approve ("reply approve, or say which to change") gets Approve, not option buttons', () => {
    const q = 'The design is ready. Do you agree with these four proposals, or what should change?\r\n\r\n'
      + '1. **Pin scope:** pins the files.\r\n2. **Concurrency:** one run per task.\r\n3. **Returns at once:** it answers "started".\r\n'
      + '4. **Concierge:** it may only draft.\r\n\r\nReply "approve" to accept all four, or say which to change.';
    expect(questionOptions(q)).toEqual([]);
    expect(asksForApproval(q)).toBe(true);
    expect(offersSkip(q)).toBe(false);
  });

  // The Mac mini's "Manager needs a decision" card. Its numbered list is the Results (findings), followed by a
  // separate closing question: the buttons were "1. Build" ... "5. Loopback to host port 9999".
  const findingsAsk = 'The localhost verify: build, isolation and outbound checks behaved as expected, but `dotnet test` cannot run on macOS '
    + 'in the sandbox: the test host fails at TcpListener.Start with SocketException (13) Permission denied.\n\n'
    + 'Results:\n\n'
    + '1. Build: succeeded (0 warnings, 0 errors).\n'
    + '2. dotnet test (VSTest): failed, SocketException (13) Permission denied at TcpListener.Start.\n'
    + '3. MTP variant (dotnet exec of the test dll): failed, NamedPipeServerStream aborted (exit 134).\n'
    + '4. Isolation and outbound (writes outside the clone, api.nuget.org): blocked as expected.\n'
    + '5. Loopback to host port 9999: refused.\n\n'
    + 'Do you want to turn on **Allow localhost (macOS)** in Team settings > Network? It lets the run\'s own processes talk to '
    + 'each other and to this Mac\'s localhost. Reply **approve** once it is on and I will have the test run repeated, or tell '
    + 'me to skip it and close without the test run.';

  it('a findings list followed by a closing question gives no numbered buttons, but Approve and Skip', () => {
    expect(questionOptions(findingsAsk)).toEqual([]);
    expect(asksForApproval(findingsAsk)).toBe(true);
    expect(offersSkip(findingsAsk)).toBe(true);
  });

  it('the same findings with CRLF line ends, or inline "(1) ... (5)", are not options either', () => {
    expect(questionOptions(findingsAsk.replace(/\n/g, '\r\n'))).toEqual([]);
    const inline = 'Results: (1) Build: succeeded; (2) dotnet test: failed; (3) Loopback: refused. Do you want to turn on Allow localhost? '
      + 'Reply approve, or tell me to skip it.';
    expect(questionOptions(inline)).toEqual([]);
    expect(offersSkip(inline)).toBe(true);
  });

  it('a list of answers is still options when the question comes after it', () => {
    const q = 'The tests cannot run here.\n\n1. **Allow localhost** for this team and re-run.\n2. **Skip the tests** and close.\n\nWhich do you want?';
    expect(questionOptions(q).map((o) => o.label)).toEqual(['Allow localhost', 'Skip the tests']);
  });

  it('Skip is offered only when the question offers to skip or decline', () => {
    expect(offersSkip('Do you approve this plan?')).toBe(false);
    expect(offersSkip('Reply approve to go on, or decline and I will stop.')).toBe(true);
    expect(offersSkip('Which region should Cloud use? Or skip the deploy.')).toBe(false);
  });
});

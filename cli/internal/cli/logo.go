package cli

import (
	"fmt"
	"io"
	"strings"
)

// The Yawble head mark (the website's logo-mark.png) as Braille text: each character is a 2x4
// block of dots, 32 columns wide. Regenerate it from the image rather than editing it by hand.

// logoMark is the mark shown above the output of `yawble`, `yawble version` and `yawble up`
// when a person is reading a terminal. It is never written to a pipe or with --json.
const logoMark = `               ⢿⣆
           ⢿⣿⡀  ⣈⣷⣶⣿⣿⣿⣿⣿⣷⣶⣤⡀
            ⠈⠻⣦⡸⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣦⡀
           ⣤⣶⡄⠈⠳⣌⠻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⡀
   ⣠⠊     ⣠⠿⠿⠃  ⠈⢳⣌⣉⡙⢻⣿⣿⣿⣿⣿⣿⣿⣿⣧
  ⣼⠃   ⣴⣶⡞⠁      ⢰⣿⣿⣿⡆⣿⣿⣿⣿⣿⣿⣿⣿⡟
  ⣿⡀   ⠙⠛⠁     ⣠⠴⣦⡙⠛⢋⣴⣿⣿⣿⣿⣿⣿⣿⣿⣿⡀
⣆ ⠻⣷⣄⡀      ⠰⣿⡏⠁ ⢻⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣷
⠘⢷⣄⡈⠛⠿⢷⣶⣤⣄⣀⣀     ⣾⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⠏
  ⠉⠻⠷⣶⣤⣤⣉⣉⠛⠛⠿⠿⢶⣶⣴⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿
     ⠠⣌⣉⡙⠛⠻⠿⠶⣶⣤⣄⣉⠙⠻⢿⣿⣿⣿⣿⠟⠙⠛⠿⠟⠃
       ⠉⠙⠛⠻⠶⠶⣤⣌⣉⠛⠻⢦⣄⠈⠻⣿⡟
               ⠉⠙⠢⠄⠈⠓⢄⠈⢻⡄
                        ⠃`

// welcome is what `yawble` with no arguments says under the mark: the tagline, this binary's
// version (the %s) and where to go next.
const welcome = `Yawble — A fleet of agents, moving with you.
%s

Usage:
  yawble <command> [flags]

Get started:    yawble up
All commands:   yawble --help
Help on one:    yawble <command> --help
`

// Yawble orange, #E83B00, as a 24-bit colour and as the nearest of the 256 standard colours for a
// terminal that has no 24-bit colour (macOS Terminal among them).
const (
	orange24  = "\x1b[38;2;232;59;0m"
	orange256 = "\x1b[38;5;202m"
	reset     = "\x1b[0m"
)

// showLogo draws the mark, then a blank line, when a person is reading a terminal: in Yawble
// orange when colour is on, and the colour ends with the mark.
func showLogo(deps Deps, out io.Writer) {
	if !deps.StdoutTerminal {
		return
	}
	if !deps.Color {
		fmt.Fprintln(out, logoMark)
		fmt.Fprintln(out)
		return
	}
	colour := orange256
	if deps.Env != nil {
		if ct := strings.ToLower(deps.Env("COLORTERM")); ct == "truecolor" || ct == "24bit" {
			colour = orange24
		}
	}
	fmt.Fprintln(out, colour+logoMark+reset)
	fmt.Fprintln(out)
}

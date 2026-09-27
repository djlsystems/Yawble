package cli

import (
	"fmt"
	"io"
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

// showLogo draws the mark, then a blank line, when a person is reading a terminal.
func showLogo(deps Deps, out io.Writer) {
	if !deps.StdoutTerminal {
		return
	}
	fmt.Fprintln(out, logoMark)
	fmt.Fprintln(out)
}

// Package maskedinput reads a secret from a terminal already in raw mode, echoing one * for each
// character typed or pasted, so a person sees that a paste landed without the secret on screen.
package maskedinput

import (
	"bufio"
	"errors"
	"io"
	"unicode/utf8"
)

// ErrInterrupted is returned when the person presses Ctrl+C at the prompt.
var ErrInterrupted = errors.New("interrupted")

// Read reads one line from r, a terminal in raw mode, and writes one * to w for every character
// it keeps. Enter ends the line. Backspace removes the last character and its *; Ctrl+U clears
// the line. Escape sequences (arrow keys, and the markers a terminal puts around a paste) are
// skipped, and other control characters are ignored. Ctrl+C is ErrInterrupted and Ctrl+D on an
// empty line, or the end of r, ends the read with what was kept.
func Read(r io.Reader, w io.Writer) (string, error) {
	in := bufio.NewReader(r)
	var value []byte

	erase := func(n int) {
		for i := 0; i < n; i++ {
			_, _ = w.Write([]byte("\b \b"))
		}
	}

	for {
		b, err := in.ReadByte()
		if err != nil {
			if errors.Is(err, io.EOF) {
				return string(value), nil
			}
			return "", err
		}

		switch {
		case b == '\r' || b == '\n':
			return string(value), nil
		case b == 0x03: // Ctrl+C
			return "", ErrInterrupted
		case b == 0x04: // Ctrl+D
			if len(value) == 0 {
				return "", nil
			}
		case b == 0x7f || b == 0x08: // Backspace
			if len(value) > 0 {
				_, size := utf8.DecodeLastRune(value)
				value = value[:len(value)-size]
				erase(1)
			}
		case b == 0x15: // Ctrl+U
			erase(utf8.RuneCount(value))
			value = value[:0]
		case b == 0x1b: // Escape: skip the whole sequence.
			skipEscape(in)
		case b < 0x20:
			// Another control character: not part of a secret.
		default:
			value = append(value, b)
			// One * per character: a UTF-8 continuation byte adds to the character before it.
			if b&0xC0 != 0x80 {
				_, _ = w.Write([]byte("*"))
			}
		}
	}
}

// skipEscape consumes the rest of an escape sequence: a CSI (ESC [ ... final byte in @ to ~), an
// SS3 (ESC O and one byte), or a single byte after ESC.
func skipEscape(in *bufio.Reader) {
	next, err := in.ReadByte()
	if err != nil {
		return
	}
	switch next {
	case '[':
		for {
			c, err := in.ReadByte()
			if err != nil || (c >= 0x40 && c <= 0x7e) {
				return
			}
		}
	case 'O':
		_, _ = in.ReadByte()
	}
}

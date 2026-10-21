// Package text holds the words every command spells the same way.
package text

import "fmt"

// Plural is "1 noun" or "N nouns".
func Plural(n int, noun string) string {
	if n == 1 {
		return "1 " + noun
	}
	return fmt.Sprintf("%d %ss", n, noun)
}

// PluralOf is "N one" or "N many", for a noun whose plural is its own word.
func PluralOf(n int, one, many string) string {
	if n == 1 {
		return fmt.Sprintf("%d %s", n, one)
	}
	return fmt.Sprintf("%d %s", n, many)
}

// ErrorText is the error's text, or "" for none.
func ErrorText(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}

// OrEmpty is the string, or "" for a nil pointer.
func OrEmpty(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

package maskedinput

import (
	"bytes"
	"errors"
	"strings"
	"testing"
)

func read(t *testing.T, typed string) (string, string, error) {
	t.Helper()
	var echo bytes.Buffer
	value, err := Read(strings.NewReader(typed), &echo)
	return value, echo.String(), err
}

func TestAPasteShowsOneStarPerCharacterAndKeepsTheValue(t *testing.T) {
	value, echo, err := read(t, "sk-abc123\r")
	if err != nil || value != "sk-abc123" {
		t.Fatalf("value %q, err %v", value, err)
	}
	if echo != "*********" {
		t.Fatalf("echo %q, want nine stars", echo)
	}
	if strings.Contains(echo, "abc") {
		t.Fatal("the secret reached the screen")
	}
}

func TestTheMarkersAroundABracketedPasteAreNotPartOfTheValue(t *testing.T) {
	value, echo, _ := read(t, "\x1b[200~token\x1b[201~\r")
	if value != "token" || echo != "*****" {
		t.Fatalf("value %q echo %q", value, echo)
	}
}

func TestBackspaceRemovesTheLastCharacterAndItsStar(t *testing.T) {
	value, echo, _ := read(t, "abx\x7fc\r")
	if value != "abc" {
		t.Fatalf("value %q", value)
	}
	if echo != "***\b \b*" {
		t.Fatalf("echo %q", echo)
	}
}

func TestCtrlUClearsTheLine(t *testing.T) {
	value, _, _ := read(t, "wrong\x15right\r")
	if value != "right" {
		t.Fatalf("value %q", value)
	}
}

func TestAMultiByteCharacterIsOneStar(t *testing.T) {
	value, echo, _ := read(t, "é€\r")
	if value != "é€" || echo != "**" {
		t.Fatalf("value %q echo %q", value, echo)
	}
}

func TestCtrlCIsInterrupted(t *testing.T) {
	_, _, err := read(t, "abc\x03")
	if !errors.Is(err, ErrInterrupted) {
		t.Fatalf("err %v", err)
	}
}

func TestArrowKeysAndOtherControlCharactersAreIgnored(t *testing.T) {
	value, echo, _ := read(t, "a\x1b[Db\x1bOA\tc\r")
	if value != "abc" || echo != "***" {
		t.Fatalf("value %q echo %q", value, echo)
	}
}

func TestTheEndOfInputEndsTheLineWithWhatWasTyped(t *testing.T) {
	value, _, err := read(t, "abc")
	if err != nil || value != "abc" {
		t.Fatalf("value %q err %v", value, err)
	}
}

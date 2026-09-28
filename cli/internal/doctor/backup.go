package doctor

import (
	"fmt"
	"os"
	"time"

	"github.com/djlsystems/yawble/cli/internal/backup"
)

// Info is a line that informs and never fails: the newest backup, which a person may keep
// anywhere or nowhere.
const Info Verdict = "info"

// BackupCheck names the newest backup `yawble backup` (or a restore's safety copy) wrote on this
// computer and its age. None, or one since moved, is said, never failed.
func BackupCheck(configDir string, now time.Time) Check {
	c := Check{Name: "backup", Verdict: Info}
	rec, ok := backup.Last(configDir)
	switch {
	case !ok:
		c.Detail = "no backup has been written on this computer (yawble backup)"
	case !exists(rec.Path):
		c.Detail = fmt.Sprintf("the newest backup written here, %s, %s, is no longer at that path", rec.Path, age(now.Sub(rec.At)))
	default:
		c.Detail = fmt.Sprintf("newest %s, %s", rec.Path, age(now.Sub(rec.At)))
	}
	return c
}

func exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

// age is how long ago, in the largest whole unit that fits.
func age(d time.Duration) string {
	switch {
	case d < time.Minute:
		return "just now"
	case d < time.Hour:
		return plural(int(d/time.Minute), "minute") + " old"
	case d < 48*time.Hour:
		return plural(int(d/time.Hour), "hour") + " old"
	default:
		return plural(int(d/(24*time.Hour)), "day") + " old"
	}
}

func plural(n int, unit string) string {
	if n == 1 {
		return "1 " + unit
	}
	return fmt.Sprintf("%d %ss", n, unit)
}

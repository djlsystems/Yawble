package doctor

import (
	"fmt"
	"os"
	"time"

	"github.com/djlsystems/yawble/cli/internal/backup"
)

// Info is a line that informs and never fails: a figure a person may want, with nothing to do.
const Info Verdict = "info"

// BackupCheck folds the newest copy `yawble backup` (or a restore's safety copy) wrote on this
// computer into the "backups" row, beside the Host's daily copies in the data volume, so "am I
// backed up?" has one answer. The daily copies are lost with the volume, so without a copy here
// the row warns and names `yawble backup`; a copy here is named with its age, and one since
// moved is said. The separate row this was is gone: two rows disagreed.
func BackupCheck(checks []Check, configDir string, now time.Time) []Check {
	row, at := Check{Name: "backups", Verdict: Skip, Detail: "daily copies in the data volume: not known"}, -1
	for i, c := range checks {
		if c.Name == "backups" {
			row, at = c, i
			if c.Verdict == Skip {
				row.Detail = "daily copies in the data volume: not known (" + c.Detail + ")"
			}
			break
		}
	}
	const fix = "yawble backup (writes a copy on this computer, outside the data volume)"
	rec, ok := backup.Last(configDir)
	switch {
	case !ok:
		row.Detail += "; no copy on this computer yet"
		row.Verdict, row.Fix = Warn, fix
	case !exists(rec.Path):
		row.Detail += fmt.Sprintf("; the newest copy written on this computer, %s, %s, is no longer there", rec.Path, age(now.Sub(rec.At)))
		row.Verdict, row.Fix = Warn, fix
	default:
		row.Detail += fmt.Sprintf("; newest copy on this computer: %s, %s", rec.Path, age(now.Sub(rec.At)))
		if row.Verdict == Skip {
			row.Verdict = OK
		}
	}
	if at < 0 {
		return append(checks, row)
	}
	checks[at] = row
	return checks
}

// dailyCopies counts the Host's daily copies in words.
func dailyCopies(n int) string {
	if n == 1 {
		return "1 daily copy"
	}
	return fmt.Sprintf("%d daily copies", n)
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

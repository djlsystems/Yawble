package machine

import "testing"

// The read inside the machine: whose delegate.conf it is, from its first line, CRLF or not; an
// answer without both labels is not measured, never "no controllers".
func TestParseDelegation(t *testing.T) {
	for _, c := range []struct {
		name, out, file string
		measured, cause bool
	}{
		{"absent", "controllers: \nuser.slice: \ndelegate.conf: absent\n", DelegateAbsent, true, true},
		{"ours", "controllers: \nuser.slice: \ndelegate.conf: present\n" + DelegateContent, DelegateOurs, true, false},
		{"ours crlf", "controllers: cpu\r\nuser.slice: \r\ndelegate.conf: present\r\n" + DelegateMark + "\r\n[Service]\r\n" + DelegateLine + "\r\n", DelegateOurs, true, false},
		{"foreign", "controllers: \nuser.slice: \ndelegate.conf: present\n[Service]\nDelegate=memory pids cpu io\n", DelegateForeign, true, false},
		{"has them", "controllers: cpuset cpu io memory pids\nuser.slice: \ndelegate.conf: absent\n", DelegateAbsent, true, false},
		{"empty", "", "", false, false},
		{"unreadable", "controllers-unreadable: /sys/x\nuser.slice: \ndelegate.conf: absent\n", "", false, false},
	} {
		d := parseDelegation(c.out)
		if d.Measured != c.measured || d.File != c.file || d.IsTheCause() != c.cause {
			t.Errorf("%s: %+v cause %v", c.name, d, d.IsTheCause())
		}
	}
}

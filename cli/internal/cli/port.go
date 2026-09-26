package cli

import (
	"context"
	"fmt"
	"io"
	"strconv"

	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/doctor"
	"github.com/djlsystems/yawble/cli/internal/engine"
	"github.com/djlsystems/yawble/cli/internal/instance"
)

// portSearch is how far above a taken port `up` looks for a free one.
const portSearch = 50

// ensurePort settles the host port before `up` creates anything. Yawble's own running container
// answering on it is not a conflict. Held by another program, `up` offers the next free port
// above it and saves the answer, so every later command and the board's address agree; without a
// yes it stops, naming the setting, with nothing created.
func ensurePort(ctx context.Context, deps Deps, c config.Config, yes bool, out io.Writer) (int, error) {
	port := c.Port
	if port == 0 {
		port = config.DefaultPort
	}
	free := deps.PortFree
	if free == nil {
		free = doctor.PortFree
	}
	if free(port) {
		return port, nil
	}
	if info, err := engineOf(deps, c).Inspect(ctx, instance.ContainerName); err == nil && info.State == engine.StateRunning {
		return port, nil
	}

	next := 0
	for p := port + 1; p <= port+portSearch && p <= 65535; p++ {
		if free(p) {
			next = p
			break
		}
	}
	if next == 0 {
		return 0, fmt.Errorf("port %d is in use by another program and no port up to %d is free; free one, or choose one with: yawble config set port <port>", port, port+portSearch)
	}

	ok, err := confirm(deps, yes, out, fmt.Sprintf("Port %d is in use by another program. Use port %d instead?", port, next))
	if err != nil {
		return 0, err
	}
	if !ok {
		return 0, fmt.Errorf("port %d is in use by another program; free it, or choose another with: yawble config set port <port>", port)
	}
	if err := savePort(deps, next); err != nil {
		return 0, err
	}
	fmt.Fprintf(out, "using port %d; saved in yawble's config (yawble config set port changes it)\n", next)
	return next, nil
}

func savePort(deps Deps, port int) error {
	raw, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return err
	}
	if err := raw.Set("port", strconv.Itoa(port)); err != nil {
		return err
	}
	_, err = config.Save(deps.ConfigDir, raw)
	return err
}

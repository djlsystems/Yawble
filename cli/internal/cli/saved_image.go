package cli

import (
	"fmt"
	"io"
	"regexp"
	"strings"

	"github.com/djlsystems/yawble/cli/internal/buildinfo"
	"github.com/djlsystems/yawble/cli/internal/config"
	"github.com/djlsystems/yawble/cli/internal/release"
)

// releaseTag is the tag Yawble's release script pushes: yyyy.mm.dd.N, nothing else.
var releaseTag = regexp.MustCompile(`^\d{4}\.\d{2}\.\d{2}\.\d+$`)

// savedRelease answers the release tag a saved reference names, or "" when it names anything
// else: a local or dev build, another registry, a digest, a tag that is not a release version.
// suffix is "-worker" for workerImage, "" for image.
func savedRelease(ref, suffix string) string {
	tag, ok := strings.CutPrefix(ref, buildinfo.ImageRepository+":")
	if !ok {
		return ""
	}
	if tag, ok = strings.CutSuffix(tag, suffix); !ok || !releaseTag.MatchString(tag) {
		return ""
	}
	return tag
}

// savedImageKeys are the settings that can hold the instance on an image, with the suffix the
// pin carries for each.
var savedImageKeys = []struct{ key, suffix string }{{"image", ""}, {"workerImage", "-worker"}}

func savedValue(c config.Config, key string) string {
	if key == "image" {
		return c.Image
	}
	return c.WorkerImage
}

// reconcileSavedImages is what update does with a saved image before it moves the instance. A
// saved image naming a release older than this yawble's pin is the release that was current when
// it was saved: it is rewritten to the pin with the same writer as `config set`, and said. Anything
// else is the person's own choice and is left; the answer is one sentence for each setting that
// now holds the instance away from the pin. A saved release newer than the pin is never moved back.
func reconcileSavedImages(deps Deps, out io.Writer) ([]string, error) {
	pin := buildinfo.Image()
	if pin == "" {
		return nil, nil // a dev build pins nothing to move a saved image to
	}
	// The FILE, as `config set` reads it: an exported YAWBLE_IMAGE is not copied into it.
	c, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return nil, err
	}
	var held []string
	changed := false
	for _, k := range savedImageKeys {
		saved, want := savedValue(c, k.key), pin+k.suffix
		if saved == "" || saved == want {
			continue
		}
		tag := savedRelease(saved, k.suffix)
		switch {
		case tag != "" && release.IsNewer(buildinfo.ImageTag, tag):
			if err := c.Set(k.key, want); err != nil {
				return nil, err
			}
			changed = true
			fmt.Fprintf(out, "%s %s -> %s (saved setting updated)\n", k.key, saved, want)
		case tag != "" && release.IsNewer(tag, buildinfo.ImageTag):
			fmt.Fprintf(out, "%s %s is newer than %s, which this yawble pins; left as it is (an update never moves back)\n", k.key, saved, want)
		default:
			held = append(held, fmt.Sprintf("the saved setting %s in config.toml holds the instance on %s, not %s, which this yawble pins; change it with `yawble config set %s <reference>` or clear it with `yawble config set %s \"\"`, then run yawble update --instance",
				k.key, saved, want, k.key, k.key))
		}
	}
	if changed {
		if _, err := config.Save(deps.ConfigDir, c); err != nil {
			return nil, err
		}
	}
	return held, nil
}

// sayVersionSavedImages is `version`'s line for each saved image that differs from the pin: the
// pin is what the binary was built with, the saved setting is what `up` starts.
func sayVersionSavedImages(deps Deps, out io.Writer) {
	c, _, err := config.Load(deps.ConfigDir, func(string) string { return "" })
	if err != nil {
		return // version still answers with a config it cannot read; up and doctor say why
	}
	pin := buildinfo.Image()
	for _, k := range savedImageKeys {
		saved := savedValue(c, k.key)
		if saved == "" || (pin != "" && saved == pin+k.suffix) {
			continue
		}
		label := "saved image"
		if k.key == "workerImage" {
			label = "saved workerImage"
		}
		fmt.Fprintf(out, "%s %s (config.toml; this is what yawble up starts)\n", label, saved)
	}
}

// Package buildinfo holds what a release stamps into the binary. The zero values are honest: a
// build nobody stamped is "dev" and pins no image, and `up` refuses to guess one.
package buildinfo

var (
	// Version is the release tag, set with -ldflags "-X .../buildinfo.Version=v2026.09.24.1". It is
	// the same tag Yawble carries: core and CLI are released together under one number.
	Version = "dev"
	// Commit is the short commit the release was built from.
	Commit = ""
	// ImageTag is the Yawble image this release pins: Version without its v, the bare
	// yyyy.mm.dd.N that Yawble's release script pushes. Empty means this build pins none.
	ImageTag = ""
)

// ImageRepository is where Yawble's release script pushes the image.
const ImageRepository = "ghcr.io/djlsystems/yawble"

// Image is the exact reference this build was released against, or "" when it was not released.
// It is never "latest": a CLI and a Host that never ran together must not meet by accident.
func Image() string {
	if ImageTag == "" {
		return ""
	}
	return ImageRepository + ":" + ImageTag
}

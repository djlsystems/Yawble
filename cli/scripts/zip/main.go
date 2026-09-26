// zip writes one file into a zip archive. The release script uses it for the Windows assets so a
// release can be cut from any machine that has Go, which Git for Windows (no `zip`) is not.
//
//	go run ./scripts/zip <archive.zip> <file>
package main

import (
	"archive/zip"
	"fmt"
	"io"
	"os"
	"path/filepath"
)

func main() {
	if len(os.Args) != 3 {
		fmt.Fprintln(os.Stderr, "usage: go run ./scripts/zip <archive.zip> <file>")
		os.Exit(2)
	}
	if err := run(os.Args[1], os.Args[2]); err != nil {
		fmt.Fprintln(os.Stderr, "zip:", err)
		os.Exit(1)
	}
}

func run(archive, file string) error {
	in, err := os.Open(file)
	if err != nil {
		return err
	}
	defer in.Close()
	info, err := in.Stat()
	if err != nil {
		return err
	}
	out, err := os.Create(archive)
	if err != nil {
		return err
	}
	w := zip.NewWriter(out)
	header, err := zip.FileInfoHeader(info)
	if err != nil {
		return err
	}
	header.Name = filepath.Base(file)
	header.Method = zip.Deflate
	entry, err := w.CreateHeader(header)
	if err != nil {
		return err
	}
	if _, err := io.Copy(entry, in); err != nil {
		return err
	}
	if err := w.Close(); err != nil {
		return err
	}
	return out.Close()
}

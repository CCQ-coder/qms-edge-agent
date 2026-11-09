package main

import (
	"archive/tar"
	"compress/gzip"
	"context"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

const rolledMESPrefix = "mes-captures-rolled-"

func (m *mesMonitor) rollMESDirectory(directory string, now time.Time) error {
	parent := filepath.Dir(directory)
	archive, err := os.MkdirTemp(parent, rolledMESPrefix+now.In(time.Local).Format("20060102T150405.000000000")+"-")
	if err != nil {
		return err
	}
	source := filepath.Join(archive, "mes-captures")
	if err := os.Rename(directory, source); err != nil {
		_ = os.Remove(archive)
		return err
	}
	if err := os.Mkdir(directory, 0700); err != nil {
		if rollbackErr := os.Rename(source, directory); rollbackErr != nil {
			return fmt.Errorf("create new MES capture directory: %w; restore old directory: %v", err, rollbackErr)
		}
		_ = os.Remove(archive)
		return err
	}
	if err := syncMESDirectory(parent); err != nil {
		return err
	}
	m.wakeMESArchiveWorker()
	return nil
}

func (m *mesMonitor) wakeMESArchiveWorker() {
	if m.archiveWake != nil {
		select {
		case m.archiveWake <- struct{}{}:
		default:
		}
	}
}

func (m *mesMonitor) runMESArchiveWorker(ctx context.Context) {
	ticker := time.NewTicker(time.Hour)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-m.archiveWake:
		case <-ticker.C:
		}
		if err := m.rollStaleMESDirectory(); err != nil {
			log.Printf("MES daily capture rotation failed: %v", err)
		}
		entries, err := os.ReadDir(m.bridge.config.QueueDir)
		if err != nil {
			log.Printf("MES archive scan failed: %v", err)
			continue
		}
		for _, entry := range entries {
			if entry.IsDir() && strings.HasPrefix(entry.Name(), rolledMESPrefix) {
				path := filepath.Join(m.bridge.config.QueueDir, entry.Name())
				var err error
				if strings.HasSuffix(entry.Name(), ".deleting") {
					err = finishMESArchiveDeletion(path)
				} else {
					err = compressMESArchive(path)
				}
				if err != nil {
					log.Printf("MES archive processing failed for %s: %v", entry.Name(), err)
				}
			}
		}
	}
}

func (m *mesMonitor) rollStaleMESDirectory() error {
	m.storeMu.Lock()
	defer m.storeMu.Unlock()
	directory := filepath.Join(m.bridge.config.QueueDir, "mes-captures")
	entries, err := os.ReadDir(directory)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".json") {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		now := time.Now()
		if info.ModTime().In(time.Local).Format("20060102") != now.In(time.Local).Format("20060102") {
			return m.rollMESDirectory(directory, now)
		}
		break
	}
	return nil
}

func compressMESArchive(directory string) error {
	source := filepath.Join(directory, "mes-captures")
	manifest, err := mesArchiveManifest(source)
	if errors.Is(err, os.ErrNotExist) {
		entries, readErr := os.ReadDir(directory)
		if readErr == nil && len(entries) == 0 {
			return os.Remove(directory)
		}
	}
	if err != nil {
		return err
	}
	if len(manifest) == 0 {
		return errors.New("rolled MES directory has no capture files")
	}
	archive := directory + ".tar.gz"
	temporary := archive + ".tmp"
	if _, err := os.Stat(archive); errors.Is(err, os.ErrNotExist) {
		if err := os.Remove(temporary); err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
		file, err := os.OpenFile(temporary, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
		if err != nil {
			return err
		}
		writeErr := writeMESArchive(file, source, manifest)
		closeErr := file.Close()
		if writeErr != nil {
			_ = os.Remove(temporary)
			return writeErr
		}
		if closeErr != nil {
			_ = os.Remove(temporary)
			return closeErr
		}
		if err := verifyMESArchive(temporary, manifest, true); err != nil {
			_ = os.Remove(temporary)
			return err
		}
		if err := os.Link(temporary, archive); err != nil {
			return err
		}
		if err := syncMESDirectory(filepath.Dir(directory)); err != nil {
			return err
		}
	} else if err != nil {
		return err
	}
	if err := verifyMESArchive(archive, manifest, true); err != nil {
		return err
	}
	if err := os.Remove(temporary); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	contents, err := os.ReadDir(directory)
	if err != nil {
		return err
	}
	if len(contents) != 1 || contents[0].Name() != "mes-captures" || !contents[0].IsDir() {
		return errors.New("rolled MES directory contains unexpected files")
	}
	deleting := directory + ".deleting"
	if _, err := os.Stat(deleting); err == nil {
		return fmt.Errorf("MES archive cleanup directory already exists: %s", deleting)
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if err := os.Rename(directory, deleting); err != nil {
		return err
	}
	if err := syncMESDirectory(filepath.Dir(directory)); err != nil {
		return err
	}
	return finishMESArchiveDeletion(deleting)
}

func finishMESArchiveDeletion(deleting string) error {
	directory := strings.TrimSuffix(deleting, ".deleting")
	if directory == deleting {
		return errors.New("invalid MES archive cleanup directory")
	}
	contents, err := os.ReadDir(deleting)
	if err != nil {
		return err
	}
	if len(contents) > 1 || (len(contents) == 1 && (contents[0].Name() != "mes-captures" || !contents[0].IsDir())) {
		return errors.New("MES archive cleanup directory contains unexpected files")
	}
	var remaining map[string]int64
	if len(contents) == 1 {
		remaining, err = mesArchiveManifest(filepath.Join(deleting, "mes-captures"))
		if err != nil {
			return err
		}
	}
	if err := verifyMESArchive(directory+".tar.gz", remaining, false); err != nil {
		return err
	}
	return os.RemoveAll(deleting)
}

func mesArchiveManifest(directory string) (map[string]int64, error) {
	entries, err := os.ReadDir(directory)
	if err != nil {
		return nil, err
	}
	manifest := make(map[string]int64, len(entries))
	for _, entry := range entries {
		info, err := entry.Info()
		if err != nil {
			return nil, err
		}
		if !info.Mode().IsRegular() {
			return nil, fmt.Errorf("unexpected file in rolled MES directory: %s", entry.Name())
		}
		manifest[entry.Name()] = info.Size()
	}
	return manifest, nil
}

func writeMESArchive(file *os.File, source string, manifest map[string]int64) error {
	compressed := gzip.NewWriter(file)
	writer := tar.NewWriter(compressed)
	entries, err := os.ReadDir(source)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		name := entry.Name()
		original, err := os.Open(filepath.Join(source, name))
		if err != nil {
			return err
		}
		info, err := original.Stat()
		if err == nil && (info.Size() != manifest[name] || !info.Mode().IsRegular()) {
			err = fmt.Errorf("MES capture changed during compression: %s", name)
		}
		if err == nil {
			err = writer.WriteHeader(&tar.Header{Name: "mes-captures/" + name, Mode: 0600, Size: info.Size(), ModTime: info.ModTime()})
		}
		if err == nil {
			_, err = io.CopyN(writer, original, info.Size())
		}
		closeErr := original.Close()
		if err != nil {
			return err
		}
		if closeErr != nil {
			return closeErr
		}
	}
	if err := writer.Close(); err != nil {
		return err
	}
	if err := compressed.Close(); err != nil {
		return err
	}
	return file.Sync()
}

func verifyMESArchive(path string, manifest map[string]int64, exact bool) error {
	file, err := os.Open(path)
	if err != nil {
		return err
	}
	defer file.Close()
	compressed, err := gzip.NewReader(file)
	if err != nil {
		return err
	}
	defer compressed.Close()
	reader := tar.NewReader(compressed)
	seen := make(map[string]bool, len(manifest))
	for {
		header, err := reader.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return err
		}
		name, ok := strings.CutPrefix(header.Name, "mes-captures/")
		if !ok || name == "" || strings.Contains(name, "/") || seen[name] || (header.Typeflag != tar.TypeReg && header.Typeflag != tar.TypeRegA) {
			return fmt.Errorf("MES archive entry does not match source: %s", header.Name)
		}
		if manifest != nil {
			if size, ok := manifest[name]; ok && header.Size != size {
				return fmt.Errorf("MES archive entry changed: %s", header.Name)
			} else if !ok && exact {
				return fmt.Errorf("unexpected MES archive entry: %s", header.Name)
			}
		}
		seen[name] = true
		if _, err := io.Copy(io.Discard, reader); err != nil {
			return err
		}
	}
	if len(seen) == 0 || (exact && manifest != nil && len(seen) != len(manifest)) {
		return fmt.Errorf("MES archive contains %d of %d captures", len(seen), len(manifest))
	}
	for name := range manifest {
		if !seen[name] {
			return fmt.Errorf("MES archive misses source capture: %s", name)
		}
	}
	_, err = io.Copy(io.Discard, compressed)
	return err
}

func syncMESDirectory(path string) error {
	if runtime.GOOS == "windows" {
		return nil
	}
	directory, err := os.Open(path)
	if err != nil {
		return err
	}
	defer directory.Close()
	return directory.Sync()
}

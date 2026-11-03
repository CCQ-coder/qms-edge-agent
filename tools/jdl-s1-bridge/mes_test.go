package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestMESMonitorCapturesHTTPAndRawTCPOnOnePort(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- b.serveMESCapture(listener, time.Now().Add(5*time.Second)) }()
	defer func() { listener.Close(); <-done }()

	client := &http.Client{Timeout: time.Second}
	response, err := client.Post("http://"+listener.Addr().String()+"/httptest.php?machine=E1807091137", "text/plain", strings.NewReader("state=run"))
	if err != nil {
		t.Fatal(err)
	}
	io.Copy(io.Discard, response.Body)
	response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("HTTP status = %d", response.StatusCode)
	}
	response, err = client.Post("http://"+listener.Addr().String()+basePath+"/IO/UploadDeviceStatus", "application/json", strings.NewReader(`{"AssetsCode":"E1807091137","S_Power":1}`))
	if err != nil {
		t.Fatal(err)
	}
	io.Copy(io.Discard, response.Body)
	response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("EAP status = %d", response.StatusCode)
	}

	conn, err := net.DialTimeout("tcp", listener.Addr().String(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	raw := []byte{0x02, 0x00, 0x07, 'M', 'E', 'S', 0x03}
	if _, err := conn.Write(raw); err != nil {
		t.Fatal(err)
	}
	conn.Close()

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
		if len(files) == 3 {
			seen := map[string]bool{}
			for _, file := range files {
				data, err := os.ReadFile(file)
				if err != nil {
					t.Fatal(err)
				}
				var record captureRecord
				if err := json.Unmarshal(data, &record); err != nil {
					t.Fatal(err)
				}
				if record.Transport == "http" && record.Path == "/httptest.php" && record.Method == "POST" && record.Query == "machine=E1807091137" && string(record.Body) == "state=run" {
					seen["http"] = true
				}
				if record.Transport == "tcp" && string(record.Body) == string(raw) && record.RemoteAddr != "" {
					seen["tcp"] = true
				}
				if record.Transport == "http" && record.Path == basePath+"/IO/UploadDeviceStatus" && strings.Contains(string(record.Body), `"S_Power":1`) {
					seen["eap"] = true
				}
			}
			if !seen["http"] || !seen["tcp"] || !seen["eap"] {
				t.Fatalf("captures did not contain all routes: %v", seen)
			}
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("timed out waiting for both captures")
}

func TestMESMonitorArchivesUnknownHTTPWithoutAcknowledgingIt(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	m := &mesMonitor{bridge: b}
	request := httptest.NewRequest(http.MethodPost, "/newvendor/upload?machine=7", strings.NewReader("unrecognized report"))
	response := httptest.NewRecorder()
	m.handleHTTP(response, request)
	if response.Code != http.StatusNotFound {
		t.Fatalf("unknown path status = %d", response.Code)
	}
	files, err := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if err != nil || len(files) != 1 {
		t.Fatalf("unknown path captures = %d, %v", len(files), err)
	}
	data, err := os.ReadFile(files[0])
	if err != nil {
		t.Fatal(err)
	}
	var record captureRecord
	if err := json.Unmarshal(data, &record); err != nil {
		t.Fatal(err)
	}
	if record.Path != "/newvendor/upload" || record.Query != "machine=7" || string(record.Body) != "unrecognized report" || record.ResponseStatus != http.StatusNotFound {
		t.Fatalf("unknown request was not fully archived: %+v", record)
	}
}

func TestMESMonitorCapturesLegacySoftwarePathOnPublicPort(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8", "113.195.85.167/32"}, SourceIDs: []string{"E1807091137"}, QueueDir: t.TempDir(), CaptureOnly: true, ForwardMES: true, CaptureToken: strings.Repeat("a", 32), MESListen: "0.0.0.0:8604", Broker: "127.0.0.1:1883", ProductID: "34", SerialNumber: "E1807091137", UserID: "1", Username: "u", Password: "p"}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	body := "machine=E1807091137&state=run"
	request := httptest.NewRequest(http.MethodPost, "/jdlSave/MchnData", strings.NewReader(body))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.RemoteAddr = "113.195.85.167:12345"
	response := httptest.NewRecorder()
	(&mesMonitor{bridge: b}).handleHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("legacy software path status = %d", response.Code)
	}
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if len(files) != 1 {
		t.Fatalf("legacy software captures = %d", len(files))
	}
	data, err := os.ReadFile(files[0])
	if err != nil {
		t.Fatal(err)
	}
	var record captureRecord
	if err := json.Unmarshal(data, &record); err != nil {
		t.Fatal(err)
	}
	if record.Path != "/jdlSave/MchnData" || string(record.Body) != body || record.ContentType != "application/x-www-form-urlencoded" {
		t.Fatalf("legacy software request changed: %+v", record)
	}
	queued, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "*.json"))
	if len(queued) != 0 {
		t.Fatal("unknown legacy body was forwarded to S1")
	}
}

func TestMESMonitorClassifiesOtherHTTPMethods(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- b.serveMESCapture(listener, time.Now().Add(5*time.Second)) }()
	defer func() { listener.Close(); <-done }()
	client := &http.Client{Timeout: time.Second}
	for _, method := range []string{http.MethodPatch, http.MethodDelete} {
		request, err := http.NewRequest(method, "http://"+listener.Addr().String()+"/httptest.php", strings.NewReader(method))
		if err != nil {
			t.Fatal(err)
		}
		response, err := client.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		response.Body.Close()
		if response.StatusCode != http.StatusMethodNotAllowed {
			t.Fatalf("%s status = %d", method, response.StatusCode)
		}
	}
	files, err := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if err != nil || len(files) != 2 {
		t.Fatalf("other method captures = %d, %v", len(files), err)
	}
	seen := map[string]bool{}
	for _, file := range files {
		data, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		var record captureRecord
		if err := json.Unmarshal(data, &record); err != nil {
			t.Fatal(err)
		}
		if record.Path != "/httptest.php" || string(record.Body) != record.Method || record.ResponseStatus != http.StatusMethodNotAllowed {
			t.Fatalf("method was not archived as HTTP: %+v", record)
		}
		seen[record.Method] = true
	}
	if !seen[http.MethodPatch] || !seen[http.MethodDelete] {
		t.Fatalf("missing method capture: %v", seen)
	}
}

func TestMESMonitorKeepsShortFrameAtDeadline(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- b.serveMESCapture(listener, time.Now().Add(250*time.Millisecond)) }()
	defer func() { listener.Close(); <-done }()
	conn, err := net.DialTimeout("tcp", listener.Addr().String(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if _, err := conn.Write([]byte{0x02, 0x03}); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
		if len(files) == 1 {
			data, err := os.ReadFile(files[0])
			if err != nil {
				t.Fatal(err)
			}
			var record captureRecord
			if err := json.Unmarshal(data, &record); err != nil {
				t.Fatal(err)
			}
			if record.Transport != "tcp" || string(record.Body) != string([]byte{0x02, 0x03}) {
				t.Fatalf("short frame lost at deadline: %+v", record)
			}
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("short frame was not captured")
}

func TestMESEmptyConnectionsDoNotCreateCaptures(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	m := &mesMonitor{bridge: b, until: time.Now().Add(time.Minute)}
	for i := 0; i < 129; i++ {
		server, client := net.Pipe()
		client.Close()
		m.captureTCP(&mesConn{Conn: server, reader: bufio.NewReader(server)})
		server.Close()
	}
	server, client := net.Pipe()
	go func() { client.Write([]byte{0x01, 0x02}); client.Close() }()
	m.captureTCP(&mesConn{Conn: server, reader: bufio.NewReader(server)})
	server.Close()
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if len(files) != 1 {
		t.Fatalf("empty connections created files or blocked valid report: %d", len(files))
	}
}

func TestMESArchiveKeepsAllAcceptedReportsAndRejectsWhenFull(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32), MESListen: "0.0.0.0:8604", MESMaxFiles: 3}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	protected := filepath.Join(cfg.QueueDir, "captures", "original.json")
	if err := os.MkdirAll(filepath.Dir(protected), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(protected, []byte("5173 report"), 0600); err != nil {
		t.Fatal(err)
	}
	m := &mesMonitor{bridge: b}
	for i := 0; i < cfg.MESMaxFiles; i++ {
		record := captureRecord{Transport: "tcp", RemoteAddr: fmt.Sprintf("10.1.%d.%d:1", i/256, i%256), Body: []byte(fmt.Sprintf("record-%03d", i))}
		if err := m.persistMES(record); err != nil {
			t.Fatal(err)
		}
	}
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if len(files) != cfg.MESMaxFiles {
		t.Fatalf("archived captures = %d, want %d", len(files), cfg.MESMaxFiles)
	}
	oldest, err := os.ReadFile(files[0])
	if err != nil {
		t.Fatal(err)
	}
	var first captureRecord
	if err := json.Unmarshal(oldest, &first); err != nil {
		t.Fatal(err)
	}
	if string(first.Body) != "record-000" {
		t.Fatalf("oldest accepted report was lost: %q", first.Body)
	}
	if data, err := os.ReadFile(protected); err != nil || string(data) != "5173 report" {
		t.Fatalf("5173 capture was modified: %q %v", data, err)
	}
	restarted := &mesMonitor{bridge: b}
	if err := restarted.persistMES(captureRecord{Transport: "tcp", RemoteAddr: "10.2.0.1:1", Body: []byte("after-restart")}); !errors.Is(err, errMESArchiveFull) {
		t.Fatalf("full archive after restart = %v", err)
	}
	files, _ = filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if len(files) != cfg.MESMaxFiles {
		t.Fatalf("full archive changed after rejected write: %d", len(files))
	}
}

func TestMESPermanentListenerAndRateLimit(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32), MESListen: "0.0.0.0:8604"}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- b.serveMESCapture(listener, time.Time{}) }()
	defer func() { listener.Close(); <-done }()
	time.Sleep(100 * time.Millisecond)
	response, err := (&http.Client{Timeout: time.Second}).Post("http://"+listener.Addr().String()+"/httptest.php", "text/plain", strings.NewReader("permanent"))
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("permanent listener status = %d", response.StatusCode)
	}
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if len(files) != 1 {
		t.Fatalf("permanent listener saved %d files", len(files))
	}
	m := &mesMonitor{bridge: b}
	record := captureRecord{Transport: "tcp", RemoteAddr: "10.3.0.1:1234", Body: []byte("x")}
	for i := 0; i < maxMESPerIPPerMinute; i++ {
		if err := m.persistMES(record); err != nil {
			t.Fatal(err)
		}
	}
	if err := m.persistMES(record); !errors.Is(err, errMESRateLimited) {
		t.Fatalf("rate limit = %v", err)
	}
	m.rateStart = time.Now().Add(-time.Minute)
	if err := m.persistMES(record); err != nil {
		t.Fatalf("rate limit did not reset: %v", err)
	}
}

func TestMESFailedWriteDoesNotConsumeRateLimit(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(cfg.QueueDir, "mes-captures"), []byte("block directory"), 0600); err != nil {
		t.Fatal(err)
	}
	m := &mesMonitor{bridge: b}
	if err := m.persistMES(captureRecord{RemoteAddr: "10.4.0.1:1", Body: []byte("x")}); err == nil {
		t.Fatal("failed write was acknowledged")
	}
	if m.rateGlobal != 0 || m.rateByIP["10.4.0.1"] != 0 {
		t.Fatal("failed write consumed capture rate")
	}
}

func TestMESMonitorRequiresCaptureOnlyMode(t *testing.T) {
	cfg := testConfig(t)
	cfg.MESListen = "0.0.0.0:8604"
	if _, err := newBridge(cfg); err == nil {
		t.Fatal("public MES listener accepted in forwarding mode")
	}
	cfg.CaptureOnly = true
	cfg.CaptureToken = strings.Repeat("a", 32)
	if _, err := newBridge(cfg); err != nil {
		t.Fatalf("permanent capture rejected: %v", err)
	}
	cfg.MESCaptureUntil = time.Now().Add(2 * time.Hour)
	if _, err := newBridge(cfg); err == nil {
		t.Fatal("excessively long MES monitor accepted")
	}
}

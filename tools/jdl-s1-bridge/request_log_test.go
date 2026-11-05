package main

import (
	"bufio"
	"bytes"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestPublicListenerLogsEveryParsedRequestWithoutPayload(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var logs bytes.Buffer
	previous := log.Writer()
	log.SetOutput(&logs)
	defer log.SetOutput(previous)
	done := make(chan error, 1)
	go func() { done <- b.serveMESCapture(listener, time.Now().Add(5*time.Second)) }()
	defer func() { _ = listener.Close(); <-done }()

	client := &http.Client{Timeout: time.Second}
	for _, request := range []struct {
		method, path, body string
		status             int
	}{
		{http.MethodPost, "/httptest.php?key=do-not-log", "state=run", http.StatusOK},
		{http.MethodGet, basePath + "/IO/UploadDeviceStatus?key=do-not-log", "", http.StatusMethodNotAllowed},
	} {
		req, err := http.NewRequest(request.method, "http://"+listener.Addr().String()+request.path, strings.NewReader(request.body))
		if err != nil {
			t.Fatal(err)
		}
		resp, err := client.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		_, _ = io.Copy(io.Discard, resp.Body)
		_ = resp.Body.Close()
		if resp.StatusCode != request.status {
			t.Fatalf("%s response = %d, want %d", request.path, resp.StatusCode, request.status)
		}
	}
	got := logs.String()
	if !strings.Contains(got, `method="POST" path="/httptest.php" read_bytes=9 status=200`) ||
		!strings.Contains(got, `method="GET" path="/dataService/webapi/IO/UploadDeviceStatus" read_bytes=0 status=405`) {
		t.Fatalf("request log missing route, bytes or response: %s", got)
	}
	if strings.Count(got, "JDL request ") != 2 || strings.Contains(got, "do-not-log") || strings.Contains(got, "state=run") {
		t.Fatalf("request log duplicated or exposed payload: %s", got)
	}
}

func TestPrivateListenerLogsRejectedRequestWithoutToken(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	var logs bytes.Buffer
	previous := log.Writer()
	log.SetOutput(&logs)
	defer log.SetOutput(previous)
	request := httptest.NewRequest(http.MethodPost, "/jdlSave/MchnData?key=do-not-log", strings.NewReader("state=run"))
	request.RemoteAddr = "127.0.0.1:12345"
	response := httptest.NewRecorder()
	b.handler().ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("response = %d, want 403", response.Code)
	}
	got := logs.String()
	if !strings.Contains(got, `method="POST" path="/jdlSave/MchnData" read_bytes=0 status=403`) || strings.Contains(got, "do-not-log") || strings.Contains(got, "state=run") {
		t.Fatalf("private request log missing or exposed data: %s", got)
	}
}

func TestRawTCPArchiveWritesRequestLog(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	var logs bytes.Buffer
	previous := log.Writer()
	log.SetOutput(&logs)
	defer log.SetOutput(previous)
	server, client := net.Pipe()
	defer server.Close()
	go func() { _, _ = client.Write([]byte{0x02, 0x00, 'M', 'E', 'S'}); _ = client.Close() }()
	(&mesMonitor{bridge: b}).captureTCP(&mesConn{Conn: server, reader: bufio.NewReader(server)})
	if !strings.Contains(logs.String(), `JDL request transport="tcp"`) || !strings.Contains(logs.String(), `read_bytes=5 archived=true`) {
		t.Fatalf("raw TCP request missing from log: %s", logs.String())
	}
}

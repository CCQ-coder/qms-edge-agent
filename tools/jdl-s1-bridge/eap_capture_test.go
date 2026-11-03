package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func mesPost(t *testing.T, b *bridge, path, body string) *httptest.ResponseRecorder {
	t.Helper()
	r := httptest.NewRequest(http.MethodPost, path, strings.NewReader(body))
	r.Header.Set("Content-Type", "application/json; charset=utf-8")
	r.RemoteAddr = "10.1.1.1:12345"
	w := httptest.NewRecorder()
	(&mesMonitor{bridge: b}).handleHTTP(w, r)
	return w
}

func TestCaptureModeRecordsEveryDocumentedInterface(t *testing.T) {
	cases := []struct {
		path, body, response string
		status               int
	}{
		{"/Equip/MillingMachineAccessToTask", `{"AssetsCode":"E1807091137","VehicleCode":"231312323101"}`, `"IsSuccess":false`, http.StatusServiceUnavailable},
		{"/IO/UploadProcessQualityParameters", `{"AssetsCode":"E1807091137","SpRmp":100}`, `"IsSuccess":true`, http.StatusOK},
		{"/IO/UploadDeviceStatus", `{"AssetsCode":"E1807091137","S_Power":1}`, `"IsSuccess":true`, http.StatusOK},
		{"/IO/UploadParameterAdjustments", `{"AssetsCode":"E1807091137","M_SpRmp":100}`, `"IsSuccess":true`, http.StatusOK},
		{"/IO/UploadProductionCount", `{"AssetsCode":"E1807091137","C_ChgBoardCount":2}`, `"IsSuccess":true`, http.StatusOK},
		{"/Equip/UploadJdl", `{"AssetsCode":"E1807091137","Satus":1}`, `"IsSuccess":true`, http.StatusOK},
		{"/UploadAlarmBegin", `{"AssetsCode":"E1807091137","AlarmCode":2}`, `"IsSuccess":true`, http.StatusOK},
		{"/UploadAlarmEnd", `{"AssetsCode":"E1807091137","RstAlarmCode":2}`, `"IsSuccess":true`, http.StatusOK},
		{"/uploadbrokenknife", `{"TransactionId":"20261103120000123","EqpId":"E1807091137"}`, `"Code":"0"`, http.StatusOK},
	}
	for _, tc := range cases {
		t.Run(tc.path, func(t *testing.T) {
			cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
			b, err := newBridge(cfg)
			if err != nil {
				t.Fatal(err)
			}
			w := mesPost(t, b, basePath+tc.path, tc.body)
			if w.Code != tc.status || !strings.Contains(w.Body.String(), tc.response) {
				t.Fatalf("response: %d %s", w.Code, w.Body.String())
			}
			files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
			if len(files) != 1 {
				t.Fatalf("capture files = %d", len(files))
			}
			data, err := os.ReadFile(files[0])
			if err != nil {
				t.Fatal(err)
			}
			var record captureRecord
			if err := json.Unmarshal(data, &record); err != nil {
				t.Fatal(err)
			}
			if record.Path != basePath+tc.path || string(record.Body) != tc.body {
				t.Fatalf("original request changed: %+v", record)
			}
		})
	}
}

func TestInvalidDocumentRequestIsLoggedBeforeFailure(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := mesPost(t, b, basePath+"/IO/UploadDeviceStatus", `{"AssetsCode":`)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("invalid JSON status = %d", w.Code)
	}
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
	if len(files) != 1 {
		t.Fatal("invalid request was not logged")
	}
}

func TestLegacyCaptureDoesNotExposeDocumentRoutesWithoutToken(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, basePath+"/IO/UploadDeviceStatus", `{"AssetsCode":"E1807091137"}`)
	if w.Code != http.StatusNotFound {
		t.Fatalf("legacy route unexpectedly exposed: %d", w.Code)
	}
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "captures", "*.json"))
	if len(files) != 0 {
		t.Fatal("legacy capture accepted an unauthorized document route")
	}
}

func TestMESForwardsOnlyConfirmedDeviceAndSource(t *testing.T) {
	cases := []struct {
		name, path, body, remote string
		status, queued           int
	}{
		{"trusted status", "/IO/UploadDeviceStatus", `{"AssetsCode":"E1807091137","Count":9007199254740993}`, "113.195.85.167:12345", http.StatusOK, 1},
		{"wrong device", "/IO/UploadDeviceStatus", `{"AssetsCode":"OTHER","S_Power":1}`, "113.195.85.167:12345", http.StatusForbidden, 0},
		{"wrong source", "/IO/UploadDeviceStatus", `{"AssetsCode":"E1807091137","S_Power":1}`, "10.1.1.1:12345", http.StatusForbidden, 0},
		{"scan stays capture only", "/Equip/MillingMachineAccessToTask", `{"AssetsCode":"E1807091137","VehicleCode":"LOT"}`, "113.195.85.167:12345", http.StatusServiceUnavailable, 0},
		{"broken knife", "/uploadbrokenknife", `{"EqpId":"E1807091137","TransactionId":"TX1"}`, "113.195.85.167:12345", http.StatusOK, 1},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8", "113.195.85.167/32"}, SourceIDs: []string{"E1807091137"}, QueueDir: t.TempDir(), CaptureOnly: true, ForwardMES: true, CaptureToken: strings.Repeat("a", 32), MESListen: "0.0.0.0:8604", Broker: "127.0.0.1:1883", ProductID: "34", SerialNumber: "E1807091137", UserID: "1", Username: "u", Password: "p"}
			b, err := newBridge(cfg)
			if err != nil {
				t.Fatal(err)
			}
			request := httptest.NewRequest(http.MethodPost, basePath+tc.path, strings.NewReader(tc.body))
			request.RemoteAddr = tc.remote
			response := httptest.NewRecorder()
			(&mesMonitor{bridge: b}).handleHTTP(response, request)
			if response.Code != tc.status {
				t.Fatalf("response status = %d, want %d: %s", response.Code, tc.status, response.Body.String())
			}
			captures, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "mes-captures", "*.json"))
			if len(captures) != 1 {
				t.Fatalf("captures = %d", len(captures))
			}
			queued, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "*.json"))
			if len(queued) != tc.queued {
				t.Fatalf("queued messages = %d, want %d", len(queued), tc.queued)
			}
			if tc.name == "trusted status" {
				data, err := os.ReadFile(queued[0])
				if err != nil {
					t.Fatal(err)
				}
				var message queuedMessage
				if err := json.Unmarshal(data, &message); err != nil {
					t.Fatal(err)
				}
				if message.Topic != "/34/E1807091137/event/post" || string(message.Raw) != tc.body || message.Capture == nil || message.Capture.RemoteAddr != tc.remote || string(message.Capture.Body) != tc.body || !strings.Contains(string(message.Payload), "9007199254740993") {
					t.Fatalf("queued report changed: %+v", message)
				}
			}
		})
	}
}

func TestMESForwardingSurvivesFullPublicCaptureArchive(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8", "113.195.85.167/32"}, SourceIDs: []string{"E1807091137"}, QueueDir: t.TempDir(), CaptureOnly: true, ForwardMES: true, CaptureToken: strings.Repeat("a", 32), MESListen: "0.0.0.0:8604", MESMaxFiles: 1, Broker: "127.0.0.1:1883", ProductID: "34", SerialNumber: "E1807091137", UserID: "1", Username: "u", Password: "p"}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	m := &mesMonitor{bridge: b}
	if err := m.persistMES(captureRecord{RemoteAddr: "10.1.1.1:1", Body: []byte("public sample")}); err != nil {
		t.Fatal(err)
	}
	body := `{"AssetsCode":"E1807091137","S_Power":1}`
	request := httptest.NewRequest(http.MethodPost, basePath+"/IO/UploadDeviceStatus", strings.NewReader(body))
	request.RemoteAddr = "113.195.85.167:12345"
	response := httptest.NewRecorder()
	m.handleHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("trusted report blocked by public archive: %d %s", response.Code, response.Body.String())
	}
	queued, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "*.json"))
	if len(queued) != 1 {
		t.Fatalf("trusted report queued = %d", len(queued))
	}
	data, err := os.ReadFile(queued[0])
	if err != nil {
		t.Fatal(err)
	}
	var message queuedMessage
	if err := json.Unmarshal(data, &message); err != nil {
		t.Fatal(err)
	}
	if message.Capture == nil || string(message.Capture.Body) != body || message.Capture.Path != basePath+"/IO/UploadDeviceStatus" {
		t.Fatalf("full report missing from durable queue: %+v", message.Capture)
	}
}

func TestMESForwardQueueFailureDoesNotAcknowledge(t *testing.T) {
	if os.Geteuid() == 0 {
		t.Skip("root bypasses directory write permissions")
	}
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8", "113.195.85.167/32"}, SourceIDs: []string{"E1807091137"}, QueueDir: t.TempDir(), CaptureOnly: true, ForwardMES: true, CaptureToken: strings.Repeat("a", 32), MESListen: "0.0.0.0:8604", Broker: "127.0.0.1:1883", ProductID: "34", SerialNumber: "E1807091137", UserID: "1", Username: "u", Password: "p"}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(cfg.QueueDir, "mes-captures"), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(cfg.QueueDir, 0500); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(cfg.QueueDir, 0700) })
	request := httptest.NewRequest(http.MethodPost, basePath+"/IO/UploadDeviceStatus", strings.NewReader(`{"AssetsCode":"E1807091137","S_Power":1}`))
	request.RemoteAddr = "113.195.85.167:12345"
	response := httptest.NewRecorder()
	(&mesMonitor{bridge: b}).handleHTTP(response, request)
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("failed queue was acknowledged: %d", response.Code)
	}
}

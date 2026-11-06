package main

import (
	"encoding/json"
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
	"unicode/utf8"
)

func testConfig(t *testing.T) config {
	t.Helper()
	return config{
		Listen:       "127.0.0.1:7666",
		AllowedCIDRs: []string{"127.0.0.1/8"},
		SourceIDs:    []string{"E1030ROUT017", "eq01"},
		QueueDir:     t.TempDir(),
		Broker:       "127.0.0.1:1883",
		ProductID:    "88",
		SerialNumber: "JDL001",
		UserID:       "9",
		Username:     "device-user",
		Password:     "device-password",
	}
}

func post(t *testing.T, b *bridge, path, body string) *httptest.ResponseRecorder {
	t.Helper()
	r := httptest.NewRequest(http.MethodPost, path, strings.NewReader(body))
	r.Header.Set("Content-Type", "application/json; charset=utf-8")
	r.RemoteAddr = "127.0.0.1:12345"
	w := httptest.NewRecorder()
	b.handler().ServeHTTP(w, r)
	return w
}

func queued(t *testing.T, dir string) []queuedMessage {
	t.Helper()
	files, err := filepath.Glob(filepath.Join(dir, "*.json"))
	if err != nil {
		t.Fatal(err)
	}
	var messages []queuedMessage
	for _, file := range files {
		data, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		var message queuedMessage
		if err := json.Unmarshal(data, &message); err != nil {
			t.Fatal(err)
		}
		messages = append(messages, message)
	}
	return messages
}

func TestUploadPersistsFieldsBeforeSuccess(t *testing.T) {
	cfg := testConfig(t)
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, "/dataService/webapi/IO/UploadProcessQualityParameters", `{"AssetsCode":"E1030ROUT017","SpRmp":10,"LotCode":"114-SO20211106003-05-006-000","TimeStamp":"2021-12-30 08:00:00"}`)
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), `"IsSuccess":true`) {
		t.Fatalf("response: %d %s", w.Code, w.Body.String())
	}
	messages := queued(t, cfg.QueueDir)
	if len(messages) != 1 {
		t.Fatalf("queued messages = %d", len(messages))
	}
	if messages[0].Topic != "/88/JDL001/event/post" {
		t.Fatalf("topic = %s", messages[0].Topic)
	}
	if !strings.Contains(string(messages[0].Raw), `"SpRmp":10`) {
		t.Fatalf("original payload missing: %s", messages[0].Raw)
	}
	var items []eventItem
	if err := json.Unmarshal(messages[0].Payload, &items); err != nil {
		t.Fatal(err)
	}
	if len(items) != 4 {
		t.Fatalf("items = %d", len(items))
	}
	found := false
	for _, item := range items {
		var meta eventRemark
		if err := json.Unmarshal([]byte(item.Remark), &meta); err != nil {
			t.Fatal(err)
		}
		if meta.Field == "SpRmp" {
			found = item.ID == "jdl_quality" && item.Value == "10" && meta.Kind == "number" && meta.Part == 1 && meta.Total == 1 && meta.Batch != ""
		}
	}
	if !found {
		t.Fatalf("SpRmp not preserved as a typed S1 event: %+v", items)
	}
}

func TestPropertyRoutesKeepEventAndAlarmRoutesDoNotAddProperties(t *testing.T) {
	for _, tc := range []struct {
		path         string
		body         string
		wantProperty bool
	}{
		{"/IO/UploadProcessQualityParameters", `{"AssetsCode":"E1030ROUT017","SpRmp":10}`, true},
		{"/IO/UploadDeviceStatus", `{"AssetsCode":"E1030ROUT017","S_Power":1}`, false},
		{"/IO/UploadParameterAdjustments", `{"AssetsCode":"E1030ROUT017","M_SpRmp":10}`, false},
		{"/IO/UploadProductionCount", `{"AssetsCode":"E1030ROUT017","C_ChgBoardCount":2}`, false},
		{"/Equip/UploadJdl", `{"AssetsCode":"E1030ROUT017","Jdl":99.5}`, true},
		{"/UploadAlarmBegin", `{"AssetsCode":"E1030ROUT017","AlarmCode":"2"}`, false},
		{"/UploadAlarmEnd", `{"AssetsCode":"E1030ROUT017","RstAlarmCode":"2"}`, false},
		{"/uploadbrokenknife", `{"EqpId":"eq01","TransactionId":"123","Msg":"broken"}`, false},
	} {
		t.Run(tc.path, func(t *testing.T) {
			cfg := testConfig(t)
			b, err := newBridge(cfg)
			if err != nil {
				t.Fatal(err)
			}
			if w := post(t, b, basePath+tc.path, tc.body); w.Code != http.StatusOK {
				t.Fatalf("response = %d: %s", w.Code, w.Body.String())
			}
			messages := queued(t, cfg.QueueDir)
			if len(messages) != 1 || len(messages[0].PropertyPayload) > 0 != tc.wantProperty || messages[0].Topic != "/88/JDL001/event/post" {
				t.Fatalf("queued route %s = %+v", tc.path, messages)
			}
		})
	}
}

func TestBrokenKnifeUsesItsOwnReplyAndDeviceID(t *testing.T) {
	cfg := testConfig(t)
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, "/dataService/webapi/uploadbrokenknife", `{"TransactionId":"20230817084225666","EqpId":"eq01","Msg":"机台报警","EqpType":"1"}`)
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), `"TransactionId":"20230817084225666"`) || !strings.Contains(w.Body.String(), `"Code":"0"`) {
		t.Fatalf("response: %d %s", w.Code, w.Body.String())
	}
	if len(queued(t, cfg.QueueDir)) != 1 {
		t.Fatal("broken knife report was not queued")
	}
}

func TestEveryDocumentedUploadRouteIsAccepted(t *testing.T) {
	cases := []string{
		"/IO/UploadProcessQualityParameters",
		"/IO/UploadDeviceStatus",
		"/IO/UploadParameterAdjustments",
		"/IO/UploadProductionCount",
		"/Equip/UploadJdl",
		"/UploadAlarmBegin",
		"/UploadAlarmEnd",
	}
	for _, path := range cases {
		t.Run(path, func(t *testing.T) {
			cfg := testConfig(t)
			b, err := newBridge(cfg)
			if err != nil {
				t.Fatal(err)
			}
			w := post(t, b, basePath+path, `{"AssetsCode":"E1030ROUT017","S_POWER":1}`)
			if w.Code != http.StatusOK || len(queued(t, cfg.QueueDir)) != 1 {
				t.Fatalf("route %s: %d %s", path, w.Code, w.Body.String())
			}
		})
	}
}

func TestLongChineseValueIsSplitWithoutLoss(t *testing.T) {
	cfg := testConfig(t)
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	long := strings.Repeat("断刀报警", 20)
	w := post(t, b, "/dataService/webapi/UploadAlarmBegin", `{"AssetsCode":"E1030ROUT017","AlarmCode":"2","Msg":"`+long+`"}`)
	if w.Code != http.StatusOK {
		t.Fatalf("response: %d %s", w.Code, w.Body.String())
	}
	var items []eventItem
	if err := json.Unmarshal(queued(t, cfg.QueueDir)[0].Payload, &items); err != nil {
		t.Fatal(err)
	}
	var parts []string
	var batch string
	for _, item := range items {
		var meta eventRemark
		if err := json.Unmarshal([]byte(item.Remark), &meta); err != nil {
			t.Fatal(err)
		}
		if utf8.RuneCountInString(item.Value) > 64 || utf8.RuneCountInString(item.Remark) > 200 {
			t.Fatalf("S1 column limit exceeded: %+v", item)
		}
		if meta.Field == "Msg" {
			if item.ID != "jdl_alarm_begin" || meta.Total != 2 || meta.Part != len(parts)+1 {
				t.Fatalf("invalid chunk metadata: %+v %+v", item, meta)
			}
			if batch != "" && meta.Batch != batch {
				t.Fatal("chunks have different batches")
			}
			batch = meta.Batch
			parts = append(parts, item.Value)
		}
	}
	if strings.Join(parts, "") != long {
		t.Fatalf("reconstructed message = %q", strings.Join(parts, ""))
	}
}

func TestRejectsUntrustedOrInvalidRequests(t *testing.T) {
	cases := []struct {
		name, path, body, remote, method, contentType string
		status                                        int
	}{
		{"bad-json", "/dataService/webapi/IO/UploadDeviceStatus", `{"AssetsCode":`, "127.0.0.1:1", "POST", "application/json", 400},
		{"wrong-device", "/dataService/webapi/IO/UploadDeviceStatus", `{"AssetsCode":"OTHER"}`, "127.0.0.1:1", "POST", "application/json", 403},
		{"wrong-ip", "/dataService/webapi/IO/UploadDeviceStatus", `{"AssetsCode":"E1030ROUT017"}`, "10.0.0.1:1", "POST", "application/json", 403},
		{"wrong-method", "/dataService/webapi/IO/UploadDeviceStatus", `{"AssetsCode":"E1030ROUT017"}`, "127.0.0.1:1", "GET", "application/json", 405},
		{"wrong-type", "/dataService/webapi/IO/UploadDeviceStatus", `{"AssetsCode":"E1030ROUT017"}`, "127.0.0.1:1", "POST", "text/plain", 415},
		{"unknown-route", "/dataService/webapi/IO/Other", `{"AssetsCode":"E1030ROUT017"}`, "127.0.0.1:1", "POST", "application/json", 404},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			cfg := testConfig(t)
			b, err := newBridge(cfg)
			if err != nil {
				t.Fatal(err)
			}
			r := httptest.NewRequest(tc.method, tc.path, strings.NewReader(tc.body))
			r.RemoteAddr = tc.remote
			r.Header.Set("Content-Type", tc.contentType)
			w := httptest.NewRecorder()
			b.handler().ServeHTTP(w, r)
			if w.Code != tc.status {
				t.Fatalf("status = %d, want %d; body=%s", w.Code, tc.status, w.Body.String())
			}
			if len(queued(t, cfg.QueueDir)) != 0 {
				t.Fatal("invalid request was queued")
			}
		})
	}
}

func TestQueueFailureCannotAckSuccess(t *testing.T) {
	cfg := testConfig(t)
	file := filepath.Join(t.TempDir(), "file")
	if err := os.WriteFile(file, []byte("x"), 0600); err != nil {
		t.Fatal(err)
	}
	cfg.QueueDir = filepath.Join(file, "queue")
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, "/dataService/webapi/UploadAlarmEnd", `{"AssetsCode":"E1030ROUT017","RstAlarmCode":"5"}`)
	if w.Code != http.StatusServiceUnavailable || strings.Contains(w.Body.String(), `"IsSuccess":true`) {
		t.Fatalf("response: %d %s", w.Code, w.Body.String())
	}
}

func TestScanWithoutTaskSourceFailsClosed(t *testing.T) {
	cfg := testConfig(t)
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, "/dataService/webapi/Equip/MillingMachineAccessToTask", `{"AssetsCode":"E1030ROUT017","VehicleCode":"231312323101","LogonQty":"100"}`)
	if w.Code != http.StatusServiceUnavailable || len(queued(t, cfg.QueueDir)) != 0 {
		t.Fatalf("scan response: %d %s", w.Code, w.Body.String())
	}
}

func TestRejectsPublicPlaintextEndpoints(t *testing.T) {
	cfg := testConfig(t)
	cfg.Broker = "106.227.69.144:1883"
	if _, err := newBridge(cfg); err == nil {
		t.Fatal("public plaintext MQTT broker accepted")
	}
	cfg = testConfig(t)
	cfg.Listen = "0.0.0.0:7666"
	if _, err := newBridge(cfg); err == nil {
		t.Fatal("public plaintext HTTP listener accepted")
	}
}

func TestCaptureModeStoresUnknownVendorReport(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	raw := []byte("machine=JDL-1&status=running&count=7")
	r := httptest.NewRequest(http.MethodPost, "/jdlSave/MchnData?key="+cfg.CaptureToken, strings.NewReader(string(raw)))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	r.RemoteAddr = "127.0.0.1:12345"
	w := httptest.NewRecorder()
	b.handler().ServeHTTP(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("capture response: %d %s", w.Code, w.Body.String())
	}
	files, err := filepath.Glob(filepath.Join(cfg.QueueDir, "captures", "*.json"))
	if err != nil || len(files) != 1 {
		t.Fatalf("capture files: %v %v", files, err)
	}
	data, err := os.ReadFile(files[0])
	if err != nil {
		t.Fatal(err)
	}
	var captured captureRecord
	if err := json.Unmarshal(data, &captured); err != nil {
		t.Fatal(err)
	}
	if string(captured.Body) != string(raw) || captured.ContentType != "application/x-www-form-urlencoded" || captured.Path != "/jdlSave/MchnData" {
		t.Fatalf("capture changed the report: %+v", captured)
	}
}

func TestCaptureModeRejectsMissingToken(t *testing.T) {
	cfg := config{Listen: "127.0.0.1:7666", AllowedCIDRs: []string{"127.0.0.1/8"}, QueueDir: t.TempDir(), CaptureOnly: true, CaptureToken: strings.Repeat("a", 32)}
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	r := httptest.NewRequest(http.MethodPost, "/jdlSave/MchnData", strings.NewReader(`{"status":1}`))
	r.RemoteAddr = "127.0.0.1:12345"
	w := httptest.NewRecorder()
	b.handler().ServeHTTP(w, r)
	if w.Code != http.StatusForbidden {
		t.Fatalf("missing token status = %d", w.Code)
	}
	files, _ := filepath.Glob(filepath.Join(cfg.QueueDir, "captures", "*.json"))
	if len(files) != 0 {
		t.Fatal("unauthorized payload captured")
	}
}

type brokerResult struct {
	clientID string
	username string
	password string
	topic    string
	payload  string
	err      error
}

func fakeBroker(t *testing.T, ackHeader byte) (string, <-chan brokerResult) {
	return fakeBrokerN(t, []byte{ackHeader})
}

func fakeBrokerN(t *testing.T, ackHeaders []byte) (string, <-chan brokerResult) {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	result := make(chan brokerResult, len(ackHeaders))
	go func() {
		defer listener.Close()
		for _, ackHeader := range ackHeaders {
			result <- acceptBrokerReport(listener, ackHeader)
		}
	}()
	return listener.Addr().String(), result
}

func acceptBrokerReport(listener net.Listener, ackHeader byte) (got brokerResult) {
	conn, err := listener.Accept()
	if err != nil {
		got.err = err
		return got
	}
	defer conn.Close()
	conn.SetDeadline(time.Now().Add(3 * time.Second))
	header, body, err := readTestPacket(conn)
	if err != nil {
		got.err = err
		return got
	}
	if header != 0x10 {
		got.err = fmt.Errorf("CONNECT header = %x", header)
		return got
	}
	i := 0
	protocol := readTestText(body, &i)
	if protocol != "MQTT" || body[i] != 4 {
		got.err = fmt.Errorf("protocol = %s version %d", protocol, body[i])
		return got
	}
	i++
	flags := body[i]
	i += 3
	got.clientID = readTestText(body, &i)
	if flags&0x80 != 0 {
		got.username = readTestText(body, &i)
	}
	if flags&0x40 != 0 {
		got.password = readTestText(body, &i)
	}
	if _, err = conn.Write([]byte{0x20, 0x02, 0x00, 0x00}); err != nil {
		got.err = err
		return got
	}
	header, body, err = readTestPacket(conn)
	if err != nil {
		got.err = err
		return got
	}
	if header != 0x32 {
		got.err = fmt.Errorf("PUBLISH header = %x, want QoS1", header)
		return got
	}
	i = 0
	got.topic = readTestText(body, &i)
	packetID := body[i : i+2]
	i += 2
	got.payload = string(body[i:])
	if ackHeader != 0 {
		_, got.err = conn.Write([]byte{ackHeader, 0x02, packetID[0], packetID[1]})
	}
	return got
}

func readTestPacket(r io.Reader) (byte, []byte, error) {
	var first [1]byte
	if _, err := io.ReadFull(r, first[:]); err != nil {
		return 0, nil, err
	}
	length, multiplier := 0, 1
	for i := 0; i < 4; i++ {
		var digit [1]byte
		if _, err := io.ReadFull(r, digit[:]); err != nil {
			return 0, nil, err
		}
		length += int(digit[0]&127) * multiplier
		if digit[0]&128 == 0 {
			body := make([]byte, length)
			_, err := io.ReadFull(r, body)
			return first[0], body, err
		}
		multiplier *= 128
	}
	return 0, nil, fmt.Errorf("invalid MQTT length")
}

func readTestText(data []byte, index *int) string {
	length := int(data[*index])<<8 | int(data[*index+1])
	*index += 2
	value := string(data[*index : *index+length])
	*index += length
	return value
}

func TestMQTTPubAckFastBeeCompatibility(t *testing.T) {
	for _, tc := range []struct {
		name      string
		ackHeader byte
		wantError bool
	}{
		{"fastbee", 0x42, false},
		{"invalid", 0x43, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			cfg := testConfig(t)
			address, received := fakeBroker(t, tc.ackHeader)
			cfg.Broker = address
			message := queuedMessage{Topic: "/" + cfg.ProductID + "/" + cfg.SerialNumber + "/event/post", Payload: json.RawMessage(`[]`)}
			err := publishMQTT(cfg, message)
			if (err != nil) != tc.wantError {
				t.Fatalf("publishMQTT error = %v, wantError = %t", err, tc.wantError)
			}
			if got := <-received; got.err != nil {
				t.Fatal(got.err)
			}
		})
	}
}

func TestMQTTPubAckRemovesQueuedReport(t *testing.T) {
	cfg := testConfig(t)
	address, result := fakeBrokerN(t, []byte{0x40, 0x40})
	cfg.Broker = address
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, "/dataService/webapi/Equip/UploadJdl", `{"AssetsCode":"E1030ROUT017","Jdl":1}`)
	if w.Code != http.StatusOK {
		t.Fatalf("response: %d %s", w.Code, w.Body.String())
	}
	restarted, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.flushOnce(); err != nil {
		t.Fatal(err)
	}
	if len(queued(t, cfg.QueueDir)) != 0 {
		t.Fatal("PUBACKed report stayed in queue")
	}
	archived := queued(t, filepath.Join(cfg.QueueDir, "sent"))
	if len(archived) != 1 || !strings.Contains(string(archived[0].Raw), `"Jdl":1`) {
		t.Fatalf("acknowledged raw report not archived: %+v", archived)
	}
	for _, topic := range []string{"/88/JDL001/property/post", "/88/JDL001/event/post"} {
		select {
		case got := <-result:
			if got.err != nil {
				t.Fatal(got.err)
			}
			if got.clientID != "S&JDL001&88&9" || got.username != "device-user" || got.password != "device-password" {
				t.Fatalf("MQTT credentials: %+v", got)
			}
			var items []eventItem
			if err := json.Unmarshal([]byte(got.payload), &items); err != nil {
				t.Fatal(err)
			}
			found := false
			for _, item := range items {
				if topic == "/88/JDL001/property/post" {
					found = found || item.ID == "Jdl" && item.Value == "1"
				} else {
					var meta eventRemark
					if err := json.Unmarshal([]byte(item.Remark), &meta); err != nil {
						t.Fatal(err)
					}
					found = found || meta.Field == "Jdl" && item.Value == "1"
				}
			}
			if got.topic != topic || !found {
				t.Fatalf("MQTT message: %+v", got)
			}
		case <-time.After(3 * time.Second):
			t.Fatal("broker did not receive publish")
		}
	}
}

func TestMissingPubAckRetainsQueuedReport(t *testing.T) {
	cfg := testConfig(t)
	address, _ := fakeBroker(t, 0)
	cfg.Broker = address
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, "/dataService/webapi/UploadAlarmBegin", `{"AssetsCode":"E1030ROUT017","AlarmCode":"2"}`)
	if w.Code != http.StatusOK {
		t.Fatalf("response: %d %s", w.Code, w.Body.String())
	}
	if err := b.flushOnce(); err == nil {
		t.Fatal("missing PUBACK was accepted")
	}
	if len(queued(t, cfg.QueueDir)) != 1 {
		t.Fatal("unacknowledged report was lost")
	}
}

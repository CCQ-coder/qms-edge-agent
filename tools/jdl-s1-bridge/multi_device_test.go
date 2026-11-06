package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func multiDeviceConfig(t *testing.T) config {
	t.Helper()
	var c config
	if err := json.Unmarshal([]byte(`{
		"listen":"127.0.0.1:7666",
		"mes_listen":"0.0.0.0:8604",
		"allowed_cidrs":["10.1.1.1/32"],
		"capture_only":true,
		"forward_mes":true,
		"capture_token":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		"broker":"127.0.0.1:1883",
		"product_id":"34",
		"mqtt_devices":{
			"JDLR0006":{"serial_number":"JDLR0006","user_id":"11","username":"user-6","password":"password-6"},
			"JDLR0007":{"serial_number":"JDLR0007","user_id":"12","username":"user-7","password":"password-7"}
		}
	}`), &c); err != nil {
		t.Fatal(err)
	}
	c.QueueDir = t.TempDir()
	return c
}

func postMultiDevice(t *testing.T, b *bridge, sourceID string, wantStatus int) {
	t.Helper()
	w := mesPost(t, b, "/Equip/UploadJdl", `{"AssetsCode":"`+sourceID+`","Jdl":7}`)
	if w.Code != wantStatus {
		t.Fatalf("%s status = %d, want %d: %s", sourceID, w.Code, wantStatus, w.Body.String())
	}
}

func TestMultiDeviceQueueAndRestartUseSeparateMQTTIdentities(t *testing.T) {
	cfg := multiDeviceConfig(t)
	address, reports := fakeBrokerN(t, []byte{0x40, 0x40, 0x40, 0x40})
	cfg.Broker = address
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	postMultiDevice(t, b, "JDLR0006", 200)
	postMultiDevice(t, b, "JDLR0007", 200)
	postMultiDevice(t, b, "UNKNOWN", 403)

	files, err := filepath.Glob(filepath.Join(cfg.QueueDir, "*.json"))
	if err != nil || len(files) != 2 {
		t.Fatalf("queued files = %d, %v", len(files), err)
	}
	for _, file := range files {
		data, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		var stored struct {
			SourceID string `json:"source_id"`
			Topic    string `json:"topic"`
		}
		if err := json.Unmarshal(data, &stored); err != nil {
			t.Fatal(err)
		}
		if stored.SourceID == "" || stored.Topic != "/34/"+stored.SourceID+"/event/post" {
			t.Fatalf("queue identity mismatch: %+v", stored)
		}
	}

	restarted, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.flushOnce(); err != nil {
		t.Fatal(err)
	}
	if len(queued(t, cfg.QueueDir)) != 0 || len(queued(t, filepath.Join(cfg.QueueDir, "sent"))) != 2 {
		t.Fatal("restarted bridge did not archive both reports")
	}
	seen := make(map[string]bool)
	for i := 0; i < 4; i++ {
		select {
		case got := <-reports:
			if got.err != nil {
				t.Fatal(got.err)
			}
			id := strings.Split(got.topic, "/")[2]
			want := map[string]struct{ clientID, username, password string }{
				"JDLR0006": {"S&JDLR0006&34&11", "user-6", "password-6"},
				"JDLR0007": {"S&JDLR0007&34&12", "user-7", "password-7"},
			}[id]
			if seen[got.topic] || got.clientID != want.clientID || got.username != want.username || got.password != want.password {
				t.Fatalf("wrong MQTT identity for %s: %+v", id, got)
			}
			seen[got.topic] = true
		case <-time.After(3 * time.Second):
			t.Fatal("broker did not receive both reports")
		}
	}
	for _, id := range []string{"JDLR0006", "JDLR0007"} {
		for _, kind := range []string{"property", "event"} {
			if !seen["/34/"+id+"/"+kind+"/post"] {
				t.Fatalf("missing %s report for %s", kind, id)
			}
		}
	}
}

func TestMultiDevicePublishFailureDoesNotBlockOtherDevice(t *testing.T) {
	cfg := multiDeviceConfig(t)
	address, reports := fakeBrokerN(t, []byte{0, 0x40, 0x40})
	cfg.Broker = address
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	postMultiDevice(t, b, "JDLR0006", 200)
	postMultiDevice(t, b, "JDLR0006", 200)
	postMultiDevice(t, b, "JDLR0007", 200)
	if err := b.flushOnce(); err == nil {
		t.Fatal("missing PUBACK was accepted")
	}
	if len(queued(t, cfg.QueueDir)) != 2 || len(queued(t, filepath.Join(cfg.QueueDir, "sent"))) != 1 {
		t.Fatal("one failed device blocked the other")
	}
	for i, topic := range []string{"/34/JDLR0006/property/post", "/34/JDLR0007/property/post", "/34/JDLR0007/event/post"} {
		select {
		case got := <-reports:
			if got.err != nil {
				t.Fatal(got.err)
			}
			if got.topic != topic {
				t.Fatalf("broker report %d topic = %s", i, got.topic)
			}
		case <-time.After(3 * time.Second):
			t.Fatal("broker did not receive the second device")
		}
	}
}

func TestPropertyAndEventAckRequiredBeforeArchive(t *testing.T) {
	cfg := multiDeviceConfig(t)
	address, reports := fakeBrokerN(t, []byte{0x40, 0, 0x40, 0x40})
	cfg.Broker = address
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	postMultiDevice(t, b, "JDLR0006", 200)
	stored := queued(t, cfg.QueueDir)
	if len(stored) != 1 || len(stored[0].PropertyPayload) == 0 {
		t.Fatalf("property payload missing from durable queue: %+v", stored)
	}
	var properties []eventItem
	if err := json.Unmarshal(stored[0].PropertyPayload, &properties); err != nil {
		t.Fatal(err)
	}
	if len(properties) != 2 || properties[0].ID != "AssetsCode" || properties[0].Value != "JDLR0006" || properties[1].ID != "Jdl" || properties[1].Value != "7" {
		t.Fatalf("raw fields were not preserved as properties: %+v", properties)
	}
	if err := b.flushOnce(); err == nil {
		t.Fatal("missing event PUBACK was accepted")
	}
	if len(queued(t, cfg.QueueDir)) != 1 || len(queued(t, filepath.Join(cfg.QueueDir, "sent"))) != 0 {
		t.Fatal("report archived before both topics were acknowledged")
	}
	if err := b.flushOnce(); err != nil {
		t.Fatal(err)
	}
	if len(queued(t, cfg.QueueDir)) != 0 || len(queued(t, filepath.Join(cfg.QueueDir, "sent"))) != 1 {
		t.Fatal("fully acknowledged report stayed in queue")
	}
	for i, topic := range []string{"/34/JDLR0006/property/post", "/34/JDLR0006/event/post", "/34/JDLR0006/property/post", "/34/JDLR0006/event/post"} {
		select {
		case got := <-reports:
			if got.err != nil || got.topic != topic {
				t.Fatalf("publish %d = %+v, want %s", i, got, topic)
			}
		case <-time.After(3 * time.Second):
			t.Fatalf("missing publish %d", i)
		}
	}
}

func TestMultiDeviceQueueRejectsCrossDeviceTopic(t *testing.T) {
	cfg := multiDeviceConfig(t)
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	postMultiDevice(t, b, "JDLR0006", 200)
	files, err := filepath.Glob(filepath.Join(cfg.QueueDir, "*.json"))
	if err != nil || len(files) != 1 {
		t.Fatalf("queued files = %d, %v", len(files), err)
	}
	data, err := os.ReadFile(files[0])
	if err != nil {
		t.Fatal(err)
	}
	var message map[string]interface{}
	if err := json.Unmarshal(data, &message); err != nil {
		t.Fatal(err)
	}
	message["topic"] = "/34/JDLR0007/event/post"
	data, _ = json.Marshal(message)
	if err := os.WriteFile(files[0], data, 0600); err != nil {
		t.Fatal(err)
	}
	if err := b.flushOnce(); err == nil || !strings.Contains(err.Error(), "topic") {
		t.Fatalf("cross-device queue topic accepted: %v", err)
	}
	if len(queued(t, cfg.QueueDir)) != 1 {
		t.Fatal("cross-device report was removed from queue")
	}
}

func TestMultiDeviceRejectsCrossDeviceConfig(t *testing.T) {
	cfg := multiDeviceConfig(t)
	wrong := cfg.MQTTDevices["JDLR0006"]
	wrong.SerialNumber = "JDLR0007"
	cfg.MQTTDevices["JDLR0006"] = wrong
	if _, err := newBridge(cfg); err == nil || !strings.Contains(err.Error(), "serial_number") {
		t.Fatalf("cross-device MQTT mapping accepted: %v", err)
	}
}

func TestMultiDeviceChecksArchiveBeforePublishing(t *testing.T) {
	cfg := multiDeviceConfig(t)
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	postMultiDevice(t, b, "JDLR0006", 200)
	if err := os.WriteFile(filepath.Join(cfg.QueueDir, "sent"), []byte("blocked"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := b.flushOnce(); err == nil || !strings.Contains(err.Error(), "sent") {
		t.Fatalf("archive failure was not checked before MQTT: %v", err)
	}
	if len(queued(t, cfg.QueueDir)) != 1 {
		t.Fatal("report changed after archive preflight failure")
	}
}

func TestLegacyQueueWithoutSourceIDStillReplays(t *testing.T) {
	cfg := testConfig(t)
	address, reports := fakeBroker(t, 0x40)
	cfg.Broker = address
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	w := post(t, b, basePath+"/IO/UploadDeviceStatus", `{"AssetsCode":"E1030ROUT017","S_Power":1}`)
	if w.Code != 200 {
		t.Fatalf("legacy status = %d: %s", w.Code, w.Body.String())
	}
	files, err := filepath.Glob(filepath.Join(cfg.QueueDir, "*.json"))
	if err != nil || len(files) != 1 {
		t.Fatalf("queued files = %d, %v", len(files), err)
	}
	data, err := os.ReadFile(files[0])
	if err != nil {
		t.Fatal(err)
	}
	var stored map[string]interface{}
	if err := json.Unmarshal(data, &stored); err != nil {
		t.Fatal(err)
	}
	delete(stored, "source_id")
	delete(stored, "property_payload")
	data, _ = json.Marshal(stored)
	if err := os.WriteFile(files[0], data, 0600); err != nil {
		t.Fatal(err)
	}
	restarted, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err := restarted.flushOnce(); err != nil {
		t.Fatal(err)
	}
	if len(queued(t, cfg.QueueDir)) != 0 || len(queued(t, filepath.Join(cfg.QueueDir, "sent"))) != 1 {
		t.Fatal("legacy queued report was not archived")
	}
	select {
	case got := <-reports:
		if got.err != nil || got.clientID != "S&JDL001&88&9" || got.topic != "/88/JDL001/event/post" {
			t.Fatalf("legacy report changed identity: %+v", got)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("legacy report was not published")
	}
}

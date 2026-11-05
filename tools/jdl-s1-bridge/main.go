package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"mime"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"time"
	"unicode/utf8"
)

const basePath = "/dataService/webapi"
const maxRequestBytes = 64 << 10

type config struct {
	Listen          string    `json:"listen"`
	AllowedCIDRs    []string  `json:"allowed_cidrs"`
	SourceIDs       []string  `json:"source_ids"`
	QueueDir        string    `json:"queue_dir"`
	CaptureOnly     bool      `json:"capture_only"`
	ForwardMES      bool      `json:"forward_mes"`
	CaptureToken    string    `json:"capture_token"`
	MESListen       string    `json:"mes_listen"`
	MESCaptureUntil time.Time `json:"mes_capture_until"`
	MESMaxFiles     int       `json:"mes_max_files"`
	Broker          string    `json:"broker"`
	ProductID       string    `json:"product_id"`
	SerialNumber    string    `json:"serial_number"`
	UserID          string    `json:"user_id"`
	Username        string    `json:"username"`
	Password        string    `json:"password"`
}

type route struct {
	name      string
	deviceKey string
	broken    bool
}

var routes = map[string]route{
	"/IO/UploadProcessQualityParameters": {"quality", "AssetsCode", false},
	"/IO/UploadDeviceStatus":             {"status", "AssetsCode", false},
	"/IO/UploadParameterAdjustments":     {"adjustment", "AssetsCode", false},
	"/IO/UploadProductionCount":          {"production_count", "AssetsCode", false},
	"/Equip/UploadJdl":                   {"utilization", "AssetsCode", false},
	"/UploadAlarmBegin":                  {"alarm_begin", "AssetsCode", false},
	"/UploadAlarmEnd":                    {"alarm_end", "AssetsCode", false},
	"/uploadbrokenknife":                 {"broken_knife", "EqpId", true},
}

type bridge struct {
	config  config
	allowed []*net.IPNet
	devices map[string]bool
	wake    chan struct{}
}

type queuedMessage struct {
	Topic   string          `json:"topic"`
	Payload json.RawMessage `json:"payload"`
	Raw     json.RawMessage `json:"raw"`
	Capture *captureRecord  `json:"capture,omitempty"`
}

type captureRecord struct {
	Transport      string      `json:"transport,omitempty"`
	Path           string      `json:"path"`
	Method         string      `json:"method,omitempty"`
	Query          string      `json:"query,omitempty"`
	Headers        http.Header `json:"headers,omitempty"`
	ContentType    string      `json:"content_type"`
	RemoteAddr     string      `json:"remote_addr"`
	ForwardedFor   string      `json:"forwarded_for,omitempty"`
	ReceivedAt     string      `json:"received_at"`
	Body           []byte      `json:"body"`
	ReadTimes      []string    `json:"read_times,omitempty"`
	Truncated      bool        `json:"truncated,omitempty"`
	ResponseStatus int         `json:"response_status,omitempty"`
}

type eventItem struct {
	ID     string `json:"id"`
	Value  string `json:"value"`
	TS     string `json:"ts"`
	Remark string `json:"remark"`
}

type eventRemark struct {
	Batch string `json:"batch"`
	Field string `json:"field"`
	Kind  string `json:"kind"`
	Part  int    `json:"part"`
	Total int    `json:"total"`
}

func newBridge(c config) (*bridge, error) {
	listenHost, _, err := net.SplitHostPort(c.Listen)
	if err != nil {
		return nil, fmt.Errorf("listen: %w", err)
	}
	if listenIP := net.ParseIP(listenHost); listenIP == nil || !listenIP.IsLoopback() {
		return nil, errors.New("plaintext HTTP must use a numeric loopback address; expose it only through HTTPS or a private tunnel")
	}
	if c.QueueDir == "" || len(c.AllowedCIDRs) == 0 {
		return nil, errors.New("queue_dir and allowed_cidrs are required")
	}
	if c.CaptureOnly {
		if !regexp.MustCompile(`^[A-Za-z0-9_-]{32,}$`).MatchString(c.CaptureToken) {
			return nil, errors.New("capture_token must have at least 32 URL-safe characters")
		}
	}
	if !c.CaptureOnly || c.ForwardMES {
		brokerHost, _, err := net.SplitHostPort(c.Broker)
		if err != nil {
			return nil, fmt.Errorf("broker: %w", err)
		}
		if brokerIP := net.ParseIP(brokerHost); brokerIP == nil || !brokerIP.IsLoopback() {
			return nil, errors.New("plaintext MQTT must use a numeric loopback broker address")
		}
		if c.Username == "" || c.Password == "" || !regexp.MustCompile(`^[0-9]+$`).MatchString(c.ProductID) || !regexp.MustCompile(`^[0-9]+$`).MatchString(c.UserID) || !regexp.MustCompile(`^[A-Za-z0-9]{1,32}$`).MatchString(c.SerialNumber) || len(c.SourceIDs) == 0 {
			return nil, errors.New("source_ids and S1 MQTT product, device and credentials are required")
		}
	}
	if c.MESListen != "" {
		if !c.CaptureOnly {
			return nil, errors.New("mes_listen requires capture_only")
		}
		host, _, err := net.SplitHostPort(c.MESListen)
		if err != nil || net.ParseIP(host) == nil {
			return nil, errors.New("mes_listen requires a numeric IP address and port")
		}
		if time.Until(c.MESCaptureUntil) > 30*time.Minute {
			return nil, errors.New("mes_capture_until cannot be more than 30 minutes ahead")
		}
		if c.MESMaxFiles < 0 || c.MESMaxFiles > 100000 {
			return nil, errors.New("mes_max_files must be between 1 and 100000 when set")
		}
	} else if !c.MESCaptureUntil.IsZero() {
		return nil, errors.New("mes_capture_until requires mes_listen")
	}
	if c.ForwardMES && (!c.CaptureOnly || c.MESListen == "") {
		return nil, errors.New("forward_mes requires capture_only and mes_listen")
	}
	b := &bridge{config: c, devices: make(map[string]bool), wake: make(chan struct{}, 1)}
	for _, id := range c.SourceIDs {
		if id == "" {
			return nil, errors.New("source_ids cannot contain an empty ID")
		}
		b.devices[id] = true
	}
	for _, cidr := range c.AllowedCIDRs {
		_, network, err := net.ParseCIDR(cidr)
		if err != nil {
			return nil, fmt.Errorf("allowed_cidrs: %w", err)
		}
		ones, _ := network.Mask.Size()
		if ones == 0 {
			return nil, errors.New("allowed_cidrs cannot allow the entire internet")
		}
		b.allowed = append(b.allowed, network)
	}
	return b, nil
}

func (b *bridge) handler() http.Handler {
	if b.config.CaptureOnly {
		return logRequests("private", http.HandlerFunc(b.handleCapture))
	}
	return logRequests("private", http.HandlerFunc(b.handle))
}

func (b *bridge) handleCapture(w http.ResponseWriter, r *http.Request) {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil || !b.sourceAllowed(net.ParseIP(host)) {
		writeFailure(w, http.StatusForbidden, route{}, "source IP is not allowed", "")
		return
	}
	if r.URL.Path != "/jdlSave/MchnData" {
		writeFailure(w, http.StatusNotFound, route{}, "unknown route", "")
		return
	}
	if r.Method != http.MethodPost {
		writeFailure(w, http.StatusMethodNotAllowed, route{}, "POST required", "")
		return
	}
	if subtle.ConstantTimeCompare([]byte(r.URL.Query().Get("key")), []byte(b.config.CaptureToken)) != 1 {
		writeFailure(w, http.StatusForbidden, route{}, "invalid capture key", "")
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxRequestBytes+1))
	if err != nil {
		writeFailure(w, http.StatusBadRequest, route{}, "cannot read body", "")
		return
	}
	if len(body) > maxRequestBytes {
		writeFailure(w, http.StatusRequestEntityTooLarge, route{}, "body too large", "")
		return
	}
	record := captureRecord{Transport: "http", Path: r.URL.Path, Method: r.Method, ContentType: r.Header.Get("Content-Type"), RemoteAddr: r.RemoteAddr, ForwardedFor: r.Header.Get("X-Forwarded-For"), ReceivedAt: time.Now().Format(time.RFC3339Nano), Body: body, ResponseStatus: http.StatusOK}
	if err := persistJSON(filepath.Join(b.config.QueueDir, "captures"), record); err != nil {
		log.Printf("capture write failed: %v", err)
		writeFailure(w, http.StatusServiceUnavailable, route{}, "capture unavailable", "")
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_, _ = w.Write([]byte(`{"code":0,"msg":"captured"}`))
}

func (b *bridge) handle(w http.ResponseWriter, r *http.Request) {
	path := strings.TrimPrefix(r.URL.Path, basePath)
	routeInfo, known := routes[path]
	if r.URL.Path == basePath+"/Equip/MillingMachineAccessToTask" {
		known = true
	}
	if !strings.HasPrefix(r.URL.Path, basePath+"/") || !known {
		writeFailure(w, http.StatusNotFound, routeInfo, "unknown route", "")
		return
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil || !b.sourceAllowed(net.ParseIP(host)) {
		writeFailure(w, http.StatusForbidden, routeInfo, "source IP is not allowed", "")
		return
	}
	if r.Method != http.MethodPost {
		writeFailure(w, http.StatusMethodNotAllowed, routeInfo, "POST required", "")
		return
	}
	mediaType, params, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || !strings.EqualFold(mediaType, "application/json") || (params["charset"] != "" && !strings.EqualFold(params["charset"], "utf-8")) {
		writeFailure(w, http.StatusUnsupportedMediaType, routeInfo, "UTF-8 application/json required", "")
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxRequestBytes+1))
	if err != nil {
		writeFailure(w, http.StatusBadRequest, routeInfo, "cannot read body", "")
		return
	}
	if len(body) > maxRequestBytes {
		writeFailure(w, http.StatusRequestEntityTooLarge, routeInfo, "body too large", "")
		return
	}
	if !utf8.Valid(body) {
		writeFailure(w, http.StatusBadRequest, routeInfo, "invalid UTF-8", "")
		return
	}
	var payload map[string]interface{}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.UseNumber()
	if err := decoder.Decode(&payload); err != nil || payload == nil {
		writeFailure(w, http.StatusBadRequest, routeInfo, "JSON object required", "")
		return
	}
	var extra interface{}
	if decoder.Decode(&extra) != io.EOF {
		writeFailure(w, http.StatusBadRequest, routeInfo, "trailing JSON data", "")
		return
	}
	if r.URL.Path == basePath+"/Equip/MillingMachineAccessToTask" {
		if !b.devices[stringValue(payload["AssetsCode"])] {
			writeFailure(w, http.StatusForbidden, routeInfo, "unknown device", "")
			return
		}
		writeFailure(w, http.StatusServiceUnavailable, routeInfo, "task source is not configured", "")
		return
	}
	deviceID := stringValue(payload[routeInfo.deviceKey])
	if !b.devices[deviceID] {
		writeFailure(w, http.StatusForbidden, routeInfo, "unknown device", stringValue(payload["TransactionId"]))
		return
	}
	message, err := b.makeMessage(routeInfo, payload, body)
	if err != nil {
		writeFailure(w, http.StatusBadRequest, routeInfo, err.Error(), stringValue(payload["TransactionId"]))
		return
	}
	if err := b.enqueue(message); err != nil {
		log.Printf("queue write failed: %v", err)
		writeFailure(w, http.StatusServiceUnavailable, routeInfo, "queue unavailable", stringValue(payload["TransactionId"]))
		return
	}
	writeUploadSuccess(w, routeInfo, stringValue(payload["TransactionId"]))
}

func writeUploadSuccess(w http.ResponseWriter, routeInfo route, transactionID string) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	if routeInfo.broken {
		_ = json.NewEncoder(w).Encode(map[string]interface{}{"TransactionId": transactionID, "Code": "0", "Msg": "上传成功", "Data": nil})
	} else {
		_ = json.NewEncoder(w).Encode(map[string]interface{}{"Error": map[string]interface{}{"Message": "", "Code": 0}, "IsSuccess": true})
	}
}

func writeFailure(w http.ResponseWriter, status int, routeInfo route, message, transactionID string) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	if routeInfo.broken {
		_ = json.NewEncoder(w).Encode(map[string]interface{}{"TransactionId": transactionID, "Code": "1", "Msg": message, "Data": nil})
	} else {
		_ = json.NewEncoder(w).Encode(map[string]interface{}{"Error": map[string]interface{}{"Message": message, "Code": 1}, "IsSuccess": false})
	}
}

func (b *bridge) sourceAllowed(ip net.IP) bool {
	if ip == nil {
		return false
	}
	for _, network := range b.allowed {
		if network.Contains(ip) {
			return true
		}
	}
	return false
}

func stringValue(v interface{}) string { s, _ := v.(string); return s }

func (b *bridge) makeMessage(routeInfo route, payload map[string]interface{}, raw []byte) (queuedMessage, error) {
	batch := make([]byte, 8)
	if _, err := rand.Read(batch); err != nil {
		return queuedMessage{}, err
	}
	batchID := hex.EncodeToString(batch)
	keys := make([]string, 0, len(payload))
	for key := range payload {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	items := make([]eventItem, 0, len(keys))
	for _, key := range keys {
		value, kind, err := eventValue(payload[key])
		if err != nil {
			return queuedMessage{}, err
		}
		runes := []rune(value)
		total := (len(runes) + 63) / 64
		if total == 0 {
			total = 1
		}
		for part := 0; part < total; part++ {
			start := part * 64
			end := start + 64
			if end > len(runes) {
				end = len(runes)
			}
			meta, err := json.Marshal(eventRemark{Batch: batchID, Field: key, Kind: kind, Part: part + 1, Total: total})
			if err != nil {
				return queuedMessage{}, err
			}
			if utf8.RuneCount(meta) > 200 {
				return queuedMessage{}, fmt.Errorf("field name %q exceeds S1 remark limit", key)
			}
			items = append(items, eventItem{ID: "jdl_" + routeInfo.name, Value: string(runes[start:end]), TS: time.Now().Format("2006-01-02 15:04:05"), Remark: string(meta)})
		}
	}
	encoded, err := json.Marshal(items)
	if err != nil {
		return queuedMessage{}, err
	}
	return queuedMessage{Topic: "/" + b.config.ProductID + "/" + b.config.SerialNumber + "/event/post", Payload: encoded, Raw: append([]byte(nil), raw...)}, nil
}

func eventValue(v interface{}) (string, string, error) {
	switch value := v.(type) {
	case nil:
		return "", "null", nil
	case string:
		return value, "string", nil
	case json.Number:
		return string(value), "number", nil
	case bool:
		if value {
			return "true", "bool", nil
		}
		return "false", "bool", nil
	default:
		data, err := json.Marshal(v)
		return string(data), "json", err
	}
}

func (b *bridge) enqueue(message queuedMessage) error {
	if err := persistJSON(b.config.QueueDir, message); err != nil {
		return err
	}
	select {
	case b.wake <- struct{}{}:
	default:
	}
	return nil
}

func persistJSON(directory string, value interface{}) error {
	if err := os.MkdirAll(directory, 0700); err != nil {
		return err
	}
	file, err := os.CreateTemp(directory, ".incoming-*")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	if err := json.NewEncoder(file).Encode(value); err != nil {
		file.Close()
		return err
	}
	if err := file.Sync(); err != nil {
		file.Close()
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	name := time.Now().UTC().Format("20060102T150405.000000000") + "-" + filepath.Base(file.Name()) + ".json"
	if err := os.Rename(file.Name(), filepath.Join(directory, name)); err != nil {
		return err
	}
	if runtime.GOOS != "windows" {
		dir, err := os.Open(directory)
		if err != nil {
			return err
		}
		defer dir.Close()
		if err := dir.Sync(); err != nil {
			return err
		}
	}
	return nil
}

func (b *bridge) flushOnce() error {
	files, err := os.ReadDir(b.config.QueueDir)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	for _, file := range files {
		if file.IsDir() || !strings.HasSuffix(file.Name(), ".json") {
			continue
		}
		path := filepath.Join(b.config.QueueDir, file.Name())
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		var message queuedMessage
		if err := json.Unmarshal(data, &message); err != nil {
			return fmt.Errorf("queue file %s: %w", path, err)
		}
		if message.Topic == "" || !json.Valid(message.Payload) {
			return fmt.Errorf("queue file %s is invalid", path)
		}
		if err := publishMQTT(b.config, message); err != nil {
			return err
		}
		sentDir := filepath.Join(b.config.QueueDir, "sent")
		if err := os.MkdirAll(sentDir, 0700); err != nil {
			return err
		}
		// shortcut: sent archives grow until an operator confirms S1 persistence and prunes them.
		if err := os.Rename(path, filepath.Join(sentDir, file.Name())); err != nil {
			return err
		}
	}
	return nil
}

func (b *bridge) runWorker(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		if err := b.flushOnce(); err != nil {
			log.Printf("S1 forward retry: %v", err)
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		case <-b.wake:
		}
	}
}

func main() {
	configPath := flag.String("config", "config.json", "private JSON configuration")
	flag.Parse()
	stat, err := os.Stat(*configPath)
	if err != nil {
		log.Fatal(err)
	}
	if runtime.GOOS != "windows" && stat.Mode().Perm()&0077 != 0 {
		log.Fatal("config file must be readable only by its owner (chmod 600)")
	}
	data, err := os.ReadFile(*configPath)
	if err != nil {
		log.Fatal(err)
	}
	var c config
	if err := json.Unmarshal(data, &c); err != nil {
		log.Fatal(err)
	}
	b, err := newBridge(c)
	if err != nil {
		log.Fatal(err)
	}
	if c.MESListen != "" && (c.MESCaptureUntil.IsZero() || time.Now().Before(c.MESCaptureUntil)) {
		listener, err := net.Listen("tcp", c.MESListen)
		if err != nil {
			log.Fatal(err)
		}
		if c.MESCaptureUntil.IsZero() {
			log.Printf("MES capture on %s continuously", c.MESListen)
		} else {
			log.Printf("MES capture on %s until %s", c.MESListen, c.MESCaptureUntil.Format(time.RFC3339))
		}
		go func() {
			if err := b.serveMESCapture(listener, c.MESCaptureUntil); err != nil {
				log.Fatalf("MES capture stopped: %v", err)
			}
		}()
	}
	if !c.CaptureOnly || c.ForwardMES {
		go b.runWorker(context.Background())
	}
	server := &http.Server{Addr: c.Listen, Handler: b.handler(), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 10 * time.Second, WriteTimeout: 10 * time.Second, MaxHeaderBytes: 8192}
	log.Printf("JDL listener on %s", c.Listen)
	log.Fatal(server.ListenAndServe())
}

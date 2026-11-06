package main

import (
	"bytes"
	"encoding/json"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"time"
	"unicode/utf8"
)

func documentedRoute(path string) (route, bool, bool) {
	// The device firmware sends the documented routes without their base path.
	path = strings.TrimPrefix(path, basePath)
	if path == "/Equip/MillingMachineAccessToTask" {
		return route{}, true, true
	}
	info, known := routes[path]
	return info, false, known
}

func (b *bridge) handleDocumentCapture(w http.ResponseWriter, r *http.Request, persist func(captureRecord) error) {
	info, scan, known := documentedRoute(r.URL.Path)
	if !known {
		writeFailure(w, http.StatusNotFound, route{}, "unknown route", "")
		return
	}
	body, readErr := io.ReadAll(io.LimitReader(r.Body, maxRequestBytes+1))
	status, message := http.StatusOK, ""
	if readErr != nil {
		status, message = http.StatusBadRequest, "cannot read body"
	}
	truncated := len(body) > maxRequestBytes
	if truncated {
		body = body[:maxRequestBytes]
		status, message = http.StatusRequestEntityTooLarge, "body too large"
	}
	var payload map[string]interface{}
	if status == http.StatusOK && r.Method != http.MethodPost {
		status, message = http.StatusMethodNotAllowed, "POST required"
	}
	if status == http.StatusOK {
		decoder := json.NewDecoder(bytes.NewReader(body))
		decoder.UseNumber()
		var extra interface{}
		if !utf8.Valid(body) || decoder.Decode(&payload) != nil || payload == nil || decoder.Decode(&extra) != io.EOF {
			status, message = http.StatusBadRequest, "JSON object required"
		}
	}
	transactionID := stringValue(payload["TransactionId"])
	if status == http.StatusOK {
		deviceKey := info.deviceKey
		if scan {
			deviceKey = "AssetsCode"
		}
		if stringValue(payload[deviceKey]) == "" {
			status, message = http.StatusBadRequest, "missing "+deviceKey
		} else if scan && stringValue(payload["VehicleCode"]) == "" {
			status, message = http.StatusBadRequest, "missing VehicleCode"
		} else if info.broken && transactionID == "" {
			status, message = http.StatusBadRequest, "missing TransactionId"
		} else if scan {
			status, message = http.StatusServiceUnavailable, "task source is not configured"
		}
	}
	var forwardMessage queuedMessage
	forward := status == http.StatusOK && b.config.ForwardMES && !scan
	if forward {
		host, _, err := net.SplitHostPort(r.RemoteAddr)
		_, knownDevice := b.devices[stringValue(payload[info.deviceKey])]
		if err != nil || !b.sourceAllowed(net.ParseIP(host)) || !knownDevice {
			status, message, forward = http.StatusForbidden, "untrusted device or source", false
		} else if forwardMessage, err = b.makeMessage(info, payload, body); err != nil {
			status, message, forward = http.StatusBadRequest, err.Error(), false
		}
	}
	record := captureRecord{Transport: "http", Path: r.URL.Path, Method: r.Method, Query: r.URL.RawQuery, Headers: r.Header.Clone(), ContentType: r.Header.Get("Content-Type"), RemoteAddr: r.RemoteAddr, ForwardedFor: r.Header.Get("X-Forwarded-For"), ReceivedAt: time.Now().Format(time.RFC3339Nano), Body: body, Truncated: truncated, ResponseStatus: status}
	if forward {
		// The durable MQTT queue also archives the full request when public capture storage is full.
		forwardMessage.Capture = &record
		if err := b.enqueue(forwardMessage); err != nil {
			record.ResponseStatus = http.StatusServiceUnavailable
			if captureErr := persist(record); captureErr != nil {
				log.Printf("JDL EAP failed report capture on %s from %s: %v", r.URL.Path, r.RemoteAddr, captureErr)
			}
			log.Printf("JDL EAP forward queue failed on %s from %s: %v", r.URL.Path, r.RemoteAddr, err)
			writeFailure(w, http.StatusServiceUnavailable, info, "forward queue unavailable", transactionID)
			return
		}
		if err := persist(record); err != nil {
			log.Printf("JDL EAP public capture skipped on %s from %s: %v", r.URL.Path, r.RemoteAddr, err)
		}
		log.Printf("JDL EAP queued %s from %s: %d bytes", r.URL.Path, r.RemoteAddr, len(body))
		writeUploadSuccess(w, info, transactionID)
		return
	}
	if err := persist(record); err != nil {
		log.Printf("JDL EAP capture failed on %s from %s: %v", r.URL.Path, r.RemoteAddr, err)
		writeFailure(w, http.StatusServiceUnavailable, info, "capture unavailable", transactionID)
		return
	}
	log.Printf("JDL EAP captured %s from %s: %d bytes, response %d", r.URL.Path, r.RemoteAddr, len(body), status)
	if status != http.StatusOK {
		writeFailure(w, status, info, message, transactionID)
		return
	}
	writeUploadSuccess(w, info, transactionID)
}

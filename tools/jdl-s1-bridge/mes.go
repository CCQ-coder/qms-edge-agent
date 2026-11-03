package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

const maxMESConnections = 16
const maxMESConnectionsPerIP = 4
const defaultMESMaxFiles = 10000
const maxMESPerIPPerMinute = 30
const maxMESGlobalPerMinute = 300

var errMESRateLimited = errors.New("MES capture rate limit reached")
var errMESArchiveFull = errors.New("MES capture archive is full")

type mesMonitor struct {
	bridge     *bridge
	until      time.Time
	clients    chan struct{}
	httpIn     *mesHTTPListener
	activeMu   sync.Mutex
	activeByIP map[string]int
	logMu      sync.Mutex
	logWindow  time.Time
	logCount   int
	storeMu    sync.Mutex
	rateStart  time.Time
	rateGlobal int
	rateByIP   map[string]int
}

type mesHTTPListener struct {
	addr  net.Addr
	conns chan net.Conn
	done  chan struct{}
	once  sync.Once
}

func (l *mesHTTPListener) Accept() (net.Conn, error) {
	select {
	case conn := <-l.conns:
		return conn, nil
	case <-l.done:
		return nil, net.ErrClosed
	}
}

func (l *mesHTTPListener) Close() error {
	l.once.Do(func() { close(l.done) })
	return nil
}

func (l *mesHTTPListener) Addr() net.Addr { return l.addr }

type mesConn struct {
	net.Conn
	reader  *bufio.Reader
	clients chan struct{}
	release func()
	once    sync.Once
}

func (c *mesConn) Read(p []byte) (int, error) { return c.reader.Read(p) }

func (c *mesConn) Close() error {
	c.once.Do(func() {
		if c.release != nil {
			c.release()
		}
		<-c.clients
	})
	return c.Conn.Close()
}

func (b *bridge) serveMESCapture(listener net.Listener, until time.Time) error {
	if !until.IsZero() && !time.Now().Before(until) {
		_ = listener.Close()
		return nil
	}
	m := &mesMonitor{bridge: b, until: until, clients: make(chan struct{}, maxMESConnections), activeByIP: make(map[string]int)}
	m.httpIn = &mesHTTPListener{addr: listener.Addr(), conns: make(chan net.Conn, maxMESConnections), done: make(chan struct{})}
	server := &http.Server{Handler: http.HandlerFunc(m.handleHTTP), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 15 * time.Second, WriteTimeout: 10 * time.Second, IdleTimeout: 5 * time.Second, MaxHeaderBytes: 8192}
	server.SetKeepAlivesEnabled(false)
	httpError := make(chan error, 1)
	go func() {
		if err := server.Serve(m.httpIn); err != nil && !errors.Is(err, http.ErrServerClosed) && !errors.Is(err, net.ErrClosed) {
			httpError <- err
			_ = listener.Close()
		}
	}()
	if !until.IsZero() {
		timer := time.AfterFunc(time.Until(until), func() { _ = listener.Close() })
		defer timer.Stop()
	}
	defer m.httpIn.Close()
	defer server.Close()
	for {
		conn, err := listener.Accept()
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				select {
				case cause := <-httpError:
					return fmt.Errorf("MES HTTP server: %w", cause)
				default:
					if !until.IsZero() && !time.Now().Before(until) {
						return nil
					}
					return errors.New("MES listener closed unexpectedly")
				}
			}
			return err
		}
		select {
		case m.clients <- struct{}{}:
			host, _, err := net.SplitHostPort(conn.RemoteAddr().String())
			if err != nil || !m.admitIP(host) {
				m.logUncaptured(conn.RemoteAddr().String(), "connection limit")
				<-m.clients
				_ = conn.Close()
				continue
			}
			go m.classify(&mesConn{Conn: conn, reader: bufio.NewReader(conn), clients: m.clients, release: func() { m.releaseIP(host) }})
		default:
			m.logUncaptured(conn.RemoteAddr().String(), "global connection limit")
			_ = conn.Close()
		}
	}
}

func (m *mesMonitor) logUncaptured(remote, reason string) {
	m.logMu.Lock()
	if time.Since(m.logWindow) >= time.Minute {
		m.logWindow = time.Now()
		m.logCount = 0
	}
	shouldLog := m.logCount < 10
	m.logCount++
	m.logMu.Unlock()
	if shouldLog {
		log.Printf("MES uncaptured connection from %s: %s", remote, reason)
	}
}

func (m *mesMonitor) admitIP(host string) bool {
	m.activeMu.Lock()
	defer m.activeMu.Unlock()
	if m.activeByIP[host] >= maxMESConnectionsPerIP {
		return false
	}
	m.activeByIP[host]++
	return true
}

func (m *mesMonitor) releaseIP(host string) {
	m.activeMu.Lock()
	defer m.activeMu.Unlock()
	m.activeByIP[host]--
	if m.activeByIP[host] == 0 {
		delete(m.activeByIP, host)
	}
}

func (m *mesMonitor) persistMES(record captureRecord) error {
	m.storeMu.Lock()
	defer m.storeMu.Unlock()
	now := time.Now()
	if m.rateStart.IsZero() || now.Sub(m.rateStart) >= time.Minute {
		m.rateStart = now
		m.rateGlobal = 0
		m.rateByIP = make(map[string]int)
	}
	host, _, err := net.SplitHostPort(record.RemoteAddr)
	if err != nil {
		host = record.RemoteAddr
	}
	if m.rateGlobal >= maxMESGlobalPerMinute || m.rateByIP[host] >= maxMESPerIPPerMinute {
		return errMESRateLimited
	}
	directory := filepath.Join(m.bridge.config.QueueDir, "mes-captures")
	if err := os.MkdirAll(directory, 0700); err != nil {
		return err
	}
	entries, err := os.ReadDir(directory)
	if err != nil {
		return err
	}
	fileCount := 0
	for _, entry := range entries {
		if !entry.IsDir() && strings.HasSuffix(entry.Name(), ".json") {
			fileCount++
		}
	}
	limit := m.bridge.config.MESMaxFiles
	if limit == 0 {
		limit = defaultMESMaxFiles
	}
	if fileCount >= limit {
		return errMESArchiveFull
	}
	if err := persistJSON(directory, record); err != nil {
		return err
	}
	m.rateGlobal++
	m.rateByIP[host]++
	return nil
}

func (m *mesMonitor) classify(conn *mesConn) {
	_ = conn.SetReadDeadline(earlier(time.Now().Add(5*time.Second), m.until))
	prefix, _ := conn.reader.Peek(8)
	_ = conn.SetReadDeadline(time.Time{})
	method, _, hasSpace := strings.Cut(string(prefix), " ")
	if hasSpace && (method == "GET" || method == "POST" || method == "HEAD" || method == "PUT" || method == "PATCH" || method == "DELETE" || method == "OPTIONS" || method == "TRACE" || method == "CONNECT") {
		select {
		case m.httpIn.conns <- conn:
			return
		case <-m.httpIn.done:
			_ = conn.Close()
			return
		}
	}
	defer conn.Close()
	m.captureTCP(conn)
}

func (m *mesMonitor) handleHTTP(w http.ResponseWriter, r *http.Request) {
	if _, _, known := documentedRoute(r.URL.Path); known {
		m.bridge.handleDocumentCapture(w, r, m.persistMES)
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxRequestBytes+1))
	status := http.StatusOK
	if r.URL.Path != "/httptest.php" && r.URL.Path != "/jdlSave/MchnData" {
		status = http.StatusNotFound
	} else if r.Method != http.MethodPost && r.Method != http.MethodGet {
		status = http.StatusMethodNotAllowed
	}
	if err != nil {
		status = http.StatusBadRequest
	}
	truncated := len(body) > maxRequestBytes
	if truncated {
		body = body[:maxRequestBytes]
		status = http.StatusRequestEntityTooLarge
	}
	record := captureRecord{Transport: "http", Path: r.URL.Path, Method: r.Method, Query: r.URL.RawQuery, Headers: r.Header.Clone(), ContentType: r.Header.Get("Content-Type"), RemoteAddr: r.RemoteAddr, ForwardedFor: r.Header.Get("X-Forwarded-For"), ReceivedAt: time.Now().Format(time.RFC3339Nano), Body: body, Truncated: truncated, ResponseStatus: status}
	if err := m.persistMES(record); err != nil {
		m.logUncaptured(r.RemoteAddr, "HTTP capture rejected: "+err.Error())
		http.Error(w, "capture unavailable", http.StatusServiceUnavailable)
		return
	}
	if status != http.StatusOK {
		http.Error(w, http.StatusText(status), status)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(map[string]interface{}{"code": 0, "msg": "captured"})
}

func (m *mesMonitor) captureTCP(conn *mesConn) {
	// shortcut: bytes are grouped by connection until the vendor's MES framing and ACK are known.
	start := time.Now()
	end := earlier(start.Add(90*time.Second), m.until)
	body := make([]byte, 0, 4096)
	var readTimes []string
	truncated := false
	buffer := make([]byte, 4096)
	for time.Now().Before(end) || conn.reader.Buffered() > 0 {
		if conn.reader.Buffered() == 0 {
			_ = conn.SetReadDeadline(earlier(time.Now().Add(35*time.Second), end))
		}
		remaining := maxRequestBytes + 1 - len(body)
		if remaining < len(buffer) {
			buffer = buffer[:remaining]
		}
		if buffered := conn.reader.Buffered(); buffered > 0 && !time.Now().Before(end) && buffered < len(buffer) {
			buffer = buffer[:buffered]
		}
		n, err := conn.Read(buffer)
		if n > 0 {
			body = append(body, buffer[:n]...)
			if len(readTimes) < 128 {
				readTimes = append(readTimes, time.Now().Format(time.RFC3339Nano))
			}
		}
		if len(body) > maxRequestBytes {
			body = body[:maxRequestBytes]
			truncated = true
			break
		}
		if err != nil {
			break
		}
	}
	if len(body) == 0 {
		m.logUncaptured(conn.RemoteAddr().String(), "TCP connection sent no bytes")
		return
	}
	record := captureRecord{Transport: "tcp", RemoteAddr: conn.RemoteAddr().String(), ReceivedAt: start.Format(time.RFC3339Nano), Body: body, ReadTimes: readTimes, Truncated: truncated}
	if err := m.persistMES(record); err != nil {
		m.logUncaptured(conn.RemoteAddr().String(), "TCP capture rejected: "+err.Error())
	}
}

func earlier(a, b time.Time) time.Time {
	if b.IsZero() || a.Before(b) {
		return a
	}
	return b
}

package main

import (
	"context"
	"fmt"
	"net"
	"testing"
	"time"
)

func TestWorkerKeepsMQTTConnectedAcrossPropertyAndEvent(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()

	brokerResult := make(chan error, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			brokerResult <- err
			return
		}
		defer conn.Close()
		_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
		if header, _, err := readTestPacket(conn); err != nil || header != 0x10 {
			brokerResult <- fmt.Errorf("CONNECT: header %x, error %v", header, err)
			return
		}
		if _, err := conn.Write([]byte{0x20, 0x02, 0, 0}); err != nil {
			brokerResult <- err
			return
		}
		for _, want := range []string{"/34/JDLR0007/property/post", "/34/JDLR0007/event/post"} {
			header, body, err := readTestPacket(conn)
			if err != nil || header != 0x32 {
				brokerResult <- fmt.Errorf("%s PUBLISH on same connection: header %x, error %v", want, header, err)
				return
			}
			i := 0
			if got := readTestText(body, &i); got != want {
				brokerResult <- fmt.Errorf("topic = %q, want %q", got, want)
				return
			}
			if _, err := conn.Write([]byte{0x40, 0x02, body[i], body[i+1]}); err != nil {
				brokerResult <- err
				return
			}
		}
		_ = conn.SetReadDeadline(time.Now().Add(200 * time.Millisecond))
		header, _, err := readTestPacket(conn)
		if timeout, ok := err.(net.Error); ok && timeout.Timeout() {
			brokerResult <- nil
			return
		}
		brokerResult <- fmt.Errorf("connection closed or extra MQTT packet after report: header %x, error %v", header, err)
	}()

	cfg := multiDeviceConfig(t)
	cfg.Broker = listener.Addr().String()
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go b.runWorker(ctx)
	postMultiDevice(t, b, "JDLR0007", 200)
	select {
	case err := <-brokerResult:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(4 * time.Second):
		t.Fatal("broker did not receive both reports on one MQTT connection")
	}
}

func TestKeepOnlinePingsRecentDeviceAndDisconnectsAfterReportsStop(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	brokerResult := make(chan error, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			brokerResult <- err
			return
		}
		defer conn.Close()
		_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
		if header, _, err := readTestPacket(conn); err != nil || header != 0x10 {
			brokerResult <- fmt.Errorf("CONNECT: header %x, error %v", header, err)
			return
		}
		if _, err := conn.Write([]byte{0x20, 0x02, 0, 0}); err != nil {
			brokerResult <- err
			return
		}
		if header, _, err := readTestPacket(conn); err != nil || header != 0xC0 {
			brokerResult <- fmt.Errorf("PINGREQ: header %x, error %v", header, err)
			return
		}
		if _, err := conn.Write([]byte{0xD0, 0}); err != nil {
			brokerResult <- err
			return
		}
		if header, _, err := readTestPacket(conn); err != nil || header != 0xE0 {
			brokerResult <- fmt.Errorf("DISCONNECT after reports stop: header %x, error %v", header, err)
			return
		}
		brokerResult <- nil
	}()

	cfg := multiDeviceConfig(t)
	cfg.Broker = listener.Addr().String()
	b, err := newBridge(cfg)
	if err != nil {
		t.Fatal(err)
	}
	client, err := connectMQTT(b.connectionFor(b.devices["JDLR0007"]))
	if err != nil {
		t.Fatal(err)
	}
	defer client.conn.Close()
	clients := map[string]*mqttClient{"JDLR0007": client}
	b.lastSeen["JDLR0007"] = time.Now()
	client.lastIO = time.Now().Add(-16 * time.Second)
	b.keepOnline(clients)
	if clients["JDLR0007"] != client {
		t.Fatal("recent device lost its MQTT session")
	}
	b.lastSeen["JDLR0007"] = time.Now().Add(-4 * time.Minute)
	b.keepOnline(clients)
	if len(clients) != 0 {
		t.Fatal("inactive device kept an MQTT session")
	}
	select {
	case err := <-brokerResult:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(4 * time.Second):
		t.Fatal("broker did not receive heartbeat and disconnect")
	}
}

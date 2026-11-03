package main

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net"
	"time"
)

func publishMQTT(c config, message queuedMessage) error {
	// shortcut: one connection per report; reuse connections if production traffic grows a backlog.
	conn, err := net.DialTimeout("tcp", c.Broker, 5*time.Second)
	if err != nil {
		return err
	}
	defer conn.Close()
	if err := conn.SetDeadline(time.Now().Add(10 * time.Second)); err != nil {
		return err
	}

	connect := append(mqttText("MQTT"), 4, 0xC2, 0, 30) // MQTT 3.1.1, clean session, username and password.
	connect = append(connect, mqttText("S&"+c.SerialNumber+"&"+c.ProductID+"&"+c.UserID)...)
	connect = append(connect, mqttText(c.Username)...)
	connect = append(connect, mqttText(c.Password)...)
	if err := writeMQTTPacket(conn, 0x10, connect); err != nil {
		return err
	}
	var ack [4]byte
	if _, err := io.ReadFull(conn, ack[:]); err != nil {
		return err
	}
	if ack[0] != 0x20 || ack[1] != 2 || ack[2] != 0 || ack[3] != 0 {
		return fmt.Errorf("S1 MQTT connection rejected: %x", ack)
	}

	publish := append(mqttText(message.Topic), 0, 1) // Packet ID 1; one in-flight message per connection.
	publish = append(publish, message.Payload...)
	if err := writeMQTTPacket(conn, 0x32, publish); err != nil {
		return err
	}
	if _, err := io.ReadFull(conn, ack[:]); err != nil {
		return err
	}
	if !bytes.Equal(ack[:], []byte{0x40, 0x02, 0x00, 0x01}) {
		return fmt.Errorf("S1 MQTT PUBACK mismatch: %x", ack)
	}
	_ = writeMQTTPacket(conn, 0xE0, nil)
	return nil
}

func mqttText(s string) []byte {
	data := []byte(s)
	return append([]byte{byte(len(data) >> 8), byte(len(data))}, data...)
}

func writeMQTTPacket(w io.Writer, header byte, body []byte) error {
	if len(body) > 268435455 {
		return errors.New("MQTT packet too large")
	}
	packet := []byte{header}
	length := len(body)
	for {
		digit := byte(length % 128)
		length /= 128
		if length > 0 {
			digit |= 0x80
		}
		packet = append(packet, digit)
		if length == 0 {
			break
		}
	}
	packet = append(packet, body...)
	_, err := io.Copy(w, bytes.NewReader(packet))
	return err
}

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
	client, err := connectMQTT(c)
	if err != nil {
		return err
	}
	defer client.disconnect()
	return client.publish(message)
}

type mqttClient struct {
	conn   net.Conn
	lastIO time.Time
}

func connectMQTT(c config) (*mqttClient, error) {
	conn, err := net.DialTimeout("tcp", c.Broker, 5*time.Second)
	if err != nil {
		return nil, err
	}
	connected := false
	defer func() {
		if !connected {
			_ = conn.Close()
		}
	}()
	if err := conn.SetDeadline(time.Now().Add(10 * time.Second)); err != nil {
		return nil, err
	}

	connect := append(mqttText("MQTT"), 4, 0xC2, 0, 30) // MQTT 3.1.1, clean session, username and password.
	connect = append(connect, mqttText("S&"+c.SerialNumber+"&"+c.ProductID+"&"+c.UserID)...)
	connect = append(connect, mqttText(c.Username)...)
	connect = append(connect, mqttText(c.Password)...)
	if err := writeMQTTPacket(conn, 0x10, connect); err != nil {
		return nil, err
	}
	var ack [4]byte
	if _, err := io.ReadFull(conn, ack[:]); err != nil {
		return nil, err
	}
	if ack[0] != 0x20 || ack[1] != 2 || ack[2] != 0 || ack[3] != 0 {
		return nil, fmt.Errorf("S1 MQTT connection rejected: %x", ack)
	}
	if err := conn.SetDeadline(time.Time{}); err != nil {
		return nil, err
	}
	connected = true
	return &mqttClient{conn: conn, lastIO: time.Now()}, nil
}

func (client *mqttClient) publish(message queuedMessage) error {
	if err := client.conn.SetDeadline(time.Now().Add(10 * time.Second)); err != nil {
		return err
	}
	defer client.conn.SetDeadline(time.Time{})
	publish := append(mqttText(message.Topic), 0, 1) // Packet ID 1; one in-flight message per connection.
	publish = append(publish, message.Payload...)
	if err := writeMQTTPacket(client.conn, 0x32, publish); err != nil {
		return err
	}
	var ack [4]byte
	if _, err := io.ReadFull(client.conn, ack[:]); err != nil {
		return err
	}
	// FastBee sets QoS1's reserved bit on PUBACK; accept only its exact variant.
	if !bytes.Equal(ack[:], []byte{0x40, 0x02, 0x00, 0x01}) && !bytes.Equal(ack[:], []byte{0x42, 0x02, 0x00, 0x01}) {
		return fmt.Errorf("S1 MQTT PUBACK mismatch: %x", ack)
	}
	client.lastIO = time.Now()
	return nil
}

func (client *mqttClient) ping() error {
	if err := client.conn.SetDeadline(time.Now().Add(10 * time.Second)); err != nil {
		return err
	}
	defer client.conn.SetDeadline(time.Time{})
	if err := writeMQTTPacket(client.conn, 0xC0, nil); err != nil {
		return err
	}
	var response [2]byte
	if _, err := io.ReadFull(client.conn, response[:]); err != nil {
		return err
	}
	if response != [2]byte{0xD0, 0} {
		return fmt.Errorf("S1 MQTT PINGRESP mismatch: %x", response)
	}
	client.lastIO = time.Now()
	return nil
}

func (client *mqttClient) disconnect() {
	_ = client.conn.SetWriteDeadline(time.Now().Add(2 * time.Second))
	_ = writeMQTTPacket(client.conn, 0xE0, nil)
	_ = client.conn.Close()
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

package sieve

import (
	"bufio"
	"crypto/tls"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"strconv"
	"strings"
	"time"
)

// Script represents a ManageSieve script with its activation state.
type Script struct {
	Name   string `json:"name"`
	Active bool   `json:"active"`
}

// Client holds an authenticated ManageSieve connection.
type Client struct {
	conn net.Conn
	r    *bufio.Reader
}

// Dial connects to a ManageSieve server (RFC 5804), negotiates TLS, and logs in.
func Dial(host string, port int, user, pass string) (*Client, error) {
	d := &net.Dialer{Timeout: 10 * time.Second}
	raw, err := d.Dial("tcp", net.JoinHostPort(host, strconv.Itoa(port)))
	if err != nil {
		return nil, err
	}
	c := &Client{conn: raw, r: bufio.NewReader(raw)}
	if err := c.skipOK(); err != nil {
		raw.Close()
		return nil, fmt.Errorf("greeting: %w", err)
	}
	// STARTTLS — verpflichtend: Zugangsdaten dürfen niemals im Klartext gehen.
	fmt.Fprintf(c.conn, "STARTTLS\r\n")
	if err := c.skipOK(); err != nil {
		raw.Close()
		return nil, fmt.Errorf("STARTTLS abgelehnt (Klartext-Auth verweigert): %w", err)
	}
	tlsConn := tls.Client(raw, &tls.Config{ServerName: host})
	if err := tlsConn.Handshake(); err != nil {
		raw.Close()
		return nil, fmt.Errorf("TLS-Handshake fehlgeschlagen: %w", err)
	}
	c.conn = tlsConn
	c.r = bufio.NewReader(tlsConn)
	if err := c.skipOK(); err != nil { // new capability greeting after TLS
		c.conn.Close()
		return nil, fmt.Errorf("Capabilities nach TLS: %w", err)
	}
	// AUTHENTICATE PLAIN (jetzt über die verschlüsselte Verbindung)
	token := base64.StdEncoding.EncodeToString([]byte("\x00" + user + "\x00" + pass))
	fmt.Fprintf(c.conn, "AUTHENTICATE \"PLAIN\" \"%s\"\r\n", token)
	if err := c.skipOK(); err != nil {
		c.conn.Close()
		return nil, fmt.Errorf("auth: %w", err)
	}
	return c, nil
}

// Close shuts down the connection.
func (c *Client) Close() { c.conn.Close() }

func (c *Client) line() (string, error) {
	l, err := c.r.ReadString('\n')
	return strings.TrimRight(l, "\r\n"), err
}

// skipOK reads response lines until OK (returns nil) or NO/BYE (returns error).
func (c *Client) skipOK() error {
	for {
		l, err := c.line()
		if err != nil {
			return err
		}
		upper := strings.ToUpper(l)
		if strings.HasPrefix(upper, "OK") {
			return nil
		}
		if strings.HasPrefix(upper, "NO") || strings.HasPrefix(upper, "BYE") {
			return fmt.Errorf("server: %s", l)
		}
	}
}

// quoteName escapes a string for use as a ManageSieve quoted-string (RFC 5804)
// and rejects CR/LF to prevent command injection into the protocol stream.
func quoteName(s string) (string, error) {
	if strings.ContainsAny(s, "\r\n\x00") {
		return "", fmt.Errorf("ungültiger Name")
	}
	s = strings.ReplaceAll(s, "\\", "\\\\")
	s = strings.ReplaceAll(s, "\"", "\\\"")
	return "\"" + s + "\"", nil
}

// readLiteral reads {N+} literal content into a string.
func (c *Client) readLiteral(line string) (string, error) {
	n, err := strconv.Atoi(strings.Trim(line, "{+}\r\n"))
	if err != nil {
		return "", err
	}
	buf := make([]byte, n)
	if _, err := io.ReadFull(c.r, buf); err != nil {
		return "", err
	}
	c.line() // consume trailing CRLF after literal
	return string(buf), nil
}

// ListScripts returns all scripts and which one is active.
func (c *Client) ListScripts() ([]Script, error) {
	fmt.Fprintf(c.conn, "LISTSCRIPTS\r\n")
	var out []Script
	for {
		l, err := c.line()
		if err != nil {
			return nil, err
		}
		up := strings.ToUpper(l)
		if strings.HasPrefix(up, "OK") {
			break
		}
		if strings.HasPrefix(up, "NO") {
			return nil, fmt.Errorf("%s", l)
		}
		active := strings.HasSuffix(up, " ACTIVE")
		name := strings.TrimSuffix(l, " ACTIVE")
		name = strings.TrimSuffix(name, " active")
		name = strings.Trim(name, "\" ")
		if name != "" {
			out = append(out, Script{Name: name, Active: active})
		}
	}
	return out, nil
}

// GetScript fetches the source of a named script.
func (c *Client) GetScript(name string) (string, error) {
	qn, err := quoteName(name)
	if err != nil {
		return "", err
	}
	fmt.Fprintf(c.conn, "GETSCRIPT %s\r\n", qn)
	l, err := c.line()
	if err != nil {
		return "", err
	}
	if strings.HasPrefix(strings.ToUpper(l), "NO") {
		return "", fmt.Errorf("%s", l)
	}
	if !strings.HasPrefix(l, "{") {
		return "", fmt.Errorf("unexpected response: %s", l)
	}
	content, err := c.readLiteral(l)
	if err != nil {
		return "", err
	}
	c.skipOK()
	return content, nil
}

// PutScript creates or replaces a script.
func (c *Client) PutScript(name, content string) error {
	qn, err := quoteName(name)
	if err != nil {
		return err
	}
	fmt.Fprintf(c.conn, "PUTSCRIPT %s {%d+}\r\n%s\r\n", qn, len(content), content)
	return c.skipOK()
}

// SetActive marks a script as the active filter.
func (c *Client) SetActive(name string) error {
	qn, err := quoteName(name)
	if err != nil {
		return err
	}
	fmt.Fprintf(c.conn, "SETACTIVE %s\r\n", qn)
	return c.skipOK()
}

// DeleteScript removes a script from the server.
func (c *Client) DeleteScript(name string) error {
	qn, err := quoteName(name)
	if err != nil {
		return err
	}
	fmt.Fprintf(c.conn, "DELETESCRIPT %s\r\n", qn)
	return c.skipOK()
}

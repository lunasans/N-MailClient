// Package mailcow is a thin client for the mailcow admin API (Modus A: eigener API-Key).
package mailcow

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

type Client struct {
	base string
	key  string
	hc   *http.Client
}

// New builds a client from a host (with or without scheme) and API key.
func New(host, key string) *Client {
	host = strings.TrimSpace(host)
	host = strings.TrimPrefix(host, "https://")
	host = strings.TrimPrefix(host, "http://")
	host = strings.TrimRight(host, "/")
	return &Client{
		base: "https://" + host + "/api/v1",
		key:  strings.TrimSpace(key),
		hc:   &http.Client{Timeout: 15 * time.Second},
	}
}

func (c *Client) do(method, path string, body interface{}) ([]byte, error) {
	var r io.Reader
	if body != nil {
		b, _ := json.Marshal(body)
		r = bytes.NewReader(b)
	}
	req, err := http.NewRequest(method, c.base+path, r)
	if err != nil {
		return nil, err
	}
	req.Header.Set("X-API-Key", c.key)
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		return nil, fmt.Errorf("Zugriff verweigert – API-Key oder IP-Allowlist prüfen")
	}
	if resp.StatusCode >= 400 {
		return nil, fmt.Errorf("HTTP %d: %s", resp.StatusCode, strings.TrimSpace(string(b)))
	}
	// mailcow meldet Fehler teils als 200 mit {"type":"error",...}
	if bytes.Contains(b, []byte(`"type":"error"`)) || bytes.Contains(b, []byte(`authentication failed`)) {
		return nil, fmt.Errorf("%s", strings.TrimSpace(string(b)))
	}
	return b, nil
}

// Test verifies host + key by hitting a key-protected endpoint.
func (c *Client) Test() error {
	_, err := c.do("GET", "/get/domain/all", nil)
	return err
}

// --- Aliases --------------------------------------------------------------

type Alias struct {
	ID      any    `json:"id"`
	Address string `json:"address"`
	Goto    string `json:"goto"`
	Active  any    `json:"active"`
}

func (a Alias) IDStr() string  { return fmt.Sprintf("%v", a.ID) }
func (a Alias) IsActive() bool { s := fmt.Sprintf("%v", a.Active); return s == "1" || s == "true" }

// Aliases returns aliases whose goto targets contain mailbox (empty = all).
func (c *Client) Aliases(mailbox string) ([]Alias, error) {
	b, err := c.do("GET", "/get/alias/all", nil)
	if err != nil {
		return nil, err
	}
	var all []Alias
	if err := json.Unmarshal(b, &all); err != nil {
		return nil, err
	}
	if mailbox == "" {
		return all, nil
	}
	mb := strings.ToLower(mailbox)
	out := []Alias{}
	for _, a := range all {
		if strings.Contains(strings.ToLower(a.Goto), mb) {
			out = append(out, a)
		}
	}
	return out, nil
}

func (c *Client) AddAlias(address, goto_ string) error {
	_, err := c.do("POST", "/add/alias", map[string]string{"address": address, "goto": goto_, "active": "1"})
	return err
}

func (c *Client) SetAliasGoto(id, goto_ string) error {
	_, err := c.do("POST", "/edit/alias", map[string]interface{}{
		"items": []string{id},
		"attr":  map[string]string{"goto": goto_},
	})
	return err
}

func (c *Client) DeleteAlias(id string) error {
	_, err := c.do("POST", "/delete/alias", []string{id})
	return err
}

// --- Quota ----------------------------------------------------------------

type Quota struct {
	Bytes    int64 `json:"bytes"`
	Used     int64 `json:"used"`
	Messages int64 `json:"messages"`
}

func toInt64(v any) int64 {
	switch t := v.(type) {
	case float64:
		return int64(t)
	case string:
		n, _ := strconv.ParseInt(strings.TrimSpace(t), 10, 64)
		return n
	case json.Number:
		n, _ := t.Int64()
		return n
	}
	return 0
}

// --- App passwords --------------------------------------------------------

type AppPassword struct {
	ID     any    `json:"id"`
	Name   string `json:"name"`
	Active any    `json:"active"`
}

func (c *Client) AppPasswords(mailbox string) ([]AppPassword, error) {
	b, err := c.do("GET", "/get/app-passwd/all/"+url.PathEscape(mailbox), nil)
	if err != nil {
		return nil, err
	}
	var out []AppPassword
	if err := json.Unmarshal(b, &out); err != nil {
		return nil, err
	}
	return out, nil
}

func (c *Client) AddAppPassword(mailbox, name, pass string) error {
	_, err := c.do("POST", "/add/app-passwd", map[string]interface{}{
		"username":    mailbox,
		"app_name":    name,
		"app_passwd":  pass,
		"app_passwd2": pass,
		"active":      "1",
		"protocols":   []string{"imap", "smtp", "pop3", "sieve", "dav"},
	})
	return err
}

func (c *Client) DeleteAppPassword(id string) error {
	_, err := c.do("POST", "/delete/app-passwd", []string{id})
	return err
}

// --- Quarantine -----------------------------------------------------------

type QItem struct {
	ID      any    `json:"id"`
	Subject string `json:"subject"`
	Sender  string `json:"sender"`
	Rcpt    string `json:"rcpt"`
	Score   any    `json:"score"`
	Created any    `json:"created"`
}

func (c *Client) Quarantine(rcpt string) ([]QItem, error) {
	b, err := c.do("GET", "/get/quarantine/all", nil)
	if err != nil {
		return nil, err
	}
	var all []QItem
	if err := json.Unmarshal(b, &all); err != nil {
		return nil, err
	}
	if rcpt == "" {
		return all, nil
	}
	rc := strings.ToLower(rcpt)
	out := []QItem{}
	for _, q := range all {
		if strings.Contains(strings.ToLower(q.Rcpt), rc) {
			out = append(out, q)
		}
	}
	return out, nil
}

func (c *Client) QuarantineDelete(id string) error {
	_, err := c.do("POST", "/delete/qitem", []string{id})
	return err
}

// QuarantineAction runs "release" or "learnham" on an item (best-effort).
func (c *Client) QuarantineAction(id, action string) error {
	_, err := c.do("POST", "/edit/qitem", map[string]interface{}{
		"items": []string{id},
		"attr":  map[string]string{"action": action},
	})
	return err
}

func (c *Client) Quota(email string) (Quota, error) {
	b, err := c.do("GET", "/get/mailbox/"+url.PathEscape(email), nil)
	if err != nil {
		return Quota{}, err
	}
	var m map[string]any
	if json.Unmarshal(b, &m) != nil {
		var arr []map[string]any
		if json.Unmarshal(b, &arr) == nil && len(arr) > 0 {
			m = arr[0]
		}
	}
	return Quota{Bytes: toInt64(m["quota"]), Used: toInt64(m["quota_used"]), Messages: toInt64(m["messages"])}, nil
}

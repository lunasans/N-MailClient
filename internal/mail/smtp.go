package mail

import (
	"bytes"
	"crypto/tls"
	"encoding/base64"
	"fmt"
	"net/smtp"
	"regexp"
	"time"

	gomail "github.com/emersion/go-message/mail"

	"n-mailclient-go/internal/store"
)

// SendAttachment is a file to attach to an outgoing message.
type SendAttachment struct {
	Filename string `json:"filename"`
	MimeType string `json:"mimeType"`
	Data     string `json:"data"` // base64-encoded bytes
}

// SendRequest is the payload the composer sends.
type SendRequest struct {
	AccountID   string           `json:"accountId"`
	From        string           `json:"from"`
	To          string           `json:"to"`
	Cc          string           `json:"cc"`
	Bcc         string           `json:"bcc"`
	Subject     string           `json:"subject"`
	Text        string           `json:"text"`
	HTML        string           `json:"html"`
	InReplyTo   string           `json:"inReplyTo"`
	References  []string         `json:"references"`
	Attachments []SendAttachment `json:"attachments"`
	RequestDSN  bool             `json:"requestDSN"`  // request read receipt
}

var emailRe = regexp.MustCompile(`[^\s<>,";]+@[^\s<>,";]+\.[^\s<>,";]+`)

func parseEmails(parts ...string) []string {
	seen := map[string]bool{}
	var out []string
	for _, p := range parts {
		for _, e := range emailRe.FindAllString(p, -1) {
			if !seen[e] {
				seen[e] = true
				out = append(out, e)
			}
		}
	}
	return out
}

func parseAddrs(s string) []*gomail.Address {
	var out []*gomail.Address
	for _, e := range emailRe.FindAllString(s, -1) {
		out = append(out, &gomail.Address{Address: e})
	}
	return out
}

// buildMIME assembles an RFC822 message (multipart/alternative for text+html).
func buildMIME(a store.Account, req SendRequest) ([]byte, error) {
	var buf bytes.Buffer
	var h gomail.Header
	h.SetDate(time.Now())
	from := req.From
	if from == "" {
		from = a.Email
	}
	h.SetAddressList("From", parseAddrs(from))
	h.SetAddressList("To", parseAddrs(req.To))
	if req.Cc != "" {
		h.SetAddressList("Cc", parseAddrs(req.Cc))
	}
	h.SetSubject(req.Subject)
	if req.InReplyTo != "" {
		h.Set("In-Reply-To", req.InReplyTo)
	}
	if len(req.References) > 0 {
		refs := ""
		for _, r := range req.References {
			refs += r + " "
		}
		h.Set("References", refs)
	}
	if req.RequestDSN {
		notify := req.From
		if notify == "" {
			notify = a.Email
		}
		h.Set("Disposition-Notification-To", notify)
	}

	mw, err := gomail.CreateWriter(&buf, h)
	if err != nil {
		return nil, err
	}
	tw, err := mw.CreateInline()
	if err != nil {
		return nil, err
	}
	// text/plain
	var th gomail.InlineHeader
	th.Set("Content-Type", "text/plain; charset=UTF-8")
	if w, err := tw.CreatePart(th); err == nil {
		_, _ = w.Write([]byte(req.Text))
		_ = w.Close()
	}
	// text/html (optional)
	if req.HTML != "" {
		var hh gomail.InlineHeader
		hh.Set("Content-Type", "text/html; charset=UTF-8")
		if w, err := tw.CreatePart(hh); err == nil {
			_, _ = w.Write([]byte(req.HTML))
			_ = w.Close()
		}
	}
	_ = tw.Close()
	// Attachments — if present, CreateWriter wraps the whole message in multipart/mixed.
	for _, att := range req.Attachments {
		data, err := base64.StdEncoding.DecodeString(att.Data)
		if err != nil {
			continue
		}
		var ah gomail.AttachmentHeader
		ah.SetFilename(att.Filename)
		ct := att.MimeType
		if ct == "" {
			ct = "application/octet-stream"
		}
		ah.Set("Content-Type", ct)
		if w, err := mw.CreateAttachment(ah); err == nil {
			_, _ = w.Write(data)
			_ = w.Close()
		}
	}
	_ = mw.Close()
	return buf.Bytes(), nil
}

// Send delivers the message and returns the raw bytes (for the Sent copy).
func Send(a store.Account, req SendRequest) ([]byte, error) {
	raw, err := buildMIME(a, req)
	if err != nil {
		return nil, err
	}
	rcpts := parseEmails(req.To, req.Cc, req.Bcc)
	if len(rcpts) == 0 {
		return nil, fmt.Errorf("kein Empfänger")
	}
	addr := fmt.Sprintf("%s:%d", a.SMTPHost, a.SMTPPort)
	auth := smtp.PlainAuth("", a.User, a.Password, a.SMTPHost)

	if a.SMTPPort == 465 {
		if err := sendImplicitTLS(addr, a.SMTPHost, auth, a.Email, rcpts, raw); err != nil {
			return nil, err
		}
		return raw, nil
	}
	if err := smtp.SendMail(addr, auth, a.Email, rcpts, raw); err != nil {
		return nil, err
	}
	return raw, nil
}

func sendImplicitTLS(addr, host string, auth smtp.Auth, from string, rcpts []string, msg []byte) error {
	conn, err := tls.Dial("tcp", addr, &tls.Config{ServerName: host})
	if err != nil {
		return err
	}
	c, err := smtp.NewClient(conn, host)
	if err != nil {
		return err
	}
	defer c.Quit()
	if err := c.Auth(auth); err != nil {
		return err
	}
	if err := c.Mail(from); err != nil {
		return err
	}
	for _, r := range rcpts {
		if err := c.Rcpt(r); err != nil {
			return err
		}
	}
	w, err := c.Data()
	if err != nil {
		return err
	}
	if _, err := w.Write(msg); err != nil {
		return err
	}
	return w.Close()
}

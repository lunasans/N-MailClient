package mail

import (
	"crypto/tls"
	"encoding/xml"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// ProbeResult is a best-effort guess of a domain's mail servers.
//
// The port implies the transport security expected by the client:
//   - IMAP 993 / SMTP 465 → implicit TLS
//   - IMAP 143 / SMTP 587 → STARTTLS
type ProbeResult struct {
	IMAPHost string `json:"imapHost"`
	IMAPPort int    `json:"imapPort"`
	SMTPHost string `json:"smtpHost"`
	SMTPPort int    `json:"smtpPort"`
	Source   string `json:"source"` // "autoconfig", "srv", "guess", or "" (invalid input)
}

// Autodiscover resolves mail server settings for an email address using, in order:
//  1. Mozilla autoconfig (provider-hosted + Thunderbird ISPDB)
//  2. DNS SRV records (RFC 6186)
//  3. Hostname guessing verified by a TLS dial
func Autodiscover(email string) ProbeResult {
	at := strings.LastIndex(email, "@")
	if at < 0 {
		return ProbeResult{IMAPPort: 993, SMTPPort: 465}
	}
	domain := strings.ToLower(email[at+1:])

	if r, ok := autoconfigLookup(domain, email); ok {
		r.Source = "autoconfig"
		return r
	}
	if r, ok := srvLookup(domain); ok {
		r.Source = "srv"
		return r
	}
	r := guess(domain)
	r.Source = "guess"
	return r
}

// --- 1. Mozilla autoconfig / ISPDB ----------------------------------------

// clientConfig mirrors the Mozilla autoconfig "clientConfig" XML schema.
type clientConfig struct {
	XMLName       xml.Name `xml:"clientConfig"`
	EmailProvider struct {
		Incoming []serverCfg `xml:"incomingServer"`
		Outgoing []serverCfg `xml:"outgoingServer"`
	} `xml:"emailProvider"`
}

type serverCfg struct {
	Type       string `xml:"type,attr"`
	Hostname   string `xml:"hostname"`
	Port       int    `xml:"port"`
	SocketType string `xml:"socketType"`
}

func (cc *clientConfig) toResult() (ProbeResult, bool) {
	var r ProbeResult
	for _, s := range cc.EmailProvider.Incoming {
		if strings.EqualFold(s.Type, "imap") && s.Hostname != "" && s.Port > 0 {
			r.IMAPHost, r.IMAPPort = s.Hostname, s.Port
			break
		}
	}
	for _, s := range cc.EmailProvider.Outgoing {
		if strings.EqualFold(s.Type, "smtp") && s.Hostname != "" && s.Port > 0 {
			r.SMTPHost, r.SMTPPort = s.Hostname, s.Port
			break
		}
	}
	return r, r.IMAPHost != "" && r.SMTPHost != ""
}

func autoconfigLookup(domain, email string) (ProbeResult, bool) {
	esc := url.QueryEscape(email)
	// HTTPS-only on purpose: server settings from an unauthenticated channel
	// could be tampered with to redirect mail through an attacker's host.
	urls := []string{
		"https://autoconfig." + domain + "/mail/config-v1.1.xml?emailaddress=" + esc,
		"https://" + domain + "/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress=" + esc,
		"https://autoconfig.thunderbird.net/v1.1/" + domain, // Mozilla ISPDB
	}
	for _, u := range urls {
		if cc, ok := fetchAutoconfig(u); ok {
			if r, ok := cc.toResult(); ok {
				return r, true
			}
		}
	}
	return ProbeResult{}, false
}

func fetchAutoconfig(rawURL string) (*clientConfig, bool) {
	client := &http.Client{Timeout: 5 * time.Second}
	resp, err := client.Get(rawURL)
	if err != nil {
		return nil, false
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, false
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return nil, false
	}
	var cc clientConfig
	if err := xml.Unmarshal(b, &cc); err != nil {
		return nil, false
	}
	return &cc, true
}

// --- 2. DNS SRV records (RFC 6186) ----------------------------------------

func srvLookup(domain string) (ProbeResult, bool) {
	var r ProbeResult
	// IMAP: prefer implicit TLS (imaps), fall back to STARTTLS (imap).
	if h, p, ok := lookupSRV("imaps", domain); ok {
		r.IMAPHost, r.IMAPPort = h, p
	} else if h, p, ok := lookupSRV("imap", domain); ok {
		r.IMAPHost, r.IMAPPort = h, p
	}
	// SMTP submission: prefer implicit TLS (submissions/465), then STARTTLS (submission/587).
	if h, p, ok := lookupSRV("submissions", domain); ok {
		r.SMTPHost, r.SMTPPort = h, p
	} else if h, p, ok := lookupSRV("submission", domain); ok {
		r.SMTPHost, r.SMTPPort = h, p
	}
	return r, r.IMAPHost != "" && r.SMTPHost != ""
}

func lookupSRV(service, domain string) (string, int, bool) {
	_, addrs, err := net.LookupSRV(service, "tcp", domain)
	if err != nil || len(addrs) == 0 {
		return "", 0, false
	}
	target := strings.TrimSuffix(addrs[0].Target, ".")
	// RFC 6186/2782: a single "." target means "service not available".
	if target == "" || addrs[0].Port == 0 {
		return "", 0, false
	}
	return target, int(addrs[0].Port), true
}

// --- 3. Hostname guessing (verified by TLS) -------------------------------

func guess(domain string) ProbeResult {
	res := ProbeResult{IMAPHost: "mail." + domain, IMAPPort: 993, SMTPHost: "mail." + domain, SMTPPort: 465}
	for _, h := range []string{"imap." + domain, "mail." + domain} {
		if dialOK(h, 993) {
			res.IMAPHost = h
			break
		}
	}
	for _, h := range []string{"smtp." + domain, "mail." + domain} {
		if dialOK(h, 465) {
			res.SMTPHost = h
			break
		}
	}
	return res
}

func dialOK(host string, port int) bool {
	d := &net.Dialer{Timeout: 4 * time.Second}
	conn, err := tls.DialWithDialer(d, "tcp", net.JoinHostPort(host, fmt.Sprintf("%d", port)), &tls.Config{ServerName: host})
	if err != nil {
		return false
	}
	_ = conn.Close()
	return true
}

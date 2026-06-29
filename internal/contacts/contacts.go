package contacts

import (
	"encoding/xml"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Contact is a single vCard contact.
type Contact struct {
	UID      string `json:"uid"`
	Href     string `json:"href"`
	ETag     string `json:"etag"`
	Name     string `json:"name"`
	Email    string `json:"email"`
	Phone    string `json:"phone"`
	Notes    string `json:"notes"`
	Birthday string `json:"birthday"` // YYYY-MM-DD or empty
}

func httpClient() *http.Client { return &http.Client{Timeout: 15 * time.Second} }

// propfind runs a WebDAV PROPFIND at depth 1 and returns href+etag+content-type pairs.
func propfind(baseURL, user, pass string) ([]struct{ Href, ETag, CT string }, error) {
	body := `<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:getetag/><d:getcontenttype/></d:prop></d:propfind>`
	req, _ := http.NewRequest("PROPFIND", baseURL, strings.NewReader(body))
	req.SetBasicAuth(user, pass)
	req.Header.Set("Depth", "1")
	req.Header.Set("Content-Type", "application/xml; charset=utf-8")
	resp, err := httpClient().Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)

	var ms struct {
		XMLName xml.Name `xml:"multistatus"`
		Items   []struct {
			Href     string `xml:"href"`
			PropStat []struct {
				Prop struct {
					ETag string `xml:"getetag"`
					CT   string `xml:"getcontenttype"`
				} `xml:"prop"`
			} `xml:"propstat"`
		} `xml:"response"`
	}
	xml.Unmarshal(b, &ms)

	base, _ := url.Parse(baseURL)
	var out []struct{ Href, ETag, CT string }
	for _, item := range ms.Items {
		ref, _ := url.Parse(item.Href)
		full := base.ResolveReference(ref).String()
		etag, ct := "", ""
		for _, ps := range item.PropStat {
			if ps.Prop.ETag != "" {
				etag = ps.Prop.ETag
			}
			if ps.Prop.CT != "" {
				ct = ps.Prop.CT
			}
		}
		out = append(out, struct{ Href, ETag, CT string }{full, etag, ct})
	}
	return out, nil
}

// List fetches all contacts from a CardDAV address-book URL.
func List(baseURL, user, pass string) ([]Contact, error) {
	items, err := propfind(baseURL, user, pass)
	if err != nil {
		return nil, err
	}
	var contacts []Contact
	for _, item := range items {
		if !strings.Contains(item.CT, "vcard") && !strings.HasSuffix(item.Href, ".vcf") {
			continue
		}
		req, _ := http.NewRequest("GET", item.Href, nil)
		req.SetBasicAuth(user, pass)
		resp, err := httpClient().Do(req)
		if err != nil {
			continue
		}
		b, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		c := parseVCard(string(b))
		c.Href = item.Href
		c.ETag = item.ETag
		contacts = append(contacts, c)
	}
	return contacts, nil
}

func parseVCard(s string) Contact {
	var c Contact
	for _, raw := range strings.Split(s, "\n") {
		line := strings.TrimRight(raw, "\r")
		kv := strings.SplitN(line, ":", 2)
		if len(kv) != 2 {
			continue
		}
		key := strings.ToUpper(strings.Split(kv[0], ";")[0])
		val := strings.TrimSpace(kv[1])
		switch key {
		case "UID":
			c.UID = val
		case "FN":
			c.Name = val
		case "EMAIL":
			if c.Email == "" {
				c.Email = val
			}
		case "TEL":
			if c.Phone == "" {
				c.Phone = val
			}
		case "NOTE":
			c.Notes = val
		case "BDAY":
			// Normalise YYYYMMDD → YYYY-MM-DD
			if len(val) == 8 {
				val = val[:4] + "-" + val[4:6] + "-" + val[6:]
			}
			c.Birthday = val
		}
	}
	return c
}

func buildVCard(c Contact) string {
	uid := c.UID
	if uid == "" {
		uid = fmt.Sprintf("%d", time.Now().UnixNano())
		c.UID = uid
	}
	var sb strings.Builder
	sb.WriteString("BEGIN:VCARD\r\nVERSION:3.0\r\n")
	sb.WriteString("UID:" + uid + "\r\n")
	sb.WriteString("FN:" + c.Name + "\r\n")
	if c.Email != "" {
		sb.WriteString("EMAIL;TYPE=INTERNET:" + c.Email + "\r\n")
	}
	if c.Phone != "" {
		sb.WriteString("TEL;TYPE=VOICE:" + c.Phone + "\r\n")
	}
	if c.Notes != "" {
		sb.WriteString("NOTE:" + c.Notes + "\r\n")
	}
	if c.Birthday != "" {
		sb.WriteString("BDAY:" + strings.ReplaceAll(c.Birthday, "-", "") + "\r\n")
	}
	sb.WriteString("END:VCARD\r\n")
	return sb.String()
}

// Save creates or updates a contact via HTTP PUT.
func Save(baseURL, user, pass string, c Contact) error {
	if c.UID == "" {
		c.UID = fmt.Sprintf("%d", time.Now().UnixNano())
	}
	href := c.Href
	if href == "" {
		href = strings.TrimRight(baseURL, "/") + "/" + c.UID + ".vcf"
	}
	body := buildVCard(c)
	req, _ := http.NewRequest("PUT", href, strings.NewReader(body))
	req.SetBasicAuth(user, pass)
	req.Header.Set("Content-Type", "text/vcard; charset=utf-8")
	if c.ETag != "" {
		req.Header.Set("If-Match", c.ETag)
	}
	resp, err := httpClient().Do(req)
	if err != nil {
		return err
	}
	resp.Body.Close()
	if resp.StatusCode >= 300 {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	return nil
}

// Delete removes a contact.
func Delete(href, etag, user, pass string) error {
	req, _ := http.NewRequest("DELETE", href, nil)
	req.SetBasicAuth(user, pass)
	if etag != "" {
		req.Header.Set("If-Match", etag)
	}
	resp, err := httpClient().Do(req)
	if err != nil {
		return err
	}
	resp.Body.Close()
	return nil
}

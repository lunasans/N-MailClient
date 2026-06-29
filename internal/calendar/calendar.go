package calendar

import (
	"encoding/xml"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Event is a single calendar entry.
type Event struct {
	UID    string `json:"uid"`
	Href   string `json:"href"`
	ETag   string `json:"etag"`
	Title  string `json:"title"`
	Start  string `json:"start"` // RFC3339 or YYYY-MM-DD for all-day
	End    string `json:"end"`
	AllDay bool   `json:"allDay"`
}

func httpClient() *http.Client { return &http.Client{Timeout: 15 * time.Second} }

// List fetches all events from a CalDAV calendar URL.
func List(calURL, user, pass string) ([]Event, error) {
	body := `<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:getetag/><d:getcontenttype/></d:prop></d:propfind>`
	req, _ := http.NewRequest("PROPFIND", calURL, strings.NewReader(body))
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

	base, _ := url.Parse(calURL)
	var events []Event
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
		if !strings.Contains(ct, "calendar") && !strings.HasSuffix(full, ".ics") {
			continue
		}
		req2, _ := http.NewRequest("GET", full, nil)
		req2.SetBasicAuth(user, pass)
		resp2, err := httpClient().Do(req2)
		if err != nil {
			continue
		}
		ics, _ := io.ReadAll(resp2.Body)
		resp2.Body.Close()
		ev := parseICS(string(ics))
		ev.Href = full
		ev.ETag = etag
		events = append(events, ev)
	}
	return events, nil
}

func parseICS(s string) Event {
	var ev Event
	inEvent := false
	for _, raw := range strings.Split(s, "\n") {
		line := strings.TrimRight(raw, "\r")
		if line == "BEGIN:VEVENT" {
			inEvent = true
			continue
		}
		if line == "END:VEVENT" {
			break
		}
		if !inEvent {
			continue
		}
		kv := strings.SplitN(line, ":", 2)
		if len(kv) != 2 {
			continue
		}
		params := kv[0]
		key := strings.ToUpper(strings.Split(params, ";")[0])
		val := strings.TrimSpace(kv[1])
		switch key {
		case "UID":
			ev.UID = val
		case "SUMMARY":
			ev.Title = val
		case "DTSTART":
			ev.AllDay = strings.Contains(params, "VALUE=DATE") || len(val) == 8
			ev.Start = parseICSDate(val, ev.AllDay)
		case "DTEND":
			allDay := strings.Contains(params, "VALUE=DATE") || len(val) == 8
			ev.End = parseICSDate(val, allDay)
		}
	}
	return ev
}

func parseICSDate(val string, allDay bool) string {
	if allDay && len(val) >= 8 {
		return val[:4] + "-" + val[4:6] + "-" + val[6:8]
	}
	for _, layout := range []string{"20060102T150405Z", "20060102T150405"} {
		if t, err := time.Parse(layout, val); err == nil {
			return t.Format(time.RFC3339)
		}
	}
	return val
}

func buildICS(ev Event) string {
	uid := ev.UID
	if uid == "" {
		uid = fmt.Sprintf("%d", time.Now().UnixNano())
	}
	now := time.Now().UTC().Format("20060102T150405Z")

	var dtstart, dtend string
	if ev.AllDay {
		s := strings.ReplaceAll(ev.Start, "-", "")
		e := strings.ReplaceAll(ev.End, "-", "")
		if len(s) > 8 {
			s = s[:8]
		}
		if len(e) > 8 {
			e = e[:8]
		}
		dtstart = "DTSTART;VALUE=DATE:" + s
		dtend = "DTEND;VALUE=DATE:" + e
	} else {
		t, _ := time.Parse(time.RFC3339, ev.Start)
		dtstart = "DTSTART:" + t.UTC().Format("20060102T150405Z")
		t2, _ := time.Parse(time.RFC3339, ev.End)
		dtend = "DTEND:" + t2.UTC().Format("20060102T150405Z")
	}

	return fmt.Sprintf(
		"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//N-MailClient//Go//EN\r\n"+
			"BEGIN:VEVENT\r\nUID:%s\r\nDTSTAMP:%s\r\nSUMMARY:%s\r\n%s\r\n%s\r\n"+
			"END:VEVENT\r\nEND:VCALENDAR\r\n",
		uid, now, ev.Title, dtstart, dtend)
}

// Save creates or updates a calendar event via HTTP PUT.
func Save(calURL, user, pass string, ev Event) error {
	if ev.UID == "" {
		ev.UID = fmt.Sprintf("%d", time.Now().UnixNano())
	}
	href := ev.Href
	if href == "" {
		href = strings.TrimRight(calURL, "/") + "/" + ev.UID + ".ics"
	}
	body := buildICS(ev)
	req, _ := http.NewRequest("PUT", href, strings.NewReader(body))
	req.SetBasicAuth(user, pass)
	req.Header.Set("Content-Type", "text/calendar; charset=utf-8")
	if ev.ETag != "" {
		req.Header.Set("If-Match", ev.ETag)
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

// Delete removes a calendar event.
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

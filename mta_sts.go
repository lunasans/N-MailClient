package main

import (
	"io"
	"net/http"
	"strings"
	"time"
)

// CheckMTASTS fetches the MTA-STS policy for a domain and returns the
// enforcement mode: "enforce", "testing", or "none".
func (a *App) CheckMTASTS(domain string) string {
	url := "https://mta-sts." + domain + "/.well-known/mta-sts.txt"
	client := &http.Client{Timeout: 5 * time.Second}
	resp, err := client.Get(url)
	if err != nil || resp.StatusCode != 200 {
		return "none"
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(io.LimitReader(resp.Body, 4096))
	if err != nil {
		return "none"
	}
	for _, line := range strings.Split(string(b), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "mode:") {
			mode := strings.TrimSpace(strings.TrimPrefix(line, "mode:"))
			switch mode {
			case "enforce", "testing":
				return mode
			}
		}
	}
	return "none"
}

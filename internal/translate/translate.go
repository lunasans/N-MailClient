package translate

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

// Translate sends text to a LibreTranslate server and returns the translated text.
// sourceLang may be "auto". targetLang is an ISO 639-1 code (e.g. "de", "en").
func Translate(text, sourceLang, targetLang, serverURL, apiKey string) (string, error) {
	body := map[string]string{
		"q":      text,
		"source": sourceLang,
		"target": targetLang,
		"format": "text",
	}
	if apiKey != "" {
		body["api_key"] = apiKey
	}
	b, _ := json.Marshal(body)
	req, err := http.NewRequest("POST", serverURL+"/translate", bytes.NewReader(b))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := (&http.Client{Timeout: 30 * time.Second}).Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	rb, _ := io.ReadAll(resp.Body)
	var result struct {
		TranslatedText string `json:"translatedText"`
		Error          string `json:"error"`
	}
	if err := json.Unmarshal(rb, &result); err != nil {
		return "", fmt.Errorf("ungültige Antwort: %s", string(rb))
	}
	if result.Error != "" {
		return "", fmt.Errorf("%s", result.Error)
	}
	return result.TranslatedText, nil
}

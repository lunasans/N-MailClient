package mail

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strconv"
)

// Offline cache: the newest page of message summaries per account+folder is
// persisted as JSON so the list can be shown instantly and survives being
// offline. Cache lives in <UserConfigDir>/n-mailclient-go/cache.

func cacheFile(accountID, folder string) (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	p := filepath.Join(dir, "n-mailclient-go", "cache")
	if err := os.MkdirAll(p, 0o700); err != nil {
		return "", err
	}
	// hex-encode the folder name so arbitrary IMAP delimiters/chars are filesystem-safe.
	name := accountID + "_" + hex.EncodeToString([]byte(folder)) + ".json"
	return filepath.Join(p, name), nil
}

func saveCache(accountID, folder string, s []Summary) {
	f, err := cacheFile(accountID, folder)
	if err != nil {
		return
	}
	b, err := json.Marshal(s)
	if err != nil {
		return
	}
	_ = os.WriteFile(f, b, 0o600)
}

// LoadCache returns the cached summaries for an account+folder, or nil if none.
func LoadCache(accountID, folder string) []Summary {
	f, err := cacheFile(accountID, folder)
	if err != nil {
		return nil
	}
	b, err := os.ReadFile(f)
	if err != nil {
		return nil
	}
	var s []Summary
	if json.Unmarshal(b, &s) != nil {
		return nil
	}
	return s
}

func detailCacheFile(accountID, folder string, uid uint32) (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	p := filepath.Join(dir, "n-mailclient-go", "cache", "bodies")
	if err := os.MkdirAll(p, 0o700); err != nil {
		return "", err
	}
	name := accountID + "_" + hex.EncodeToString([]byte(folder)) + "_" + strconv.FormatUint(uint64(uid), 10) + ".json"
	return filepath.Join(p, name), nil
}

func saveDetailCache(accountID, folder string, uid uint32, d *Detail) {
	f, err := detailCacheFile(accountID, folder, uid)
	if err != nil {
		return
	}
	b, err := json.Marshal(d)
	if err != nil {
		return
	}
	_ = os.WriteFile(f, b, 0o600)
}

// LoadDetailCache returns the cached full message for an account+folder+uid, or nil.
func LoadDetailCache(accountID, folder string, uid uint32) *Detail {
	f, err := detailCacheFile(accountID, folder, uid)
	if err != nil {
		return nil
	}
	b, err := os.ReadFile(f)
	if err != nil {
		return nil
	}
	var d Detail
	if json.Unmarshal(b, &d) != nil {
		return nil
	}
	return &d
}

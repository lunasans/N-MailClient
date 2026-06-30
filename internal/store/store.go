package store

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"sync"

	"github.com/zalando/go-keyring"
)

// keyringService is the namespace under which account passwords are stored in
// the OS keychain (Windows Credential Manager / macOS Keychain / Secret Service).
const keyringService = "n-mailclient-go"

// Account holds connection details for one mailbox.
//
// The password is kept in memory and in the OS keychain only; it is never
// written to db.json on disk (the Password field is blanked before persisting).
type Account struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Email     string `json:"email"`
	User      string `json:"user"`
	Password  string `json:"password"`
	IMAPHost  string `json:"imapHost"`
	IMAPPort  int    `json:"imapPort"`
	SMTPHost  string `json:"smtpHost"`
	SMTPPort  int    `json:"smtpPort"`
	Signature  string `json:"signature"`
	Color      string `json:"color"`
	SieveHost  string `json:"sieveHost"`  // optional; falls back to IMAPHost
	SievePort  int    `json:"sievePort"`  // optional; falls back to 4190
	CardDAVURL string `json:"cardDavUrl"` // address-book base URL
	CalDAVURL  string `json:"calDavUrl"`  // calendar base URL
	WebDAVURL  string `json:"webDavUrl"`  // attachment archive WebDAV target (optional)
	ArchiveDir string `json:"archiveDir"` // local attachment-archive folder (empty = default)
}

type Store struct {
	mu       sync.Mutex
	path     string
	Accounts []Account `json:"accounts"`
}

func New() *Store {
	dir, err := os.UserConfigDir()
	if err != nil {
		dir = "."
	}
	base := filepath.Join(dir, "n-mailclient-go")
	_ = os.MkdirAll(base, 0o700)
	return &Store{path: filepath.Join(base, "db.json")}
}

func (s *Store) Load() {
	s.mu.Lock()
	defer s.mu.Unlock()
	b, err := os.ReadFile(s.path)
	if err != nil {
		return
	}
	if err := json.Unmarshal(b, s); err != nil {
		return
	}
	migrated := false
	for i := range s.Accounts {
		if s.Accounts[i].Password != "" {
			// Legacy plaintext password from an older db.json → migrate to keychain.
			_ = keyring.Set(keyringService, s.Accounts[i].ID, s.Accounts[i].Password)
			migrated = true
			continue
		}
		if pw, err := keyring.Get(keyringService, s.Accounts[i].ID); err == nil {
			s.Accounts[i].Password = pw
		}
	}
	if migrated {
		// Rewrite db.json without the plaintext passwords.
		s.persist()
	}
}

// persist writes db.json with passwords stripped; the real passwords are stored
// in the OS keychain instead. Callers must already hold s.mu.
func (s *Store) persist() {
	clean := make([]Account, len(s.Accounts))
	copy(clean, s.Accounts)
	for i := range clean {
		if clean[i].Password != "" {
			_ = keyring.Set(keyringService, clean[i].ID, clean[i].Password)
		}
		clean[i].Password = ""
	}
	b, _ := json.MarshalIndent(struct {
		Accounts []Account `json:"accounts"`
	}{clean}, "", "  ")
	_ = os.WriteFile(s.path, b, 0o600)
}

func (s *Store) List() []Account {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]Account, len(s.Accounts))
	copy(out, s.Accounts)
	return out
}

func (s *Store) Get(id string) (Account, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, a := range s.Accounts {
		if a.ID == id {
			return a, nil
		}
	}
	return Account{}, errors.New("Konto nicht gefunden")
}

func (s *Store) Add(in Account) (Account, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	in.ID = randID()
	if in.Name == "" {
		in.Name = in.Email
	}
	if in.User == "" {
		in.User = in.Email
	}
	s.Accounts = append(s.Accounts, in)
	s.persist()
	return in, nil
}

func (s *Store) Update(in Account) (Account, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i, a := range s.Accounts {
		if a.ID == in.ID {
			// Keep the existing password when the update leaves it empty.
			if in.Password == "" {
				in.Password = a.Password
			}
			s.Accounts[i] = in
			s.persist()
			return in, nil
		}
	}
	return Account{}, errors.New("Konto nicht gefunden")
}

func (s *Store) Remove(id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := s.Accounts[:0]
	for _, a := range s.Accounts {
		if a.ID != id {
			out = append(out, a)
		}
	}
	s.Accounts = out
	_ = keyring.Delete(keyringService, id)
	s.persist()
	return nil
}

func randID() string {
	b := make([]byte, 8)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

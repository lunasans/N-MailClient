package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/crypto/openpgp"        //nolint:staticcheck
	"golang.org/x/crypto/openpgp/armor"  //nolint:staticcheck
	"golang.org/x/crypto/openpgp/packet" //nolint:staticcheck
)

type PGPKeyInfo struct {
	Fingerprint string `json:"fingerprint"`
	Name        string `json:"name"`
	Email       string `json:"email"`
	IsPrivate   bool   `json:"isPrivate"`
}

func pgpDir() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	p := filepath.Join(dir, "n-mailclient-go", "pgp")
	return p, os.MkdirAll(p, 0o700)
}

func pgpIndexPath() (string, error) {
	d, err := pgpDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(d, "index.json"), nil
}

func pgpLoadIndex() ([]PGPKeyInfo, error) {
	p, err := pgpIndexPath()
	if err != nil {
		return nil, err
	}
	b, err := os.ReadFile(p)
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var keys []PGPKeyInfo
	json.Unmarshal(b, &keys)
	return keys, nil
}

func pgpSaveIndex(keys []PGPKeyInfo) error {
	p, err := pgpIndexPath()
	if err != nil {
		return err
	}
	b, _ := json.MarshalIndent(keys, "", "  ")
	return os.WriteFile(p, b, 0o600)
}

// PGPImportKey imports an ASCII-armored PGP key (public or private).
func (a *App) PGPImportKey(armored string) (PGPKeyInfo, error) {
	block, err := armor.Decode(strings.NewReader(armored))
	if err != nil {
		return PGPKeyInfo{}, fmt.Errorf("kein gültiger PGP-Block: %w", err)
	}
	entities, err := openpgp.ReadKeyRing(block.Body)
	if err != nil || len(entities) == 0 {
		return PGPKeyInfo{}, fmt.Errorf("PGP-Schlüssel nicht lesbar: %w", err)
	}
	e := entities[0]
	fp := fmt.Sprintf("%X", e.PrimaryKey.Fingerprint)
	isPriv := e.PrivateKey != nil

	info := PGPKeyInfo{Fingerprint: fp, IsPrivate: isPriv}
	for _, uid := range e.Identities {
		info.Name = uid.UserId.Name
		info.Email = uid.UserId.Email
		break
	}

	dir, err := pgpDir()
	if err != nil {
		return info, err
	}
	// Store the raw armored block
	keyPath := filepath.Join(dir, fp+".asc")
	if err := os.WriteFile(keyPath, []byte(armored), 0o600); err != nil {
		return info, err
	}

	keys, _ := pgpLoadIndex()
	// Replace if fingerprint already exists
	replaced := false
	for i, k := range keys {
		if k.Fingerprint == fp {
			keys[i] = info
			replaced = true
			break
		}
	}
	if !replaced {
		keys = append(keys, info)
	}
	return info, pgpSaveIndex(keys)
}

// PGPListKeys returns all imported PGP key infos.
func (a *App) PGPListKeys() ([]PGPKeyInfo, error) {
	keys, err := pgpLoadIndex()
	if err != nil {
		return nil, err
	}
	if keys == nil {
		return []PGPKeyInfo{}, nil
	}
	return keys, nil
}

// PGPDeleteKey removes a key by fingerprint.
func (a *App) PGPDeleteKey(fingerprint string) error {
	keys, err := pgpLoadIndex()
	if err != nil {
		return err
	}
	out := keys[:0]
	for _, k := range keys {
		if k.Fingerprint != fingerprint {
			out = append(out, k)
		}
	}
	dir, _ := pgpDir()
	_ = os.Remove(filepath.Join(dir, fingerprint+".asc"))
	return pgpSaveIndex(out)
}

func pgpLoadEntity(fingerprint string) (*openpgp.Entity, error) {
	dir, err := pgpDir()
	if err != nil {
		return nil, err
	}
	b, err := os.ReadFile(filepath.Join(dir, fingerprint+".asc"))
	if err != nil {
		return nil, err
	}
	block, err := armor.Decode(strings.NewReader(string(b)))
	if err != nil {
		return nil, err
	}
	entities, err := openpgp.ReadKeyRing(block.Body)
	if err != nil || len(entities) == 0 {
		return nil, fmt.Errorf("Schlüssel nicht ladbar")
	}
	return entities[0], nil
}

// PGPDecryptBody decrypts a PGP-encrypted message body using any available
// private key. Returns the plaintext or an error.
func (a *App) PGPDecryptBody(ciphertext, passphrase string) (string, error) {
	keys, err := pgpLoadIndex()
	if err != nil || len(keys) == 0 {
		return "", fmt.Errorf("keine privaten Schlüssel importiert")
	}
	var ring openpgp.EntityList
	for _, k := range keys {
		if !k.IsPrivate {
			continue
		}
		e, err := pgpLoadEntity(k.Fingerprint)
		if err != nil {
			continue
		}
		if passphrase != "" && e.PrivateKey != nil && e.PrivateKey.Encrypted {
			_ = e.PrivateKey.Decrypt([]byte(passphrase))
		}
		ring = append(ring, e)
	}
	if len(ring) == 0 {
		return "", fmt.Errorf("kein privater Schlüssel verfügbar")
	}

	// Try to detect armor
	prompt := func(keys []openpgp.Key, sym bool) ([]byte, error) {
		if passphrase != "" {
			return []byte(passphrase), nil
		}
		return nil, fmt.Errorf("Passwort benötigt")
	}

	var r io.Reader
	if strings.Contains(ciphertext, "-----BEGIN PGP") {
		block, err := armor.Decode(strings.NewReader(ciphertext))
		if err != nil {
			return "", err
		}
		r = block.Body
	} else {
		r = strings.NewReader(ciphertext)
	}

	md, err := openpgp.ReadMessage(r, ring, prompt, &packet.Config{})
	if err != nil {
		return "", fmt.Errorf("Entschlüsselung fehlgeschlagen: %w", err)
	}
	plain, err := io.ReadAll(md.UnverifiedBody)
	if err != nil {
		return "", err
	}
	return string(plain), nil
}

// PGPEncryptBody encrypts plaintext for the given recipient email using
// the matching public key. Returns ASCII-armored ciphertext.
func (a *App) PGPEncryptBody(recipientEmail, plaintext string) (string, error) {
	keys, err := pgpLoadIndex()
	if err != nil {
		return "", err
	}
	var recip *openpgp.Entity
	for _, k := range keys {
		if strings.EqualFold(k.Email, recipientEmail) {
			e, err := pgpLoadEntity(k.Fingerprint)
			if err == nil {
				recip = e
				break
			}
		}
	}
	if recip == nil {
		return "", fmt.Errorf("kein öffentlicher Schlüssel für %s", recipientEmail)
	}

	var buf strings.Builder
	w, err := armor.Encode(&buf, "PGP MESSAGE", nil)
	if err != nil {
		return "", err
	}
	plain, err := openpgp.Encrypt(w, []*openpgp.Entity{recip}, nil, nil, &packet.Config{})
	if err != nil {
		return "", err
	}
	if _, err := io.WriteString(plain, plaintext); err != nil {
		return "", err
	}
	plain.Close()
	w.Close()
	return buf.String(), nil
}

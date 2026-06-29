package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"golang.org/x/sys/windows/registry"

	toast "git.sr.ht/~jackmordaunt/go-toast/v2"
	wruntime "github.com/wailsapp/wails/v2/pkg/runtime"

	"n-mailclient-go/internal/calendar"
	"n-mailclient-go/internal/contacts"
	"n-mailclient-go/internal/mail"
	"n-mailclient-go/internal/sieve"
	"n-mailclient-go/internal/store"
	"n-mailclient-go/internal/translate"
)

type calCacheEntry struct {
	events  []calendar.Event
	expires time.Time
}

// App is bound to the frontend; every exported method is callable from JS as
// window.go.main.App.<Method>(...).
type App struct {
	ctx   context.Context
	store *store.Store
	// mail-window mode (separate process launched on double-click)
	mailMode bool
	mailUID  uint32
	mailAcc  string
	mailFld  string
	mailDark bool
	// notification preferences (set by frontend via SetNotifPrefs)
	notifInboxOnly  bool
	notifQuietStart string // "HH:MM", empty = disabled
	notifQuietEnd   string // "HH:MM"
	// CalDAV cache keyed by accountID
	calCache map[string]calCacheEntry
}

// GetWindowMode returns the startup mode for the frontend.
// In mail-window mode the frontend renders a single message fullscreen.
func (a *App) GetWindowMode() map[string]interface{} {
	if a.mailMode {
		return map[string]interface{}{
			"mode": "mail",
			"uid":  a.mailUID,
			"acc":  a.mailAcc,
			"fld":  a.mailFld,
			"dark": a.mailDark,
		}
	}
	return map[string]interface{}{"mode": "main"}
}

// OpenMailWindow launches a new process showing a single message.
// dark mirrors the current dark-mode state of the calling window.
func (a *App) OpenMailWindow(uid uint32, acc, fld, subj string, dark bool) error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	darkFlag := "--dark=0"
	if dark {
		darkFlag = "--dark=1"
	}
	return exec.Command(exe,
		"--mode=mail",
		fmt.Sprintf("--uid=%d", uid),
		"--acc="+acc,
		"--fld="+fld,
		"--subj="+subj,
		darkFlag,
	).Start()
}

func NewApp() *App {
	return &App{store: store.New()}
}

func (a *App) startup(ctx context.Context) {
	a.ctx = ctx
	a.store.Load()
	go a.pollNewMail()
}

func (a *App) domReady(_ context.Context) {
	go fixDWMCaption()
}

// SetNotifPrefs is called by the frontend when notification settings change.
func (a *App) SetNotifPrefs(inboxOnly bool, quietStart, quietEnd string) {
	a.notifInboxOnly = inboxOnly
	a.notifQuietStart = quietStart
	a.notifQuietEnd = quietEnd
}

func (a *App) pollNewMail() {
	ticker := time.NewTicker(60 * time.Second)
	defer ticker.Stop()
	knownUID := map[string]uint32{}
	initialized := map[string]bool{}
	for {
		select {
		case <-a.ctx.Done():
			return
		case <-ticker.C:
			for _, acc := range a.store.List() {
				msgs, err := mail.ListMessages(acc, "INBOX", 5)
				if err != nil || len(msgs) == 0 {
					continue
				}
				newest := msgs[0].UID
				if !initialized[acc.ID] {
					knownUID[acc.ID] = newest
					initialized[acc.ID] = true
					continue
				}
				if newest > knownUID[acc.ID] {
					knownUID[acc.ID] = newest
					wruntime.EventsEmit(a.ctx, "new-mail", map[string]interface{}{
						"subject":   msgs[0].Subject,
						"from":      msgs[0].From,
						"accountId": acc.ID,
					})
					if !a.inQuietHours() {
						go notifyNewMail(msgs[0].Subject, msgs[0].From)
					}
				}
			}
		}
	}
}

func (a *App) inQuietHours() bool {
	if a.notifQuietStart == "" || a.notifQuietEnd == "" {
		return false
	}
	now := time.Now().Format("15:04")
	s, e := a.notifQuietStart, a.notifQuietEnd
	if s <= e {
		return now >= s && now < e
	}
	// overnight range e.g. 22:00 – 07:00
	return now >= s || now < e
}

func notifyNewMail(subject, from string) {
	defer func() { _ = recover() }()
	n := toast.Notification{
		AppID: "N-MailClient",
		Title: "Neue E-Mail von " + from,
		Body:  subject,
	}
	_ = n.Push()
}

// ShowNotification sends a Windows Toast for calendar reminders etc.
func (a *App) ShowNotification(title, message string) error {
	n := toast.Notification{
		AppID: "N-MailClient",
		Title: title,
		Body:  message,
	}
	return n.Push()
}

// --- Accounts -------------------------------------------------------------

func (a *App) ListAccounts() []store.Account { return a.store.List() }

func (a *App) AddAccount(in store.Account) (store.Account, error) { return a.store.Add(in) }

func (a *App) UpdateAccount(in store.Account) (store.Account, error) { return a.store.Update(in) }

func (a *App) RemoveAccount(id string) error { return a.store.Remove(id) }

func (a *App) Probe(email string) mail.ProbeResult { return mail.Autodiscover(email) }

// --- Mail -----------------------------------------------------------------

func (a *App) acc(id string) (store.Account, error) { return a.store.Get(id) }

func (a *App) Folders(accountID string) ([]mail.Folder, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	return mail.ListFolders(acc)
}

func (a *App) Messages(accountID, folder string) ([]mail.Summary, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	return mail.ListMessages(acc, folder, 50)
}

// MessagesPage returns up to 50 summaries at the given offset (newest-first).
func (a *App) MessagesPage(accountID, folder string, offset int) ([]mail.Summary, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	return mail.ListMessagesPage(acc, folder, 50, uint32(offset))
}

// CachedMessages returns the locally cached newest-page summaries for an
// account+folder (for instant paint / offline), or an empty slice if none.
func (a *App) CachedMessages(accountID, folder string) []mail.Summary {
	s := mail.LoadCache(accountID, folder)
	if s == nil {
		return []mail.Summary{}
	}
	return s
}

// CreateFolder creates a new IMAP folder.
func (a *App) CreateFolder(accountID, name string) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.CreateFolder(acc, name)
}

// DeleteFolder permanently removes an IMAP folder.
func (a *App) DeleteFolder(accountID, name string) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.DeleteFolder(acc, name)
}

// RenameFolder renames an IMAP folder.
func (a *App) RenameFolder(accountID, oldName, newName string) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.RenameFolder(acc, oldName, newName)
}

// Translate sends text to a LibreTranslate server and returns the translated text.
func (a *App) Translate(text, targetLang, serverURL, apiKey string) (string, error) {
	return translate.Translate(text, "auto", targetLang, serverURL, apiKey)
}

// CheckRecipientTLS probes STARTTLS support on the MX server for the given email address.
func (a *App) CheckRecipientTLS(email string) bool {
	return mail.ProbeTLS(email)
}

// CheckRecipientDANE returns the DANE/TLSA status for the recipient's MX host.
// resolver is an explicit DNS server IP (empty = system resolver).
func (a *App) CheckRecipientDANE(email, resolver string) string {
	return mail.CheckDANE(email, resolver)
}

// GetAttachmentText downloads an attachment and returns its content as a string
// (useful for reading .ics and .vcf attachments without saving to disk).
func (a *App) GetAttachmentText(accountID, folder string, uid uint32, index int) (string, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return "", err
	}
	data, _, err := mail.DownloadAttachment(acc, folder, uid, index)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

// ExportSettings returns the raw settings JSON for backup purposes.
func (a *App) ExportSettings() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	b, err := os.ReadFile(filepath.Join(dir, "n-mailclient-go", "db.json"))
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// ImportSettings overwrites the settings file and reloads accounts.
func (a *App) ImportSettings(jsonData string) error {
	dir, err := os.UserConfigDir()
	if err != nil {
		return err
	}
	p := filepath.Join(dir, "n-mailclient-go", "db.json")
	if err := os.WriteFile(p, []byte(jsonData), 0o600); err != nil {
		return err
	}
	a.store.Load()
	return nil
}

func (a *App) Search(accountID, folder, query string) ([]mail.Summary, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	return mail.Search(acc, folder, query)
}

func (a *App) Message(accountID, folder string, uid uint32) (*mail.Detail, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	return mail.GetMessage(acc, folder, uid)
}

func (a *App) SetSeen(accountID, folder string, uid uint32, seen bool) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.SetFlag(acc, folder, uid, "\\Seen", seen)
}

func (a *App) SetFlagged(accountID, folder string, uid uint32, flagged bool) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.SetFlag(acc, folder, uid, "\\Flagged", flagged)
}

func (a *App) Move(accountID, folder, dest string, uid uint32) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.Move(acc, folder, dest, uid)
}

func (a *App) Delete(accountID, folder string, uid uint32) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.Delete(acc, folder, uid)
}

// SaveAttachment downloads an attachment and writes it via a native save dialog.
func (a *App) SaveAttachment(accountID, folder string, uid uint32, index int) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	data, name, err := mail.DownloadAttachment(acc, folder, uid, index)
	if err != nil {
		return err
	}
	path, err := wruntime.SaveFileDialog(a.ctx, wruntime.SaveDialogOptions{DefaultFilename: name})
	if err != nil || path == "" {
		return err
	}
	return os.WriteFile(path, data, 0o644)
}

// SetLabel adds or removes a custom IMAP keyword (label) on a message.
func (a *App) SetLabel(accountID, folder string, uid uint32, label string, on bool) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return mail.SetFlag(acc, folder, uid, label, on)
}

// SaveDraft appends the composed draft to the given IMAP folder with \Draft flag.
func (a *App) SaveDraft(req mail.SendRequest, folder string) error {
	acc, err := a.acc(req.AccountID)
	if err != nil {
		return err
	}
	return mail.SaveDraft(acc, req, folder)
}

// UnifiedInbox fetches INBOX from every account concurrently and returns all
// summaries sorted newest-first.
func (a *App) UnifiedInbox() []mail.UnifiedSummary {
	accs := a.store.List()
	ch := make(chan []mail.UnifiedSummary, len(accs))
	for _, acc := range accs {
		acc := acc
		go func() {
			msgs, err := mail.ListMessages(acc, "INBOX", 50)
			if err != nil {
				ch <- nil
				return
			}
			us := make([]mail.UnifiedSummary, len(msgs))
			for i, m := range msgs {
				us[i] = mail.UnifiedSummary{Summary: m, AccountID: acc.ID, AccountEmail: acc.Email}
			}
			ch <- us
		}()
	}
	var all []mail.UnifiedSummary
	for range accs {
		if items := <-ch; items != nil {
			all = append(all, items...)
		}
	}
	sort.Slice(all, func(i, j int) bool { return all[i].Date > all[j].Date })
	return all
}

// MessageSource returns the raw RFC 2822 source of a message.
func (a *App) MessageSource(accountID, folder string, uid uint32) (string, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return "", err
	}
	return mail.GetSource(acc, folder, uid)
}

// Send delivers a message and best-effort stores a copy in "Sent".
func (a *App) Send(req mail.SendRequest) error {
	acc, err := a.acc(req.AccountID)
	if err != nil {
		return err
	}
	raw, err := mail.Send(acc, req)
	if err != nil {
		return err
	}
	_ = mail.Append(acc, "Sent", raw)
	return nil
}

// --- Sieve / ManageSieve --------------------------------------------------

func (a *App) sieveClient(accountID string) (*sieve.Client, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	host := acc.SieveHost
	if host == "" {
		host = acc.IMAPHost
	}
	port := acc.SievePort
	if port == 0 {
		port = 4190
	}
	return sieve.Dial(host, port, acc.User, acc.Password)
}

func (a *App) SieveList(accountID string) ([]sieve.Script, error) {
	c, err := a.sieveClient(accountID)
	if err != nil {
		return nil, err
	}
	defer c.Close()
	return c.ListScripts()
}

func (a *App) SieveGet(accountID, name string) (string, error) {
	c, err := a.sieveClient(accountID)
	if err != nil {
		return "", err
	}
	defer c.Close()
	return c.GetScript(name)
}

func (a *App) SievePut(accountID, name, content string) error {
	c, err := a.sieveClient(accountID)
	if err != nil {
		return err
	}
	defer c.Close()
	return c.PutScript(name, content)
}

func (a *App) SieveSetActive(accountID, name string) error {
	c, err := a.sieveClient(accountID)
	if err != nil {
		return err
	}
	defer c.Close()
	return c.SetActive(name)
}

func (a *App) SieveDelete(accountID, name string) error {
	c, err := a.sieveClient(accountID)
	if err != nil {
		return err
	}
	defer c.Close()
	return c.DeleteScript(name)
}

// --- Contacts / CardDAV ---------------------------------------------------

func (a *App) ContactList(accountID string) ([]contacts.Contact, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	if acc.CardDAVURL == "" {
		return nil, fmt.Errorf("keine CardDAV-URL konfiguriert")
	}
	return contacts.List(acc.CardDAVURL, acc.User, acc.Password)
}

func (a *App) ContactSave(accountID string, c contacts.Contact) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	if acc.CardDAVURL == "" {
		return fmt.Errorf("keine CardDAV-URL konfiguriert")
	}
	return contacts.Save(acc.CardDAVURL, acc.User, acc.Password, c)
}

func (a *App) ContactDelete(accountID, href, etag string) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	return contacts.Delete(href, etag, acc.User, acc.Password)
}

// --- Calendar / CalDAV ----------------------------------------------------

func (a *App) CalendarList(accountID string) ([]calendar.Event, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	if acc.CalDAVURL == "" {
		return nil, fmt.Errorf("keine CalDAV-URL konfiguriert")
	}
	if a.calCache == nil {
		a.calCache = make(map[string]calCacheEntry)
	}
	if e, ok := a.calCache[accountID]; ok && time.Now().Before(e.expires) {
		return e.events, nil
	}
	evs, err := calendar.List(acc.CalDAVURL, acc.User, acc.Password)
	if err != nil {
		return nil, err
	}
	a.calCache[accountID] = calCacheEntry{events: evs, expires: time.Now().Add(5 * time.Minute)}
	return evs, nil
}

func (a *App) CalendarSave(accountID string, ev calendar.Event) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	if acc.CalDAVURL == "" {
		return fmt.Errorf("keine CalDAV-URL konfiguriert")
	}
	if err := calendar.Save(acc.CalDAVURL, acc.User, acc.Password, ev); err != nil {
		return err
	}
	delete(a.calCache, accountID)
	return nil
}

func (a *App) CalendarDelete(accountID, href, etag string) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	if err := calendar.Delete(href, etag, acc.User, acc.Password); err != nil {
		return err
	}
	delete(a.calCache, accountID)
	return nil
}

// --- Attachment Archive ----------------------------------------------------

// ArchivedFile is a file saved to the local attachment archive.
type ArchivedFile struct {
	Name string `json:"name"`
	Path string `json:"path"`
	Size int64  `json:"size"`
	Date string `json:"date"`
}

func archiveDir() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	p := filepath.Join(dir, "n-mailclient-go", "attachments")
	return p, os.MkdirAll(p, 0o755)
}

// ArchiveSave downloads an attachment, stores it locally and optionally uploads
// to a WebDAV target configured for the account.
func (a *App) ArchiveSave(accountID, folder string, uid uint32, index int) error {
	acc, err := a.acc(accountID)
	if err != nil {
		return err
	}
	data, name, err := mail.DownloadAttachment(acc, folder, uid, index)
	if err != nil {
		return err
	}
	// Anhangsnamen stammen ungeprüft aus der Mail; auf den reinen Dateinamen
	// reduzieren, um Path-Traversal (z. B. "..\..\Startup\evil.bat") zu verhindern.
	name = filepath.Base(filepath.FromSlash(name))
	if name == "." || name == string(filepath.Separator) || strings.TrimSpace(name) == "" {
		return fmt.Errorf("ungültiger Anhangsname")
	}
	dir, err := archiveDir()
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, name), data, 0o644); err != nil {
		return err
	}
	if acc.WebDAVURL != "" {
		go webdavPut(acc.WebDAVURL, acc.User, acc.Password, name, data)
	}
	return nil
}

// GetAttachmentData returns the raw bytes of an attachment as base64 for
// inline display (e.g. PDF preview in the browser).
func (a *App) GetAttachmentData(accountID, folder string, uid uint32, index int) (map[string]string, error) {
	acc, err := a.acc(accountID)
	if err != nil {
		return nil, err
	}
	data, name, err := mail.DownloadAttachment(acc, folder, uid, index)
	if err != nil {
		return nil, err
	}
	mime := "application/octet-stream"
	if strings.HasSuffix(strings.ToLower(name), ".pdf") {
		mime = "application/pdf"
	}
	return map[string]string{"data": base64.StdEncoding.EncodeToString(data), "mime": mime, "name": name}, nil
}

func webdavPut(rawURL, user, pass, filename string, data []byte) {
	req, err := http.NewRequest("PUT", strings.TrimRight(rawURL, "/")+"/"+filename, bytes.NewReader(data))
	if err != nil {
		return
	}
	req.SetBasicAuth(user, pass)
	resp, err := http.DefaultClient.Do(req)
	if err == nil {
		resp.Body.Close()
	}
}

// ArchiveList returns all files in the local attachment archive, newest first.
func (a *App) ArchiveList() ([]ArchivedFile, error) {
	dir, err := archiveDir()
	if err != nil {
		return nil, err
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return []ArchivedFile{}, nil
		}
		return nil, err
	}
	files := []ArchivedFile{}
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		info, _ := e.Info()
		files = append(files, ArchivedFile{
			Name: e.Name(),
			Path: filepath.Join(dir, e.Name()),
			Size: info.Size(),
			Date: info.ModTime().Format(time.RFC3339),
		})
	}
	sort.Slice(files, func(i, j int) bool { return files[i].Date > files[j].Date })
	return files, nil
}

// ArchiveOpen opens a file from the archive with the system default application.
func (a *App) ArchiveOpen(path string) error {
	return exec.Command("cmd", "/c", "start", "", path).Start()
}

// ArchiveDelete removes a file from the archive.
func (a *App) ArchiveDelete(path string) error {
	dir, err := archiveDir()
	if err != nil {
		return err
	}
	// Safety: only delete files inside the archive directory.
	abs, err := filepath.Abs(path)
	if err != nil || filepath.Dir(abs) != dir {
		return fmt.Errorf("ungültiger Pfad")
	}
	return os.Remove(abs)
}

// --- Autostart ---------------------------------------------------------------

const autostartKey = `Software\Microsoft\Windows\CurrentVersion\Run`
const autostartName = "N-MailClient"

func (a *App) GetAutostart() bool {
	k, err := registry.OpenKey(registry.CURRENT_USER, autostartKey, registry.QUERY_VALUE)
	if err != nil {
		return false
	}
	defer k.Close()
	_, _, err = k.GetStringValue(autostartName)
	return err == nil
}

func (a *App) SetAutostart(on bool) error {
	k, _, err := registry.CreateKey(registry.CURRENT_USER, autostartKey, registry.SET_VALUE)
	if err != nil {
		return err
	}
	defer k.Close()
	if on {
		exe, err := os.Executable()
		if err != nil {
			return err
		}
		return k.SetStringValue(autostartName, `"`+exe+`"`)
	}
	return k.DeleteValue(autostartName)
}

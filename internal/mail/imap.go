package mail

import (
	"bufio"
	"crypto/tls"
	"fmt"
	"io"
	"net/textproto"
	"sort"
	"strings"
	"time"

	"github.com/emersion/go-imap"
	"github.com/emersion/go-imap/client"
	gomail "github.com/emersion/go-message/mail"

	"n-mailclient-go/internal/store"
)

// catSection fetches just the headers needed to categorize a message (BODY.PEEK).
var catSection = &imap.BodySectionName{
	BodyPartName: imap.BodyPartName{
		Specifier: imap.HeaderSpecifier,
		Fields:    []string{"List-Id", "List-Unsubscribe", "Precedence", "Auto-Submitted"},
	},
	Peek: true,
}

// socialDomains are sender-domain fragments classified as "soziales".
var socialDomains = []string{"facebook", "fb.com", "twitter", "x.com", "linkedin", "instagram",
	"xing", "tiktok", "youtube", "pinterest", "reddit", "discord", "mastodon", "snapchat", "meetup", "nextdoor"}

// classify assigns an inbox category from sender domain + list/bulk headers.
func classify(m *imap.Message) string {
	if m.Envelope != nil && len(m.Envelope.From) > 0 {
		dom := strings.ToLower(m.Envelope.From[0].HostName)
		for _, s := range socialDomains {
			if strings.Contains(dom, s) {
				return "soziales"
			}
		}
	}
	r := m.GetBody(catSection)
	if r == nil {
		return "allgemein"
	}
	hdr, _ := textproto.NewReader(bufio.NewReader(r)).ReadMIMEHeader()
	if hdr.Get("List-Id") != "" {
		return "newsletter"
	}
	prec := strings.ToLower(hdr.Get("Precedence"))
	auto := strings.ToLower(hdr.Get("Auto-Submitted"))
	if hdr.Get("List-Unsubscribe") != "" || strings.Contains(prec, "bulk") || prec == "list" || (auto != "" && auto != "no") {
		return "werbung"
	}
	return "allgemein"
}

// Folder is a mailbox with its unread count and hierarchy info.
type Folder struct {
	Name      string `json:"name"`
	Unseen    uint32 `json:"unseen"`
	Delimiter string `json:"delimiter"`
	Depth     int    `json:"depth"`
	Label     string `json:"label"` // leaf name for display
}

// Summary is a lightweight message header for the list view.
type Summary struct {
	UID            uint32   `json:"uid"`
	Subject        string   `json:"subject"`
	From           string   `json:"from"`
	Date           string   `json:"date"`
	Seen           bool     `json:"seen"`
	Flagged        bool     `json:"flagged"`
	Answered       bool     `json:"answered"`
	HasAttachments bool     `json:"hasAttachments"`
	Labels         []string `json:"labels"`
	Category       string   `json:"category"` // allgemein|werbung|newsletter|soziales
}

// Attachment metadata within a message.
type Attachment struct {
	Index    int    `json:"index"`
	Filename string `json:"filename"`
	MimeType string `json:"mimeType"`
}

// AuthResult holds the parsed e-mail authentication verdicts (Authentication-Results
// / Received-SPF). Each value is the result token: pass, fail, softfail, neutral,
// none, permerror, temperror, or "" if absent.
type AuthResult struct {
	SPF   string `json:"spf"`
	DKIM  string `json:"dkim"`
	DMARC string `json:"dmarc"`
}

// Detail is a fully-read message for the reading pane.
type Detail struct {
	UID         uint32       `json:"uid"`
	Subject     string       `json:"subject"`
	From        string       `json:"from"`
	To          string       `json:"to"`
	Cc          string       `json:"cc"`
	Date        string       `json:"date"`
	Text        string       `json:"text"`
	HTML        string       `json:"html"`
	MessageID   string       `json:"messageId"`
	References  []string     `json:"references"`
	Attachments []Attachment `json:"attachments"`
	Labels      []string     `json:"labels"`
	Auth        AuthResult   `json:"auth"`     // SPF/DKIM/DMARC verdicts
	FromName    string       `json:"fromName"` // display name of the first From address
	FromAddr    string       `json:"fromAddr"` // mailbox@host of the first From address
}

// authToken extracts the verdict after "<key>=" in a (lower-cased) auth string,
// e.g. authToken("spf=pass smtp.mailfrom=...", "spf") == "pass".
func authToken(s, key string) string {
	i := strings.Index(s, key+"=")
	if i < 0 {
		return ""
	}
	rest := s[i+len(key)+1:]
	j := 0
	for j < len(rest) && rest[j] >= 'a' && rest[j] <= 'z' {
		j++
	}
	return rest[:j]
}

// parseAuth reads SPF/DKIM/DMARC verdicts from the message header.
func parseAuth(h gomail.Header) AuthResult {
	ar := strings.ToLower(h.Get("Authentication-Results"))
	res := AuthResult{
		SPF:   authToken(ar, "spf"),
		DKIM:  authToken(ar, "dkim"),
		DMARC: authToken(ar, "dmarc"),
	}
	if res.SPF == "" {
		// Fall back to a dedicated Received-SPF header (e.g. "Received-SPF: pass (...)").
		rs := strings.TrimSpace(strings.ToLower(h.Get("Received-SPF")))
		for _, v := range []string{"pass", "fail", "softfail", "neutral", "none", "permerror", "temperror"} {
			if strings.HasPrefix(rs, v) {
				res.SPF = v
				break
			}
		}
	}
	return res
}

func connect(a store.Account) (*client.Client, error) {
	addr := fmt.Sprintf("%s:%d", a.IMAPHost, a.IMAPPort)
	var c *client.Client
	var err error
	if a.IMAPPort == 143 {
		// Plaintext port → upgrade with STARTTLS (refuse to continue if it fails).
		if c, err = client.Dial(addr); err != nil {
			return nil, err
		}
		if err = c.StartTLS(&tls.Config{ServerName: a.IMAPHost}); err != nil {
			_ = c.Logout()
			return nil, err
		}
	} else {
		// Implicit TLS (993 and anything else).
		if c, err = client.DialTLS(addr, nil); err != nil {
			return nil, err
		}
	}
	if err := c.Login(a.User, a.Password); err != nil {
		_ = c.Logout()
		return nil, err
	}
	return c, nil
}

func addr(a *imap.Address) string {
	if a == nil {
		return ""
	}
	if a.PersonalName != "" {
		return fmt.Sprintf("%s <%s@%s>", a.PersonalName, a.MailboxName, a.HostName)
	}
	return fmt.Sprintf("%s@%s", a.MailboxName, a.HostName)
}

func joinAddrs(list []*imap.Address) string {
	parts := make([]string, 0, len(list))
	for _, a := range list {
		parts = append(parts, addr(a))
	}
	return strings.Join(parts, ", ")
}

// ListFolders returns all mailboxes with their unread counts.
func ListFolders(a store.Account) ([]Folder, error) {
	c, err := connect(a)
	if err != nil {
		return nil, err
	}
	defer c.Logout()

	mailboxes := make(chan *imap.MailboxInfo, 50)
	done := make(chan error, 1)
	go func() { done <- c.List("", "*", mailboxes) }()

	type mboxMeta struct {
		name      string
		delimiter string
	}
	var boxes []mboxMeta
	for m := range mailboxes {
		noselect := false
		for _, attr := range m.Attributes {
			if attr == imap.NoSelectAttr {
				noselect = true
			}
		}
		if !noselect {
			boxes = append(boxes, mboxMeta{m.Name, m.Delimiter})
		}
	}
	if err := <-done; err != nil {
		return nil, err
	}
	sort.Slice(boxes, func(i, j int) bool { return boxes[i].name < boxes[j].name })

	out := make([]Folder, 0, len(boxes))
	for _, b := range boxes {
		f := Folder{Name: b.name, Delimiter: b.delimiter}
		if b.delimiter != "" {
			parts := strings.Split(b.name, b.delimiter)
			f.Depth = len(parts) - 1
			f.Label = parts[len(parts)-1]
		} else {
			f.Label = b.name
		}
		// Skip Dovecot namespace artifacts where a folder's label equals its
		// direct parent's full name (e.g. "Archive/Archive", "INBOX.INBOX").
		if f.Depth > 0 && f.Delimiter != "" {
			idx := strings.LastIndex(f.Name, f.Delimiter)
			if idx >= 0 && f.Label == f.Name[:idx] {
				continue
			}
		}
		if status, err := c.Status(b.name, []imap.StatusItem{imap.StatusUnseen}); err == nil {
			f.Unseen = status.Unseen
		}
		out = append(out, f)
	}
	return out, nil
}

func hasAttachments(bs *imap.BodyStructure) bool {
	if bs == nil {
		return false
	}
	if strings.EqualFold(bs.Disposition, "attachment") {
		return true
	}
	for _, p := range bs.Parts {
		if hasAttachments(p) {
			return true
		}
	}
	return false
}

// ListMessages returns the newest `limit` message summaries of a folder.
func ListMessages(a store.Account, folder string, limit uint32) ([]Summary, error) {
	c, err := connect(a)
	if err != nil {
		return nil, err
	}
	defer c.Logout()

	mbox, err := c.Select(folder, true)
	if err != nil {
		return nil, err
	}
	if mbox.Messages == 0 {
		return []Summary{}, nil
	}
	from := uint32(1)
	if mbox.Messages > limit {
		from = mbox.Messages - limit + 1
	}
	seqset := new(imap.SeqSet)
	seqset.AddRange(from, mbox.Messages)

	messages := make(chan *imap.Message, limit)
	done := make(chan error, 1)
	items := []imap.FetchItem{imap.FetchEnvelope, imap.FetchUid, imap.FetchFlags, imap.FetchBodyStructure, catSection.FetchItem()}
	go func() { done <- c.Fetch(seqset, items, messages) }()

	var out []Summary
	for m := range messages {
		out = append(out, summaryFrom(m))
	}
	if err := <-done; err != nil {
		return nil, err
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Date > out[j].Date })
	return out, nil
}

func summaryFrom(m *imap.Message) Summary {
	s := Summary{UID: m.Uid, HasAttachments: hasAttachments(m.BodyStructure), Category: classify(m)}
	if m.Envelope != nil {
		s.Subject = m.Envelope.Subject
		if len(m.Envelope.From) > 0 {
			s.From = addr(m.Envelope.From[0])
		}
		s.Date = m.Envelope.Date.Format(time.RFC3339)
	}
	for _, f := range m.Flags {
		switch f {
		case imap.SeenFlag:
			s.Seen = true
		case imap.FlaggedFlag:
			s.Flagged = true
		case imap.AnsweredFlag:
			s.Answered = true
		default:
			if !strings.HasPrefix(f, "\\") {
				s.Labels = append(s.Labels, f)
			}
		}
	}
	return s
}

// GetMessage fetches and parses one message by UID (marks it seen). On a
// connection/fetch error it falls back to the offline cache; on success it
// refreshes that cache.
func GetMessage(a store.Account, folder string, uid uint32) (*Detail, error) {
	d, err := getMessageLive(a, folder, uid)
	if err != nil {
		if cached := LoadDetailCache(a.ID, folder, uid); cached != nil {
			return cached, nil
		}
		return nil, err
	}
	saveDetailCache(a.ID, folder, uid, d)
	return d, nil
}

func getMessageLive(a store.Account, folder string, uid uint32) (*Detail, error) {
	c, err := connect(a)
	if err != nil {
		return nil, err
	}
	defer c.Logout()

	if _, err := c.Select(folder, false); err != nil {
		return nil, err
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uid)
	section := &imap.BodySectionName{}
	items := []imap.FetchItem{section.FetchItem(), imap.FetchEnvelope, imap.FetchFlags}

	messages := make(chan *imap.Message, 1)
	done := make(chan error, 1)
	go func() { done <- c.UidFetch(seqset, items, messages) }()

	msg := <-messages
	if err := <-done; err != nil {
		return nil, err
	}
	if msg == nil {
		return nil, fmt.Errorf("Nachricht nicht gefunden")
	}

	d := &Detail{UID: uid, Attachments: []Attachment{}, References: []string{}}
	if msg.Envelope != nil {
		d.Subject = msg.Envelope.Subject
		d.From = joinAddrs(msg.Envelope.From)
		d.To = joinAddrs(msg.Envelope.To)
		d.Cc = joinAddrs(msg.Envelope.Cc)
		d.Date = msg.Envelope.Date.Format(time.RFC3339)
		d.MessageID = msg.Envelope.MessageId
		if len(msg.Envelope.From) > 0 {
			f := msg.Envelope.From[0]
			d.FromName = f.PersonalName
			d.FromAddr = fmt.Sprintf("%s@%s", f.MailboxName, f.HostName)
		}
	}

	if r := msg.GetBody(section); r != nil {
		if mr, err := gomail.CreateReader(r); err == nil {
			d.Auth = parseAuth(mr.Header)
			idx := 0
			for {
				p, err := mr.NextPart()
				if err == io.EOF {
					break
				} else if err != nil {
					break
				}
				switch h := p.Header.(type) {
				case *gomail.InlineHeader:
					ct, _, _ := h.ContentType()
					b, _ := io.ReadAll(p.Body)
					if ct == "text/html" {
						d.HTML += string(b)
					} else if d.Text == "" {
						d.Text = string(b)
					}
				case *gomail.AttachmentHeader:
					name, _ := h.Filename()
					ct, _, _ := h.ContentType()
					if name == "" {
						name = fmt.Sprintf("anhang-%d", idx+1)
					}
					d.Attachments = append(d.Attachments, Attachment{Index: idx, Filename: name, MimeType: ct})
					idx++
				}
			}
		}
	}
	for _, f := range msg.Flags {
		if !strings.HasPrefix(f, "\\") {
			d.Labels = append(d.Labels, f)
		}
	}
	// Mark as seen after a successful read.
	_ = c.UidStore(seqset, imap.FormatFlagsOp(imap.AddFlags, true), []interface{}{imap.SeenFlag}, nil)
	return d, nil
}

// DownloadAttachment returns the bytes + filename of one attachment.
func DownloadAttachment(a store.Account, folder string, uid uint32, index int) ([]byte, string, error) {
	c, err := connect(a)
	if err != nil {
		return nil, "", err
	}
	defer c.Logout()
	if _, err := c.Select(folder, true); err != nil {
		return nil, "", err
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uid)
	section := &imap.BodySectionName{}
	messages := make(chan *imap.Message, 1)
	done := make(chan error, 1)
	go func() { done <- c.UidFetch(seqset, []imap.FetchItem{section.FetchItem()}, messages) }()
	msg := <-messages
	if err := <-done; err != nil {
		return nil, "", err
	}
	if msg == nil {
		return nil, "", fmt.Errorf("Nachricht nicht gefunden")
	}
	r := msg.GetBody(section)
	mr, err := gomail.CreateReader(r)
	if err != nil {
		return nil, "", err
	}
	idx := 0
	for {
		p, err := mr.NextPart()
		if err == io.EOF {
			break
		} else if err != nil {
			break
		}
		if h, ok := p.Header.(*gomail.AttachmentHeader); ok {
			if idx == index {
				name, _ := h.Filename()
				b, _ := io.ReadAll(p.Body)
				return b, name, nil
			}
			idx++
		}
	}
	return nil, "", fmt.Errorf("Anhang nicht gefunden")
}

// SetSeen / SetFlagged toggle flags on a message.
func SetFlag(a store.Account, folder string, uid uint32, flag string, on bool) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	if _, err := c.Select(folder, false); err != nil {
		return err
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uid)
	var op imap.FlagsOp = imap.AddFlags
	if !on {
		op = imap.RemoveFlags
	}
	return c.UidStore(seqset, imap.FormatFlagsOp(op, true), []interface{}{flag}, nil)
}

// Move copies a message to dest and removes it from the source (COPY + \Deleted + EXPUNGE).
func Move(a store.Account, folder, dest string, uid uint32) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	if _, err := c.Select(folder, false); err != nil {
		return err
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uid)
	if err := c.UidCopy(seqset, dest); err != nil {
		return err
	}
	if err := c.UidStore(seqset, imap.FormatFlagsOp(imap.AddFlags, true), []interface{}{imap.DeletedFlag}, nil); err != nil {
		return err
	}
	return c.Expunge(nil)
}

// Delete permanently removes a message (\Deleted + EXPUNGE).
func Delete(a store.Account, folder string, uid uint32) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	if _, err := c.Select(folder, false); err != nil {
		return err
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uid)
	if err := c.UidStore(seqset, imap.FormatFlagsOp(imap.AddFlags, true), []interface{}{imap.DeletedFlag}, nil); err != nil {
		return err
	}
	return c.Expunge(nil)
}

// Search returns summaries of messages matching a full-text query in a folder.
func Search(a store.Account, folder, query string) ([]Summary, error) {
	c, err := connect(a)
	if err != nil {
		return nil, err
	}
	defer c.Logout()
	if _, err := c.Select(folder, true); err != nil {
		return nil, err
	}
	criteria := imap.NewSearchCriteria()
	criteria.Text = []string{query}
	uids, err := c.UidSearch(criteria)
	if err != nil {
		return nil, err
	}
	if len(uids) == 0 {
		return []Summary{}, nil
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uids...)
	messages := make(chan *imap.Message, len(uids))
	done := make(chan error, 1)
	items := []imap.FetchItem{imap.FetchEnvelope, imap.FetchUid, imap.FetchFlags, imap.FetchBodyStructure, catSection.FetchItem()}
	go func() { done <- c.UidFetch(seqset, items, messages) }()
	var out []Summary
	for m := range messages {
		out = append(out, summaryFrom(m))
	}
	if err := <-done; err != nil {
		return nil, err
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Date > out[j].Date })
	return out, nil
}

// Append stores a raw message into a folder (e.g. the Sent copy).
func Append(a store.Account, folder string, raw []byte) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	return c.Append(folder, []string{imap.SeenFlag}, time.Now(), strings.NewReader(string(raw)))
}

// UnifiedSummary extends Summary with account info for the cross-account inbox.
type UnifiedSummary struct {
	Summary
	AccountID    string `json:"accountId"`
	AccountEmail string `json:"accountEmail"`
}

// GetSource returns the raw RFC 2822 bytes of a message as a string.
func GetSource(a store.Account, folder string, uid uint32) (string, error) {
	c, err := connect(a)
	if err != nil {
		return "", err
	}
	defer c.Logout()
	if _, err := c.Select(folder, true); err != nil {
		return "", err
	}
	seqset := new(imap.SeqSet)
	seqset.AddNum(uid)
	section := &imap.BodySectionName{}
	messages := make(chan *imap.Message, 1)
	done := make(chan error, 1)
	go func() { done <- c.UidFetch(seqset, []imap.FetchItem{section.FetchItem()}, messages) }()
	msg := <-messages
	if err := <-done; err != nil {
		return "", err
	}
	if msg == nil {
		return "", fmt.Errorf("Nachricht nicht gefunden")
	}
	b, err := io.ReadAll(msg.GetBody(section))
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// SaveDraft appends a composed message to the given drafts folder with \Draft + \Seen flags.
func SaveDraft(a store.Account, req SendRequest, draftsFolder string) error {
	raw, err := buildMIME(a, req)
	if err != nil {
		return err
	}
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	return c.Append(draftsFolder, []string{imap.DraftFlag, imap.SeenFlag}, time.Now(), strings.NewReader(string(raw)))
}

// ListMessagesPage returns up to limit summaries starting at offset (newest-first).
// offset=0 = newest page, offset=50 = next older page, etc.
func ListMessagesPage(a store.Account, folder string, limit, offset uint32) ([]Summary, error) {
	c, err := connect(a)
	if err != nil {
		return nil, err
	}
	defer c.Logout()

	mbox, err := c.Select(folder, true)
	if err != nil {
		return nil, err
	}
	if mbox.Messages == 0 || mbox.Messages <= offset {
		return []Summary{}, nil
	}
	to := mbox.Messages - offset
	from := uint32(1)
	if to > limit {
		from = to - limit + 1
	}
	seqset := new(imap.SeqSet)
	seqset.AddRange(from, to)

	messages := make(chan *imap.Message, limit)
	done := make(chan error, 1)
	items := []imap.FetchItem{imap.FetchEnvelope, imap.FetchUid, imap.FetchFlags, imap.FetchBodyStructure, catSection.FetchItem()}
	go func() { done <- c.Fetch(seqset, items, messages) }()

	var out []Summary
	for m := range messages {
		out = append(out, summaryFrom(m))
	}
	if err := <-done; err != nil {
		return nil, err
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Date > out[j].Date })
	if offset == 0 {
		saveCache(a.ID, folder, out) // refresh offline cache for the newest page
	}
	return out, nil
}

// CreateFolder creates a new mailbox on the server.
func CreateFolder(a store.Account, name string) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	return c.Create(name)
}

// DeleteFolder permanently removes a mailbox from the server.
func DeleteFolder(a store.Account, name string) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	return c.Delete(name)
}

// RenameFolder renames an existing mailbox.
func RenameFolder(a store.Account, oldName, newName string) error {
	c, err := connect(a)
	if err != nil {
		return err
	}
	defer c.Logout()
	return c.Rename(oldName, newName)
}

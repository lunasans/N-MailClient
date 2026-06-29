package mail

import (
	"net"
	"strings"
	"time"
)

// ProbeTLS checks if the primary MX server for the given email address
// advertises STARTTLS on port 25. Returns true if STARTTLS is supported.
func ProbeTLS(email string) bool {
	parts := strings.SplitN(email, "@", 2)
	if len(parts) != 2 {
		return false
	}
	domain := parts[1]
	mxs, err := net.LookupMX(domain)
	if err != nil || len(mxs) == 0 {
		return false
	}
	host := strings.TrimSuffix(mxs[0].Host, ".")
	conn, err := net.DialTimeout("tcp", host+":25", 5*time.Second)
	if err != nil {
		return false
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(8 * time.Second))
	buf := make([]byte, 512)
	conn.Read(buf) // read SMTP greeting
	conn.Write([]byte("EHLO n-mailclient\r\n"))
	n, _ := conn.Read(buf)
	conn.Write([]byte("QUIT\r\n"))
	return strings.Contains(strings.ToUpper(string(buf[:n])), "STARTTLS")
}

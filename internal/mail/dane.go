package mail

import (
	"net"
	"strings"
	"time"
	"unsafe"

	"github.com/miekg/dns"
	"golang.org/x/sys/windows"
)

// CheckDANE returns the DANE/TLSA status for the recipient's primary MX host:
//
//	"secure"     — TLSA records present AND DNSSEC-validated (AD flag)
//	"present"    — TLSA records present but not DNSSEC-validated by the resolver
//	"none"       — no TLSA records
//	"noresolver" — no usable DNS resolver (so the check could not run)
//
// resolver is an explicit DNS server ("ip" or "ip:port"); empty uses the
// system-configured resolver.
func CheckDANE(email, resolver string) string {
	parts := strings.SplitN(email, "@", 2)
	if len(parts) != 2 {
		return "none"
	}
	mxs, err := net.LookupMX(parts[1])
	if err != nil || len(mxs) == 0 {
		return "none"
	}
	host := strings.TrimSuffix(mxs[0].Host, ".")

	server := strings.TrimSpace(resolver)
	if server == "" {
		server = systemDNS()
	}
	if server == "" {
		return "noresolver"
	}
	if !strings.Contains(server, ":") {
		server += ":53"
	}

	m := new(dns.Msg)
	m.SetQuestion("_25._tcp."+host+".", dns.TypeTLSA)
	m.SetEdns0(4096, true) // request DNSSEC (DO bit)
	m.RecursionDesired = true

	c := &dns.Client{Timeout: 5 * time.Second}
	r, _, err := c.Exchange(m, server)
	if err != nil || r == nil {
		return "noresolver"
	}
	hasTLSA := false
	for _, ans := range r.Answer {
		if _, ok := ans.(*dns.TLSA); ok {
			hasTLSA = true
			break
		}
	}
	if !hasTLSA {
		return "none"
	}
	if r.AuthenticatedData {
		return "secure"
	}
	return "present"
}

// --- Windows system DNS discovery (IP Helper API GetNetworkParams) ------------

var (
	iphlpapi             = windows.NewLazySystemDLL("iphlpapi.dll")
	procGetNetworkParams = iphlpapi.NewProc("GetNetworkParams")
)

type ipAddrString struct {
	Next      *ipAddrString
	IpAddress [16]byte
	IpMask    [16]byte
	Context   uint32
}

type fixedInfo struct {
	HostName         [132]byte
	DomainName       [132]byte
	CurrentDnsServer *ipAddrString
	DnsServerList    ipAddrString
	NodeType         uint32
	ScopeId          [260]byte
	EnableRouting    uint32
	EnableProxy      uint32
	EnableDns        uint32
}

// systemDNS returns the first configured DNS server IP, or "" if undeterminable.
func systemDNS() string {
	var size uint32
	procGetNetworkParams.Call(0, uintptr(unsafe.Pointer(&size)))
	if size == 0 {
		return ""
	}
	buf := make([]byte, size)
	ret, _, _ := procGetNetworkParams.Call(uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size)))
	if ret != 0 {
		return ""
	}
	fi := (*fixedInfo)(unsafe.Pointer(&buf[0]))
	for p := &fi.DnsServerList; p != nil; p = p.Next {
		if ip := cstr(p.IpAddress[:]); ip != "" {
			return ip
		}
	}
	return ""
}

func cstr(b []byte) string {
	i := 0
	for i < len(b) && b[i] != 0 {
		i++
	}
	return string(b[:i])
}

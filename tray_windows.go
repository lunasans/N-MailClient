//go:build windows

package main

import (
	"os"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	modShell32 = windows.NewLazySystemDLL("shell32.dll")

	procShellNotifyIcon    = modShell32.NewProc("Shell_NotifyIconW")
	procExtractIconW       = modShell32.NewProc("ExtractIconW")
	procCreateWindowExW    = modUser32.NewProc("CreateWindowExW")
	procDefWindowProcW     = modUser32.NewProc("DefWindowProcW")
	procDestroyWindow      = modUser32.NewProc("DestroyWindow")
	procDispatchMessageW   = modUser32.NewProc("DispatchMessageW")
	procGetMessageW        = modUser32.NewProc("GetMessageW")
	procLoadIconW          = modUser32.NewProc("LoadIconW")
	procPostQuitMessage    = modUser32.NewProc("PostQuitMessage")
	procRegisterClassExW   = modUser32.NewProc("RegisterClassExW")
	procTranslateMessage   = modUser32.NewProc("TranslateMessage")
	procTrackPopupMenu     = modUser32.NewProc("TrackPopupMenu")
	procCreatePopupMenu    = modUser32.NewProc("CreatePopupMenu")
	procAppendMenuW        = modUser32.NewProc("AppendMenuW")
	procDestroyMenu        = modUser32.NewProc("DestroyMenu")
	procGetCursorPos       = modUser32.NewProc("GetCursorPos")
	procSetForegroundWindow = modUser32.NewProc("SetForegroundWindow")
	procShowWindow         = modUser32.NewProc("ShowWindow")
	procPostMessageW       = modUser32.NewProc("PostMessageW")
)

const (
	wmUser      = 0x0400
	wmTray      = wmUser + 1
	wmNotify    = wmUser + 2
	nibMessage  = wmTray

	nimAdd    = 0x0
	nimDelete = 0x2
	nifIcon   = 0x2
	nifTip    = 0x4
	nifMessage = 0x1

	wsBorder     = 0x00800000
	wsOverlapped = 0x00000000
	swHide       = 0
	swShow       = 5
	swRestore    = 9

	mfString  = 0x0
	mfSeparator = 0x800
	tpmLeftButton = 0x0
	tpmBottomAlign = 0x20
	tpmRightAlign  = 0x8

	idOpen = 1001
	idQuit = 1002

	wm_Command = 0x0111
	wm_Destroy = 0x0002
)

type NOTIFYICONDATA struct {
	CbSize           uint32
	HWnd             windows.HWND
	UID              uint32
	UFlags           uint32
	UCallbackMessage uint32
	HIcon            windows.Handle
	SzTip            [128]uint16
	DwState          uint32
	DwStateMask      uint32
	SzInfo           [256]uint16
	UVersion         uint32
	SzInfoTitle      [64]uint16
	DwInfoFlags      uint32
	GuidItem         windows.GUID
	HBalloonIcon     windows.Handle
}

type WNDCLASSEX struct {
	CbSize        uint32
	Style         uint32
	LpfnWndProc   uintptr
	CbClsExtra    int32
	CbWndExtra    int32
	HInstance     windows.Handle
	HIcon         windows.Handle
	HCursor       windows.Handle
	HbrBackground windows.Handle
	LpszMenuName  *uint16
	LpszClassName *uint16
	HIconSm       windows.Handle
}

type POINT struct{ X, Y int32 }

type MSG struct {
	HWnd    windows.HWND
	Message uint32
	WParam  uintptr
	LParam  uintptr
	Time    uint32
	Pt      POINT
}

var trayHWND windows.HWND
var showWindowFn func()
var quitAppFn func()

func wndProc(hwnd windows.HWND, msg uint32, wParam, lParam uintptr) uintptr {
	switch msg {
	case wmTray:
		switch lParam & 0xFFFF {
		case 0x0205: // WM_RBUTTONUP
			showTrayMenu(hwnd)
		case 0x0203: // WM_LBUTTONDBLCLK
			if showWindowFn != nil {
				showWindowFn()
			}
		}
	case wm_Command:
		id := wParam & 0xFFFF
		switch id {
		case idOpen:
			if showWindowFn != nil {
				showWindowFn()
			}
		case idQuit:
			removeTrayIcon(hwnd)
			procPostQuitMessage.Call(0)
			if quitAppFn != nil {
				quitAppFn()
			}
		}
	case wm_Destroy:
		removeTrayIcon(hwnd)
	}
	ret, _, _ := procDefWindowProcW.Call(uintptr(hwnd), uintptr(msg), wParam, lParam)
	return ret
}

func showTrayMenu(hwnd windows.HWND) {
	hmenu, _, _ := procCreatePopupMenu.Call()
	openStr, _ := windows.UTF16PtrFromString("Öffnen")
	procAppendMenuW.Call(hmenu, mfString, idOpen, uintptr(unsafe.Pointer(openStr)))
	procAppendMenuW.Call(hmenu, mfSeparator, 0, 0)
	quitStr, _ := windows.UTF16PtrFromString("Beenden")
	procAppendMenuW.Call(hmenu, mfString, idQuit, uintptr(unsafe.Pointer(quitStr)))

	var pt POINT
	procGetCursorPos.Call(uintptr(unsafe.Pointer(&pt)))
	procSetForegroundWindow.Call(uintptr(hwnd))
	procTrackPopupMenu.Call(hmenu, tpmLeftButton|tpmBottomAlign, uintptr(pt.X), uintptr(pt.Y), 0, uintptr(hwnd), 0)
	procDestroyMenu.Call(hmenu)
}

// loadAppIcon returns the icon embedded in the running executable; falls back
// to the generic application icon if extraction fails.
func loadAppIcon() uintptr {
	if exe, err := os.Executable(); err == nil {
		if p, err := windows.UTF16PtrFromString(exe); err == nil {
			if h, _, _ := procExtractIconW.Call(0, uintptr(unsafe.Pointer(p)), 0); h != 0 && h != 1 {
				return h
			}
		}
	}
	h, _, _ := procLoadIconW.Call(0, 32512) // IDI_APPLICATION
	return h
}

func addTrayIcon(hwnd windows.HWND) {
	icon := loadAppIcon()
	tip, _ := windows.UTF16FromString("N-MailClient")

	var nid NOTIFYICONDATA
	nid.CbSize = uint32(unsafe.Sizeof(nid))
	nid.HWnd = hwnd
	nid.UID = 1
	nid.UFlags = nifIcon | nifTip | nifMessage
	nid.UCallbackMessage = wmTray
	nid.HIcon = windows.Handle(icon)
	copy(nid.SzTip[:], tip)
	procShellNotifyIcon.Call(nimAdd, uintptr(unsafe.Pointer(&nid)))
}

func removeTrayIcon(hwnd windows.HWND) {
	var nid NOTIFYICONDATA
	nid.CbSize = uint32(unsafe.Sizeof(nid))
	nid.HWnd = hwnd
	nid.UID = 1
	procShellNotifyIcon.Call(nimDelete, uintptr(unsafe.Pointer(&nid)))
}

// RunTray starts the system tray icon in a background goroutine.
func RunTray(onShow, onQuit func()) {
	showWindowFn = onShow
	quitAppFn = onQuit
	go func() {
		className, _ := windows.UTF16PtrFromString("NMailClientTray")
		wc := WNDCLASSEX{
			CbSize:        uint32(unsafe.Sizeof(WNDCLASSEX{})),
			LpfnWndProc:   windows.NewCallback(wndProc),
			LpszClassName: className,
		}
		procRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc)))

		hwnd, _, _ := procCreateWindowExW.Call(
			0,
			uintptr(unsafe.Pointer(className)),
			0, 0, 0, 0, 0, 0,
			0, 0, 0, 0,
		)
		trayHWND = windows.HWND(hwnd)
		addTrayIcon(trayHWND)

		var msg MSG
		for {
			r, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
			if r == 0 {
				break
			}
			procTranslateMessage.Call(uintptr(unsafe.Pointer(&msg)))
			procDispatchMessageW.Call(uintptr(unsafe.Pointer(&msg)))
		}
	}()
}

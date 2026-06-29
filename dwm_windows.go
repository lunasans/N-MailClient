//go:build windows

package main

import (
	"syscall"
	"time"
	"unsafe"
)

type margins struct {
	CxLeftWidth, CxRightWidth, CyTopHeight, CyBottomHeight int32
}

var (
	modDwmapi  = syscall.NewLazyDLL("dwmapi.dll")
	modUser32  = syscall.NewLazyDLL("user32.dll")

	procDwmExtendFrameIntoClientArea = modDwmapi.NewProc("DwmExtendFrameIntoClientArea")
	procGetForegroundWindow          = modUser32.NewProc("GetForegroundWindow")
)

// fixDWMCaption removes the OS caption area that Windows re-adds when Mica is
// enabled, even for frameless windows. Must run after the window is visible.
func fixDWMCaption() {
	time.Sleep(150 * time.Millisecond)
	hwnd, _, _ := procGetForegroundWindow.Call()
	if hwnd == 0 {
		return
	}
	// Extend 1 px into the top non-client area — enough to suppress the
	// DWM caption region while keeping Mica active.
	m := margins{CxLeftWidth: 0, CxRightWidth: 0, CyTopHeight: 1, CyBottomHeight: 0}
	procDwmExtendFrameIntoClientArea.Call(hwnd, uintptr(unsafe.Pointer(&m)))
}

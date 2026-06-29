package main

import (
	"embed"
	"os"
	"strconv"
	"strings"

	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"
	"github.com/wailsapp/wails/v2/pkg/options/windows"
	wruntime "github.com/wailsapp/wails/v2/pkg/runtime"
)

//go:embed all:frontend/dist
var assets embed.FS

func main() {
	// Make German + English spellcheck dictionaries available to WebView2; the
	// per-field `lang` attribute then selects which one applies in the composer.
	if os.Getenv("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") == "" {
		_ = os.Setenv("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--accept-lang=de-DE,de,en-US,en")
	}

	// Parse optional mail-window args: --mode=mail --uid=X --acc=Y --fld=Z --subj=S
	mode, uid, acc, fld, subj, dark := parseMailArgs(os.Args[1:])

	app := NewApp()
	if mode == "mail" {
		app.mailMode = true
		app.mailUID = uid
		app.mailAcc = acc
		app.mailFld = fld
		app.mailDark = dark
		if err := wails.Run(&options.App{
			Title:            "N-MailClient — " + subj,
			Width:            820,
			Height:           640,
			MinWidth:         560,
			MinHeight:        400,
			Frameless:        true,
			AssetServer:      &assetserver.Options{Assets: assets},
			BackgroundColour: &options.RGBA{R: 0, G: 0, B: 0, A: 0},
			Windows: &windows.Options{
				WebviewIsTransparent: true,
				WindowIsTranslucent:  true,
				BackdropType:         windows.Mica,
			},
			OnStartup:  app.startup,
			OnDomReady: app.domReady,
			Bind:       []interface{}{app},
		}); err != nil {
			println("Error:", err.Error())
		}
		return
	}

	// Start tray icon; double-click shows, Quit menu entry quits the app.
	RunTray(
		func() { // onShow
			if app.ctx != nil {
				wruntime.WindowShow(app.ctx)
			}
		},
		func() { // onQuit
			if app.ctx != nil {
				wruntime.Quit(app.ctx)
			}
		},
	)

	// Normal main window
	if err := wails.Run(&options.App{
		Title:            "N-MailClient",
		Width:            1280,
		Height:           820,
		MinWidth:         900,
		MinHeight:        600,
		Frameless:        true,
		AssetServer:      &assetserver.Options{Assets: assets},
		BackgroundColour: &options.RGBA{R: 0, G: 0, B: 0, A: 0},
		Windows: &windows.Options{
			WebviewIsTransparent: true,
			WindowIsTranslucent:  true,
			BackdropType:         windows.Mica,
		},
		OnStartup:  app.startup,
		OnDomReady: app.domReady,
		Bind:       []interface{}{app},
	}); err != nil {
		println("Error:", err.Error())
	}
}

func parseMailArgs(args []string) (mode string, uid uint32, acc, fld, subj string, dark bool) {
	for _, a := range args {
		switch {
		case a == "--mode=mail":
			mode = "mail"
		case strings.HasPrefix(a, "--uid="):
			n, _ := strconv.ParseUint(a[6:], 10, 32)
			uid = uint32(n)
		case strings.HasPrefix(a, "--acc="):
			acc = a[6:]
		case strings.HasPrefix(a, "--fld="):
			fld = a[6:]
		case strings.HasPrefix(a, "--subj="):
			subj = a[7:]
		case a == "--dark=1":
			dark = true
		}
	}
	return
}

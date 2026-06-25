import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react'
import {
  Bold,
  Italic,
  Link2,
  List,
  ListOrdered,
  Quote,
  RemoveFormatting,
  Strikethrough,
  Underline
} from 'lucide-react'

export interface RichEditorHandle {
  getHTML: () => string
  getText: () => string
  focus: () => void
}

interface Props {
  initialHtml: string
}

/** execCommand helper (deprecated API but reliable in Electron/Chromium). */
function exec(command: string, value?: string): void {
  document.execCommand(command, false, value)
}

const RichTextEditor = forwardRef<RichEditorHandle, Props>(({ initialHtml }, ref) => {
  const elRef = useRef<HTMLDivElement>(null)
  const savedRange = useRef<Range | null>(null)
  const [showLink, setShowLink] = useState(false)
  const [linkUrl, setLinkUrl] = useState('')

  // Seed content once on mount.
  useEffect(() => {
    if (elRef.current) elRef.current.innerHTML = initialHtml
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useImperativeHandle(ref, () => ({
    getHTML: () => elRef.current?.innerHTML ?? '',
    getText: () => elRef.current?.innerText ?? '',
    focus: () => elRef.current?.focus()
  }))

  function cmd(command: string, value?: string): void {
    elRef.current?.focus()
    exec(command, value)
  }

  function saveSelection(): void {
    const sel = window.getSelection()
    if (sel && sel.rangeCount > 0) savedRange.current = sel.getRangeAt(0).cloneRange()
  }

  function restoreSelection(): void {
    const sel = window.getSelection()
    if (sel && savedRange.current) {
      sel.removeAllRanges()
      sel.addRange(savedRange.current)
    }
  }

  function applyLink(): void {
    const url = linkUrl.trim()
    setShowLink(false)
    setLinkUrl('')
    if (!url) return
    elRef.current?.focus()
    restoreSelection()
    const sel = window.getSelection()
    if (sel && sel.toString()) {
      exec('createLink', url)
    } else {
      exec('insertHTML', `<a href="${url}">${url}</a>`)
    }
  }

  const Btn = ({
    onClick,
    title,
    children
  }: {
    onClick: () => void
    title: string
    children: React.ReactNode
  }): JSX.Element => (
    <button
      type="button"
      title={title}
      onMouseDown={(e) => e.preventDefault()} // keep the editor selection
      onClick={onClick}
      className="rounded p-1.5 text-gray-600 hover:bg-gray-100"
    >
      {children}
    </button>
  )

  return (
    <div className="rounded border">
      <div className="flex flex-wrap items-center gap-0.5 border-b bg-gray-50 px-1 py-1">
        <Btn title="Fett" onClick={() => cmd('bold')}>
          <Bold className="h-4 w-4" />
        </Btn>
        <Btn title="Kursiv" onClick={() => cmd('italic')}>
          <Italic className="h-4 w-4" />
        </Btn>
        <Btn title="Unterstrichen" onClick={() => cmd('underline')}>
          <Underline className="h-4 w-4" />
        </Btn>
        <Btn title="Durchgestrichen" onClick={() => cmd('strikeThrough')}>
          <Strikethrough className="h-4 w-4" />
        </Btn>
        <span className="mx-1 h-5 w-px bg-gray-300" />
        <Btn title="Aufzählung" onClick={() => cmd('insertUnorderedList')}>
          <List className="h-4 w-4" />
        </Btn>
        <Btn title="Nummerierte Liste" onClick={() => cmd('insertOrderedList')}>
          <ListOrdered className="h-4 w-4" />
        </Btn>
        <Btn title="Zitat" onClick={() => cmd('formatBlock', 'blockquote')}>
          <Quote className="h-4 w-4" />
        </Btn>
        <span className="mx-1 h-5 w-px bg-gray-300" />
        <Btn
          title="Link"
          onClick={() => {
            saveSelection()
            setShowLink((v) => !v)
          }}
        >
          <Link2 className="h-4 w-4" />
        </Btn>
        <Btn title="Formatierung entfernen" onClick={() => cmd('removeFormat')}>
          <RemoveFormatting className="h-4 w-4" />
        </Btn>
      </div>

      {showLink && (
        <div className="flex items-center gap-2 border-b bg-white px-2 py-1.5">
          <input
            autoFocus
            className="flex-1 rounded border px-2 py-1 text-sm"
            placeholder="https://…"
            value={linkUrl}
            onChange={(e) => setLinkUrl(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') applyLink()
              if (e.key === 'Escape') setShowLink(false)
            }}
          />
          <button
            type="button"
            onClick={applyLink}
            className="rounded bg-brand px-3 py-1 text-sm text-white"
          >
            Einfügen
          </button>
        </div>
      )}

      <div
        ref={elRef}
        contentEditable
        className="rich-editor h-56 overflow-y-auto px-3 py-2 text-sm focus:outline-none"
      />
    </div>
  )
})

RichTextEditor.displayName = 'RichTextEditor'
export default RichTextEditor

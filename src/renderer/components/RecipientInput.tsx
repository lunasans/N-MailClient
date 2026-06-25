import { useMemo, useRef, useState } from 'react'
import { useMailStore } from '../store/useMailStore'

interface Suggestion {
  name: string
  email: string
  photo?: string
}

interface Props {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  className?: string
  autoFocus?: boolean
}

function formatRecipient(name: string, email: string): string {
  return name && name !== '(ohne Namen)' ? `${name} <${email}>` : email
}

export default function RecipientInput({
  value,
  onChange,
  placeholder,
  className,
  autoFocus
}: Props): JSX.Element {
  const contacts = useMailStore((s) => s.contacts)
  const [open, setOpen] = useState(false)
  const [highlight, setHighlight] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)

  // The token currently being typed (after the last comma).
  const lastComma = value.lastIndexOf(',')
  const token = value.slice(lastComma + 1).trim().toLowerCase()

  const suggestions = useMemo<Suggestion[]>(() => {
    if (token.length < 1) return []
    const out: Suggestion[] = []
    for (const c of contacts) {
      for (const email of c.emails) {
        if (c.fullName.toLowerCase().includes(token) || email.toLowerCase().includes(token)) {
          out.push({ name: c.fullName, email, photo: c.photo })
          if (out.length >= 6) return out
        }
      }
    }
    return out
  }, [contacts, token])

  function choose(s: Suggestion): void {
    const prefix = lastComma >= 0 ? value.slice(0, lastComma + 1) + ' ' : ''
    onChange(prefix + formatRecipient(s.name, s.email) + ', ')
    setOpen(false)
    setHighlight(0)
    inputRef.current?.focus()
  }

  const showList = open && suggestions.length > 0

  return (
    <div className={`relative ${className ?? ''}`}>
      <input
        ref={inputRef}
        className="w-full rounded border px-3 py-2 text-sm"
        placeholder={placeholder}
        value={value}
        autoFocus={autoFocus}
        onChange={(e) => {
          onChange(e.target.value)
          setOpen(true)
          setHighlight(0)
        }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        onKeyDown={(e) => {
          if (!showList) return
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            setHighlight((h) => (h + 1) % suggestions.length)
          } else if (e.key === 'ArrowUp') {
            e.preventDefault()
            setHighlight((h) => (h - 1 + suggestions.length) % suggestions.length)
          } else if (e.key === 'Enter' || e.key === 'Tab') {
            e.preventDefault()
            choose(suggestions[highlight])
          } else if (e.key === 'Escape') {
            setOpen(false)
          }
        }}
      />
      {showList && (
        <div className="absolute left-0 right-0 top-full z-10 mt-1 max-h-56 overflow-y-auto rounded-md border bg-white py-1 shadow-lg">
          {suggestions.map((s, i) => (
            <button
              key={s.email + i}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => choose(s)}
              className={`flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm ${
                i === highlight ? 'bg-blue-50' : 'hover:bg-gray-100'
              }`}
            >
              {s.photo ? (
                <img src={s.photo} alt="" className="h-6 w-6 shrink-0 rounded-full object-cover" />
              ) : (
                <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-gray-100 text-xs text-gray-600">
                  {s.name.slice(0, 1).toUpperCase()}
                </span>
              )}
              <span className="min-w-0">
                <span className="block truncate">{s.name}</span>
                <span className="block truncate text-xs text-gray-400">{s.email}</span>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

import { clearTranslate, decryptPassword, getTranslate, setTranslate } from './db'
import type { TranslateConfig, TranslateResult } from '../types'

// Machine translation via a (typically self-hosted) LibreTranslate instance.
// Email content is sent to the configured server only — choose a server you trust.

function base(url: string): string {
  return url.replace(/\/+$/, '')
}

/** Public translate config (without the API key) for the renderer. */
export function getPublicConfig(): TranslateConfig | null {
  const c = getTranslate()
  return c ? { url: c.url, target: c.target } : null
}

/** Validate a LibreTranslate connection by listing languages. */
export async function testConnection(url: string, apiKey: string): Promise<void> {
  const res = await fetch(`${base(url)}/languages`, {
    headers: apiKey ? { Authorization: `Bearer ${apiKey}` } : undefined
  })
  if (!res.ok) throw new Error(`Server antwortete mit ${res.status}.`)
  const langs = (await res.json()) as unknown
  if (!Array.isArray(langs)) throw new Error('Unerwartete Antwort vom Server.')
}

/** Validate then persist the translation connection. */
export async function saveConfig(url: string, target: string, apiKey: string): Promise<void> {
  await testConnection(url, apiKey)
  setTranslate({ url: base(url), target }, apiKey)
}

export function clearConfig(): void {
  clearTranslate()
}

/** Translate text (or HTML) to the configured target language (source auto-detected). */
export async function translate(text: string, isHtml = false): Promise<TranslateResult> {
  const c = getTranslate()
  if (!c) throw new Error('Kein Übersetzungsdienst konfiguriert.')
  const key = c.secret ? decryptPassword(c.secret) : ''
  const res = await fetch(`${base(c.url)}/translate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      q: text,
      source: 'auto',
      target: c.target,
      format: isHtml ? 'html' : 'text',
      ...(key ? { api_key: key } : {})
    })
  })
  if (!res.ok) {
    let msg = `Übersetzung fehlgeschlagen (${res.status}).`
    try {
      const j = (await res.json()) as { error?: string }
      if (j.error) msg = j.error
    } catch {
      /* keep generic message */
    }
    throw new Error(msg)
  }
  const data = (await res.json()) as {
    translatedText: string
    detectedLanguage?: { language?: string }
  }
  return { text: data.translatedText, detected: data.detectedLanguage?.language }
}

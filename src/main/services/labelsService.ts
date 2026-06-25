import { randomUUID } from 'crypto'
import type { Label } from '../types'
import { getLabels, setLabels } from './db'

/** Derive a valid, unique IMAP keyword from a label name. */
function keywordFor(name: string, existing: Label[]): string {
  const base = name.replace(/[^A-Za-z0-9]+/g, '_').replace(/^_+|_+$/g, '') || 'label'
  const used = new Set(existing.map((l) => l.keyword))
  let kw = base
  let i = 1
  while (used.has(kw)) kw = `${base}_${++i}`
  return kw
}

export function listLabels(): Label[] {
  return getLabels()
}

export function addLabel(name: string, color: string): Label {
  const labels = getLabels()
  const label: Label = { id: randomUUID(), name: name.trim(), color, keyword: keywordFor(name, labels) }
  setLabels([...labels, label])
  return label
}

export function removeLabel(id: string): void {
  setLabels(getLabels().filter((l) => l.id !== id))
}

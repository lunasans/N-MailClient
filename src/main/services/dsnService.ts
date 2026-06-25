import type { DeliveryInfo } from '../types'

// Parse a Delivery Status Notification (RFC 3464). A DSN is a
// multipart/report; report-type=delivery-status message whose middle part
// (message/delivery-status) carries per-recipient fields like Action, Status,
// Final-Recipient and Diagnostic-Code.

function field(region: string, name: string): string | undefined {
  const m = region.match(new RegExp('^' + name + '\\s*:\\s*(.+)$', 'im'))
  return m ? m[1].trim() : undefined
}

/** Strip the "rfc822;" / "smtp;" address-type prefix from a DSN field value. */
function stripType(value?: string): string | undefined {
  return value?.replace(/^[^;]*;\s*/, '').trim()
}

/** Return delivery info if the raw message is a DSN report, else null. */
export function parseDeliveryReport(raw: string): DeliveryInfo | null {
  const head = raw.slice(0, 4000)
  const isReport = /content-type:\s*multipart\/report/i.test(head)
  const hasStatusPart = /content-type:\s*message\/delivery-status/i.test(raw)
  if (!isReport && !hasStatusPart) return null

  const idx = raw.search(/content-type:\s*message\/delivery-status/i)
  const region = idx >= 0 ? raw.slice(idx) : raw

  const action = (field(region, 'Action') ?? '').toLowerCase()
  const status: DeliveryInfo['status'] = action.includes('failed')
    ? 'failed'
    : action.includes('delayed')
      ? 'delayed'
      : action.includes('delivered')
        ? 'delivered'
        : action.includes('relayed') || action.includes('expanded')
          ? 'relayed'
          : 'unknown'

  // Without an Action field this is probably not a real per-recipient report.
  if (!action && status === 'unknown') return null

  return {
    status,
    recipient: stripType(field(region, 'Final-Recipient') ?? field(region, 'Original-Recipient')),
    code: field(region, 'Status'),
    diagnostic: stripType(field(region, 'Diagnostic-Code'))
  }
}

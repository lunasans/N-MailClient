import type { Account } from '@shared/index'

/** Fallback palette used when an account has no explicit color. */
export const ACCOUNT_PALETTE = [
  '#2563eb', // blue
  '#16a34a', // green
  '#dc2626', // red
  '#9333ea', // purple
  '#ea580c', // orange
  '#0891b2', // cyan
  '#ca8a04', // amber
  '#db2777' // pink
]

/** Resolve an account's color: explicit if set, else a palette color by index. */
export function colorForAccount(account: Pick<Account, 'color'>, index: number): string {
  return account.color || ACCOUNT_PALETTE[index % ACCOUNT_PALETTE.length]
}

/** Color by account id, looking up its position in the accounts list. */
export function colorById(accounts: Account[], accountId: string): string {
  const i = accounts.findIndex((a) => a.id === accountId)
  return i === -1 ? ACCOUNT_PALETTE[0] : colorForAccount(accounts[i], i)
}

import { useEffect, useState } from 'react'
import { Undo2 } from 'lucide-react'
import { useMailStore } from '../store/useMailStore'

export default function UndoSendBanner(): JSX.Element | null {
  const pending = useMailStore((s) => s.pendingSend)
  const undoSend = useMailStore((s) => s.undoSend)
  const [remaining, setRemaining] = useState(0)

  useEffect(() => {
    if (!pending) return
    const tick = (): void =>
      setRemaining(Math.max(0, Math.ceil((pending.deadline - Date.now()) / 1000)))
    tick()
    const id = setInterval(tick, 250)
    return () => clearInterval(id)
  }, [pending])

  if (!pending) return null

  return (
    <div className="fixed bottom-6 left-1/2 z-40 flex -translate-x-1/2 items-center gap-4 rounded-lg bg-gray-900 px-4 py-2.5 text-sm text-white shadow-lg">
      <span>
        Nachricht wird gesendet{remaining > 0 ? ` (${remaining}s)` : '…'}
      </span>
      <button
        onClick={undoSend}
        className="flex items-center gap-1.5 rounded bg-white/15 px-3 py-1 hover:bg-white/25"
      >
        <Undo2 className="h-4 w-4" />
        Rückgängig
      </button>
    </div>
  )
}

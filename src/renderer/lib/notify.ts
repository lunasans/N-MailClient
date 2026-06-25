/**
 * Notification sound. Prefers a user-supplied WAV at
 * src/renderer/assets/notification.wav (bundled by Vite). Falls back to a
 * short synthesized chime if no file is present.
 */

let cachedUrl: string | null | undefined // undefined = not yet resolved

function getWavUrl(): string | null {
  if (cachedUrl !== undefined) return cachedUrl
  try {
    const mods = import.meta.glob('../assets/notification.wav', {
      eager: true,
      query: '?url',
      import: 'default'
    })
    cachedUrl = (Object.values(mods)[0] as string | undefined) ?? null
  } catch {
    cachedUrl = null
  }
  return cachedUrl
}

function synthChime(): void {
  try {
    const Ctx =
      window.AudioContext ||
      (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    const ctx = new Ctx()
    const now = ctx.currentTime
    for (const n of [
      { f: 880, t: 0 },
      { f: 1175, t: 0.12 }
    ]) {
      const o = ctx.createOscillator()
      const g = ctx.createGain()
      o.connect(g)
      g.connect(ctx.destination)
      o.type = 'sine'
      o.frequency.value = n.f
      const start = now + n.t
      g.gain.setValueAtTime(0.0001, start)
      g.gain.exponentialRampToValueAtTime(0.18, start + 0.02)
      g.gain.exponentialRampToValueAtTime(0.0001, start + 0.18)
      o.start(start)
      o.stop(start + 0.2)
    }
    setTimeout(() => ctx.close(), 600)
  } catch {
    /* audio not available — ignore */
  }
}

export function playChime(): void {
  const url = getWavUrl()
  if (url) {
    try {
      const audio = new Audio(url)
      audio.volume = 0.6
      void audio.play()
      return
    } catch {
      /* fall through to synth */
    }
  }
  synthChime()
}

/** Show a desktop notification (best effort). */
export function showMailNotification(title: string, body: string): void {
  try {
    new Notification(title, { body })
  } catch {
    /* notifications unavailable — ignore */
  }
}

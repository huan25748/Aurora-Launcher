import { Minus, Square, X, Play, Maximize2 } from 'lucide-react'
import { useStore } from '../store'
import { cn } from '../api'

export function BrandMark() {
  return (
    <span className="brand-mark">
      <svg viewBox="0 0 24 24" fill="none" aria-hidden>
        <defs>
          <linearGradient id="brand-aurora" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#5eead4" />
            <stop offset="0.52" stopColor="#818cf8" />
            <stop offset="1" stopColor="#f472b6" />
          </linearGradient>
        </defs>
        <path d="M5.2 19.2 L12 4.8 L18.8 19.2" stroke="url(#brand-aurora)" strokeWidth="2.8" strokeLinecap="round" strokeLinejoin="round" />
        <path d="M8.3 14.2 H15.7" stroke="#eafffb" strokeWidth="2.2" strokeLinecap="round" />
      </svg>
    </span>
  )
}

export function TitleBar() {
  const game = useStore((s) => s.game)
  const progress = useStore((s) => s.progress)
  const maximized = useStore((s) => s.maximized)
  const setMaximized = useStore((s) => s.setMaximized)
  const setConsoleOpen = useStore((s) => s.setConsoleOpen)
  const consoleOpen = useStore((s) => s.consoleOpen)
  const navigate = useStore((s) => s.navigate)

  const active = progress.groups.find((g) => g.status === 'running')
  const running = game.state === 'running'

  const statusText = running
    ? `正在运行 · ${game.instanceName ?? ''}`
    : active
      ? `${active.title} · ${active.steps.find((s) => s.status === 'running')?.title ?? '准备中'}`
      : '就绪'

  return (
    <div className="titlebar">
      <div className="brand">
        <BrandMark />
        <span>Aurora</span>
        <span className="brand-sub">Launcher</span>
      </div>
      <div className="titlebar-actions">
        <button
          className="titlebar-chip"
          onClick={() => setConsoleOpen(!consoleOpen)}
          title={consoleOpen ? '收起控制台' : '打开控制台'}
          style={{ marginRight: 6 }}
        >
          {running ? <Play size={12} style={{ color: 'var(--success)' }} /> : active ? <span className="spinner" style={{ width: 12, height: 12 }} /> : <span style={{ width: 7, height: 7, borderRadius: 99, background: 'var(--faint)' }} />}
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{statusText}</span>
        </button>
        <button
          className="win-btn"
          title="切换主题"
          onClick={() => {
            const settings = useStore.getState().settings
            if (!settings) return
            const next = settings.theme === 'dark' ? 'light' : 'dark'
            void useStore.getState().patchSettings({ theme: next })
          }}
        >
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
          </svg>
        </button>
        <button
          className="win-btn"
          title="打开实例"
          onClick={() => navigate({ name: 'instances' })}
        >
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
            <path d="M3.3 7L12 12l8.7-5M12 22V12" />
          </svg>
        </button>
        <div style={{ width: 1, height: 18, background: 'var(--line)', margin: '0 4px' }} />
        <button className="win-btn" onClick={() => void window.aurora.app.window.minimize()} title="最小化">
          <Minus size={15} />
        </button>
        <button
          className="win-btn"
          onClick={async () => {
            const max = await window.aurora.app.window.toggleMaximize()
            setMaximized(max)
          }}
          title={maximized ? '还原' : '最大化'}
        >
          {maximized ? <Square size={13} /> : <Maximize2 size={13} />}
        </button>
        <button className={cn('win-btn', 'close')} onClick={() => void window.aurora.app.window.close()} title="关闭">
          <X size={15} />
        </button>
      </div>
    </div>
  )
}

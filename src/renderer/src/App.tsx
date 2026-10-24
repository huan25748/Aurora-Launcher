import { useEffect, useMemo } from 'react'
import { api, formatDuration, GRADIENTS } from './api'
import { useStore } from './store'
import { TitleBar } from './components/TitleBar'
import { Sidebar } from './components/Sidebar'
import { ConsoleDock } from './components/ConsoleDock'
import { UpdateDialog } from './components/UpdateDialog'
import { PageErrorBoundary } from './components/ErrorBoundary'
import { Toasts } from './components/ui'
import { HomePage } from './pages/Home'
import { InstancesPage } from './pages/Instances'
import { DownloadPage } from './pages/Download'
import { AccountsPage } from './pages/Accounts'
import { SettingsPage } from './pages/Settings'
import { AboutPage } from './pages/About'
import { InstanceDetailPage } from './pages/InstanceDetail'
import { ProjectPage } from './pages/Project'

function accentText(hex: string, light: boolean): string {
  if (!light) return hex
  const value = hex.replace("#", "")
  if (value.length !== 6) return hex
  const factor = 0.5
  const r = Math.round(parseInt(value.slice(0, 2), 16) * factor)
  const g = Math.round(parseInt(value.slice(2, 4), 16) * factor)
  const b = Math.round(parseInt(value.slice(4, 6), 16) * factor)
  return `rgb(${r}, ${g}, ${b})`
}

function useTheme(): void {
  const settings = useStore((s) => s.settings)
  useEffect(() => {
    const root = document.documentElement
    const accent = GRADIENTS[settings?.accent ?? 'aurora'] ?? GRADIENTS.aurora
    root.style.setProperty('--acc1', accent[0])
    root.style.setProperty('--acc2', accent[1])
    root.style.setProperty('--acc3', accent[2])
    let mode = settings?.theme ?? 'dark'
    if (mode === 'system') {
      mode = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'
    }
    root.dataset.theme = mode
    root.style.setProperty('--acc-text', accentText(accent[0], mode === 'light'))
    root.lang = settings?.language === 'en-US' ? 'en' : 'zh-CN'
  }, [settings?.accent, settings?.theme, settings?.language])
}

export function App() {
  const booted = useStore((s) => s.booted)
  const page = useStore((s) => s.page)
  const consoleOpen = useStore((s) => s.consoleOpen)
  const settings = useStore((s) => s.settings)
  const checkUpdates = useStore((s) => s.checkUpdates)
  const init = useStore((s) => s.init)
  const navigate = useStore((s) => s.navigate)
  const setProgress = useStore((s) => s.setProgress)
  const setGame = useStore((s) => s.setGame)
  const pushLogs = useStore((s) => s.pushLogs)
  const setSettings = useStore((s) => s.setSettings)
  const setInstances = useStore((s) => s.setInstances)
  const setMaximized = useStore((s) => s.setMaximized)
  const toast = useStore((s) => s.toast)

  useTheme()

  useEffect(() => {
    if (!booted || !settings?.checkUpdates) return
    const timer = window.setTimeout(() => void checkUpdates(false), 4000)
    return () => window.clearTimeout(timer)
  }, [booted, settings?.checkUpdates, checkUpdates])

  useEffect(() => {
    void init()
    const unsubs = [
      api.events.onProgress((p) => setProgress(p)),
      api.events.onLog((line) => pushLogs([line])),
      api.events.onGameState((state) => {
        setGame(state)
        if (state.state === 'crashed') {
          toast('error', state.crashReport ?? `游戏异常退出（代码 ${state.exitCode ?? '?'}）`, '游戏崩溃')
        } else if (state.state === 'exited') {
          toast('info', '游戏已退出', state.instanceName)
        }
      }),
      api.events.onSettings((s) => setSettings(s)),
      api.events.onAccounts(() => {
        void useStore.getState().refreshAccounts()
      }),
      api.events.onInstances((list) => setInstances(list)),
      api.events.onToast((t) => toast(t.type as never, t.message, t.title)),
      api.events.onConsoleToggle((open) => useStore.getState().setConsoleOpen(open)),
      api.events.onMaximized((max) => setMaximized(max))
    ]
    const onError = (e: Event) => toast('error', (e as CustomEvent<string>).detail)
    window.addEventListener('aurora-error', onError)
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !document.querySelector('.overlay')) {
        const state = useStore.getState()
        if (state.page.name === 'project') navigate({ name: 'download' })
        else if (state.history.length && state.page.name !== 'home') state.back()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      unsubs.forEach((u) => u())
      window.removeEventListener('aurora-error', onError)
      window.removeEventListener('keydown', onKey)
    }
  }, [])

  useEffect(() => {
    if (!booted) return
    const hash = window.location.hash.replace(/^#\/?/, '')
    if (!hash) return
    const [name, id] = hash.split('/')
    const allowed = ['home', 'instances', 'instance', 'download', 'accounts', 'settings', 'about', 'project']
    if (allowed.includes(name)) navigate({ name: name as never, id })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [booted])

  const content = useMemo(() => {
    switch (page.name) {
      case 'home':
        return <HomePage />
      case 'instances':
        return <InstancesPage />
      case 'instance':
        return <InstanceDetailPage id={page.id ?? ''} tab={page.extra} />
      case 'download':
        return <DownloadPage />
      case 'project':
        return <ProjectPage id={page.id ?? ''} />
      case 'accounts':
        return <AccountsPage />
      case 'settings':
        return <SettingsPage />
      case 'about':
        return <AboutPage />
      default:
        return <HomePage />
    }
  }, [page])

  if (!booted) {
    return (
      <div className="app" style={{ display: 'grid', placeItems: 'center' }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 16 }}>
          <div className="brand-mark" style={{ width: 52, height: 52, borderRadius: 18 }}>
            <svg viewBox="0 0 24 24" fill="none" style={{ width: 28, height: 28 }}>
              <path d="M3 17c3.5 0 4.7-9 9-9 3.4 0 4.6 6 9 6" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" />
            </svg>
          </div>
          <div className="spinner lg" style={{ color: 'var(--acc1)' }} />
          <div style={{ color: 'var(--muted)', fontSize: 13 }}>正在启动 Aurora Launcher…</div>
        </div>
      </div>
    )
  }

  return (
    <div className="app">
      <TitleBar />
      <div className="app-body">
        <Sidebar />
        <main className="main">
          <div
            className="page-scroll"
            key={`${page.name}:${page.id ?? ''}`}
            style={{ paddingBottom: consoleOpen ? 396 : 40, transition: 'padding-bottom .3s ease' }}
          >
            <PageErrorBoundary key={`${page.name}:${page.id ?? ''}`} onReset={() => navigate({ name: 'home' })}>
              {content}
            </PageErrorBoundary>
          </div>
        </main>
      </div>
      <ConsoleDock />
      <UpdateDialog />
      <Toasts />
    </div>
  )
}

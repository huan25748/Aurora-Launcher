import { create } from 'zustand'
import type { Account, AppInfo, GameState, Instance, LogLine, ProgressSnapshot, Settings, UpdateInfo } from '@shared/types'
import type { TKey } from './i18n'
import { translate } from './i18n'
import { api, errorMessage } from './api'

export interface ToastItem {
  id: number
  type: 'info' | 'success' | 'error' | 'warning'
  title?: string
  message: string
  leaving?: boolean
}

export type PageName = 'home' | 'instances' | 'instance' | 'download' | 'accounts' | 'settings' | 'about' | 'project'

export interface Page {
  name: PageName
  id?: string
  extra?: string
  from?: Page
}

export interface StoreState {
  booted: boolean
  settings: Settings | null
  appInfo: AppInfo | null
  instances: Instance[]
  accounts: Account[]
  activeAccount: Account | null
  progress: ProgressSnapshot
  game: GameState
  logs: LogLine[]
  toasts: ToastItem[]
  consoleOpen: boolean
  page: Page
  history: Page[]
  downloadTab: string
  downloadSourceTab: 'modrinth' | 'curseforge'
  maximized: boolean
  settingsSection: string
  updateInfo: UpdateInfo | null

  setSettings: (s: Settings) => void
  patchSettings: (patch: Partial<Settings>) => Promise<void>
  setInstances: (list: Instance[]) => void
  setAccounts: (list: Account[]) => void
  setActiveAccount: (a: Account | null) => void
  setProgress: (p: ProgressSnapshot) => void
  setGame: (g: GameState) => void
  pushLogs: (lines: LogLine[]) => void
  clearLogs: () => void
  toast: (type: ToastItem['type'], message: string, title?: string) => void
  dismissToast: (id: number) => void
  markLeaving: (id: number) => void
  setConsoleOpen: (open: boolean) => void
  navigate: (page: Page) => void
  setDownloadView: (tab: string, source: 'modrinth' | 'curseforge') => void
  back: () => void
  setMaximized: (v: boolean) => void
  setSettingsSection: (s: string) => void
  checkUpdates: (manual?: boolean) => Promise<void>
  setUpdateInfo: (info: UpdateInfo | null) => void
  refreshInstances: () => Promise<void>
  refreshAccounts: () => Promise<void>
  t: (key: TKey) => string
  init: () => Promise<void>
}

let toastId = 1

export const useStore = create<StoreState>((set, get) => ({
  booted: false,
  settings: null,
  appInfo: null,
  instances: [],
  accounts: [],
  activeAccount: null,
  progress: { groups: [] },
  game: { state: 'idle' },
  logs: [],
  toasts: [],
  consoleOpen: false,
  page: { name: 'home' },
  history: [],
  downloadTab: 'vanilla',
  downloadSourceTab: 'modrinth',
  maximized: false,
  settingsSection: 'general',
  updateInfo: null,

  setSettings: (s) => set({ settings: s }),
  patchSettings: async (patch) => {
    const current = get().settings
    if (!current) return
    set({ settings: { ...current, ...patch } })
    try {
      const next = await api.settings.update(patch)
      set({ settings: next })
    } catch (e) {
      set({ settings: current })
      get().toast('error', errorMessage(e))
    }
  },
  setInstances: (list) => set({ instances: list }),
  setAccounts: (list) => set({ accounts: list }),
  setActiveAccount: (a) => set({ activeAccount: a }),
  setProgress: (p) => set({ progress: p }),
  setGame: (g) => set({ game: g }),
  pushLogs: (lines) =>
    set((state) => {
      const merged = [...state.logs, ...lines]
      return { logs: merged.length > 600 ? merged.slice(-600) : merged }
    }),
  clearLogs: () => set({ logs: [] }),
  toast: (type, message, title) => {
    const id = toastId++
    set((state) => ({ toasts: [...state.toasts, { id, type, message, title }].slice(-5) }))
    setTimeout(() => get().markLeaving(id), 4600)
    setTimeout(() => get().dismissToast(id), 5000)
  },
  dismissToast: (id) => set((state) => ({ toasts: state.toasts.filter((t) => t.id !== id) })),
  markLeaving: (id) => set((state) => ({ toasts: state.toasts.map((t) => (t.id === id ? { ...t, leaving: true } : t)) })),
  setConsoleOpen: (open) => set({ consoleOpen: open }),
  navigate: (page) => set((state) => ({ page, history: [...state.history, state.page].slice(-30) })),
  setDownloadView: (downloadTab, downloadSourceTab) => set({ downloadTab, downloadSourceTab }),
  back: () =>
    set((state) => {
      const history = [...state.history]
      const prev = history.pop()
      return prev ? { page: prev, history } : { page: { name: 'home' } }
    }),
  setMaximized: (v) => set({ maximized: v }),
  setSettingsSection: (s) => set({ settingsSection: s }),
  setUpdateInfo: (updateInfo) => set({ updateInfo }),
  checkUpdates: async (manual = false) => {
    try {
      const info = await api.updates.check(manual)
      if (info.hasUpdate) set({ updateInfo: info })
      else if (manual) get().toast('success', '当前已是最新版本')
    } catch (e) {
      if (manual) get().toast('error', errorMessage(e), '检查更新失败')
    }
  },
  refreshInstances: async () => {
    try {
      const list = await api.instances.list()
      set({ instances: list })
    } catch {}
  },
  refreshAccounts: async () => {
    try {
      const [list, active] = await Promise.all([api.accounts.list(), api.accounts.active()])
      set({ accounts: list, activeAccount: active })
    } catch {}
  },
  t: (key) => {
    const lang = get().settings?.language ?? 'zh-CN'
    return translate(lang, key)
  },
  init: async () => {
    try {
      const [settings, instances, accounts, active, appInfo, game, logs] = await Promise.all([
        api.settings.get(),
        api.instances.list(),
        api.accounts.list(),
        api.accounts.active(),
        api.app.info(),
        api.launch.state(),
        api.logs.latest()
      ])
      set({
        settings,
        instances,
        accounts,
        activeAccount: active,
        appInfo,
        game,
        logs: logs.slice(-400),
        booted: true
      })
    } catch (e) {
      set({ booted: true })
      get().toast('error', `初始化失败: ${errorMessage(e)}`)
    }
  }
}))

export function useT(): (key: TKey) => string {
  const lang = useStore((s) => s.settings?.language ?? 'zh-CN')
  return (key: TKey) => translate(lang, key)
}

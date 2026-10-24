import { Boxes, Download, Home, Info, Settings, Users } from 'lucide-react'
import { useStore, type PageName } from '../store'
import { cn } from '../api'
import { useT } from '../store'
import type { TKey } from '../i18n'

const ITEMS: { name: PageName; label: TKey; icon: React.ReactNode }[] = [
  { name: 'home', label: 'nav.home', icon: <Home /> },
  { name: 'instances', label: 'nav.instances', icon: <Boxes /> },
  { name: 'download', label: 'nav.download', icon: <Download /> },
  { name: 'accounts', label: 'nav.accounts', icon: <Users /> }
]

export function Sidebar() {
  const page = useStore((s) => s.page)
  const navigate = useStore((s) => s.navigate)
  const instances = useStore((s) => s.instances)
  const appInfo = useStore((s) => s.appInfo)
  const t = useT()

  const isActive = (name: PageName): boolean => {
    if (name === 'instances') return page.name === 'instances' || page.name === 'instance'
    if (name === 'download') return page.name === 'download' || page.name === 'project'
    return page.name === name
  }

  return (
    <aside className="sidebar">
      <div className="nav-section-label">{t('nav.section.game')}</div>
      {ITEMS.map((item) => (
        <button key={item.name} className={cn('nav-item', isActive(item.name) && 'active')} onClick={() => navigate({ name: item.name })}>
          {item.icon}
          <span>{t(item.label)}</span>
          {item.name === 'instances' && instances.length > 0 && <span className="nav-badge">{instances.length}</span>}
        </button>
      ))}

      <div className="nav-section-label">{t('nav.section.other')}</div>
      <button className={cn('nav-item', page.name === 'settings' && 'active')} onClick={() => navigate({ name: 'settings' })}>
        <Settings />
        <span>{t('nav.settings')}</span>
      </button>
      <button className={cn('nav-item', page.name === 'about' && 'active')} onClick={() => navigate({ name: 'about' })}>
        <Info />
        <span>{t('nav.about')}</span>
      </button>

      <div className="sidebar-spacer" />

      <div className="sidebar-foot">
        <div className="row">
          <span style={{ fontWeight: 600, color: 'var(--muted)' }}>Aurora {appInfo?.version ?? '1.0.0'}</span>
        </div>
      </div>
    </aside>
  )
}

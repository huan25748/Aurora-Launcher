import { useEffect, useMemo, useState } from 'react'
import { ArrowRight, ChevronDown, Clock3, Download, Layers, Play, Plus, Sparkles, Square } from 'lucide-react'
import type { NewsItem } from '@shared/types'
import { api, formatDate, formatDuration, heroGradientVars, LOADER_LABEL, timeAgo } from '../api'
import { useStore, useT } from '../store'
import { BareLoaderIcon, InstanceCard, type InstanceAction } from '../components/cards'
import { AccountAvatar } from '../components/cards'
import { Button, Dropdown, EmptyState, Section, Skeleton } from '../components/ui'

export function HomePage() {
  const t = useT()
  const instances = useStore((s) => s.instances)
  const settings = useStore((s) => s.settings)
  const activeAccount = useStore((s) => s.activeAccount)
  const appInfo = useStore((s) => s.appInfo)
  const game = useStore((s) => s.game)
  const progress = useStore((s) => s.progress)
  const navigate = useStore((s) => s.navigate)
  const patchSettings = useStore((s) => s.patchSettings)
  const toast = useStore((s) => s.toast)

  const [selectedId, setSelectedId] = useState<string>(settings?.lastSelectedInstanceId ?? '')
  const [news, setNews] = useState<NewsItem[] | null>(null)
  const [busy, setBusy] = useState(false)

  const selected = useMemo(
    () => instances.find((i) => i.id === selectedId) ?? instances.find((i) => i.id === settings?.lastSelectedInstanceId) ?? instances[0],
    [instances, selectedId, settings?.lastSelectedInstanceId]
  )
  const runningThis = game.state === 'running' && game.instanceId === selected?.id
  const activeGroup = progress.groups.find((g) => g.status === 'running')

  useEffect(() => {
    if (selected && selected.id !== selectedId) setSelectedId(selected.id)
  }, [selected?.id])

  useEffect(() => {
    let alive = true
    api.news
      .list()
      .then((items) => alive && setNews(items))
      .catch(() => alive && setNews([]))
    return () => {
      alive = false
    }
  }, [])

  const selectInstance = (id: string): void => {
    setSelectedId(id)
    void patchSettings({ lastSelectedInstanceId: id })
  }

  const play = async (id?: string): Promise<void> => {
    if (!activeAccount) {
      toast('warning', '请先在账户页面添加账户（离线或微软均可）')
      navigate({ name: 'accounts' })
      return
    }
    const target = instances.find((i) => i.id === (id ?? selected?.id))
    if (!target) {
      navigate({ name: 'download' })
      return
    }
    setBusy(true)
    try {
      const result = await api.launch.start({ instanceId: target.id })
      if (!result.ok) toast('error', result.error ?? '启动失败', '启动失败')
    } catch (e) {
      toast('error', e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const onCardAction = (action: InstanceAction, instance: { id: string }): void => {
    switch (action) {
      case 'play':
        void play(instance.id)
        break
      case 'open':
        void api.instances.openFolder(instance.id)
        break
      case 'settings':
        navigate({ name: 'instance', id: instance.id, extra: 'settings' })
        break
      case 'copy':
        void api.instances.copy(instance.id, '').then(() => toast('success', '实例已复制'))
        break
      case 'export':
        void api.instances.export(instance.id).then((r) => toast('success', `已导出到 ${r.path}`))
        break
      case 'export-pack':
        void api.instances.exportPack(instance.id).then((r) => toast('success', `已导出到 ${r.path}`, '整合包已导出'))
        break
      case 'delete':
        navigate({ name: 'instance', id: instance.id, extra: 'settings' })
        break
    }
  }


  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1>
            {t('home.title')}
          </h1>
          <p>{t('home.subtitle')}</p>
        </div>
        <div className="page-actions">
          <Button variant="soft" icon={<Download size={16} />} onClick={() => navigate({ name: 'download' })}>
            安装新版本
          </Button>
          <Button icon={<Plus size={16} />} onClick={() => navigate({ name: 'instances' })}>
            新建实例
          </Button>
        </div>
      </div>

      <div className="home-hero">
        {selected ? (
          <div className="hero-card" style={heroGradientVars(selected.color)}>
            <div className="hero-aura" />
            <div className="hero-top">
              <div>
                <div className="hero-eyebrow">当前实例</div>
                <Dropdown
                  align="left"
                  trigger={(toggle) => (
                    <button className="hero-instance" onClick={toggle} title="切换实例">
                      <BareLoaderIcon instance={selected} className="hero-icon" />
                      <span style={{ maxWidth: 420, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{selected.name}</span>
                      <ChevronDown size={18} style={{ opacity: 0.7 }} />
                    </button>
                  )}
                  items={instances.map((i) => ({
                    label: i.name,
                    icon: (
                      <span className="menu-instance-mark">
                        <BareLoaderIcon instance={i} className="menu-instance-icon" />
                        {i.id === selected.id && <i className="menu-active-dot" />}
                      </span>
                    ),
                    onClick: () => selectInstance(i.id)
                  }))}
                />
                <div className="hero-meta">
                  <span className="pill">
                    <Layers size={12} /> {selected.mcVersion || selected.versionId}
                  </span>
                  <span className="pill">{LOADER_LABEL[selected.loader] ?? selected.loader}{selected.loaderVersion ? ` ${selected.loaderVersion}` : ''}</span>
                </div>
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                {runningThis ? (
                  <button className="hero-play" onClick={() => void api.launch.stop()}>
                    <Square size={18} fill="currentColor" /> 停止游戏
                  </button>
                ) : (
                  <button className="hero-play" disabled={busy || !!activeGroup} onClick={() => void play()}>
                    {busy ? <span className="spinner" /> : <Play size={20} />}
                    {busy ? '正在启动…' : activeGroup ? '任务进行中…' : t('common.play')}
                  </button>
                )}
              </div>
            </div>
            <div className="hero-bottom">
              <div className="hero-last">
                {selected.lastPlayedAt ? `上次游玩 ${timeAgo(selected.lastPlayedAt)}` : '这个实例还没有启动过'}
                {selected.totalPlayTimeMs ? ` · 累计 ${formatDuration(selected.totalPlayTimeMs)}` : ''}
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button
                  className="pill"
                  style={{ cursor: 'pointer' }}
                  onClick={() => navigate({ name: 'instance', id: selected.id })}
                >
                  管理实例 <ArrowRight size={12} />
                </button>
              </div>
            </div>
          </div>
        ) : (
          <div className="hero-card" style={heroGradientVars('aurora')}>
            <div className="hero-aura" />
            <div className="hero-top">
              <div>
                <div className="hero-eyebrow">欢迎使用 Aurora Launcher</div>
                <div className="hero-instance">开启你的第一个世界</div>
                <div className="hero-meta">
                  <span className="pill">全新界面</span>
                  <span className="pill">内置 Modrinth</span>
                  <span className="pill">自动安装 Java</span>
                </div>
              </div>
            </div>
            <div className="hero-bottom">
              <div className="hero-last">还没有游戏实例，先安装一个 Minecraft 版本吧</div>
              <button className="hero-play" onClick={() => navigate({ name: 'download' })}>
                <Download size={18} /> 前往下载
              </button>
            </div>
          </div>
        )}

        <div className="hero-side">
          <div className="card account-card" onClick={() => navigate({ name: 'accounts' })}>
            <AccountAvatar account={activeAccount} size="lg" />
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 11, color: 'var(--muted)', fontWeight: 650, letterSpacing: '0.08em', textTransform: 'uppercase' }}>
                {t('home.account')}
              </div>
              <div style={{ fontWeight: 700, fontSize: 15, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {activeAccount?.username ?? '未登录'}
              </div>
              <div style={{ fontSize: 11, color: 'var(--faint)', marginTop: 1 }}>
                {activeAccount ? (activeAccount.type === 'microsoft' ? '微软正版账户' : activeAccount.type === 'authlib' ? activeAccount.authServerName ?? '第三方验证' : '离线账户') : '点击添加账户'}
              </div>
            </div>
            <ArrowRight size={16} style={{ color: 'var(--faint)' }} />
          </div>

          <div className="quick-stats">
            <div className="stat">
              <div className="v">{formatDuration(appInfo?.totalPlayTimeMs ?? 0)}</div>
              <div className="k">{t('home.totalTime')}</div>
            </div>
            <div className="stat">
              <div className="v">{appInfo?.totalVersions ?? 0}</div>
              <div className="k">{t('home.totalVersions')}</div>
            </div>
            <div className="stat">
              <div className="v">{appInfo?.totalInstances ?? 0}</div>
              <div className="k">{t('home.totalInstances')}</div>
            </div>

          </div>
        </div>
      </div>

      <Section
        title={t('home.recent')}
        count={instances.length}
        extra={
          instances.length > 0 ? (
            <button className="link" onClick={() => navigate({ name: 'instances' })}>
              查看全部 <ArrowRight size={13} />
            </button>
          ) : undefined
        }
      >
        {instances.length === 0 ? (
          <div className="card">
            <EmptyState
              icon={<Sparkles />}
              title={t('home.noInstance')}
              description="安装一个游戏版本，创建实例后即可在这里快速启动。"
              action={
                <Button variant="primary" icon={<Download size={16} />} onClick={() => navigate({ name: 'download' })}>
                  打开下载中心
                </Button>
              }
            />
          </div>
        ) : (
          <div className="instance-grid">
            {instances.slice(0, 4).map((inst) => (
              <InstanceCard
                key={inst.id}
                instance={inst}
                onPlay={(id) => void play(id)}
                onOpen={(id) => navigate({ name: 'instance', id })}
                onAction={onCardAction}
              />
            ))}
          </div>
        )}
      </Section>

      <Section
        title={t('home.news')}
        extra={
          <button className="link" onClick={() => navigate({ name: 'about' })}>
            更多 <ArrowRight size={13} />
          </button>
        }
      >
        <div className="card news-card">
          {news === null && (
            <div style={{ padding: 10, display: 'flex', flexDirection: 'column', gap: 10 }}>
              <Skeleton height={64} />
              <Skeleton height={64} />
              <Skeleton height={64} />
            </div>
          )}
          {news?.length === 0 && <EmptyState icon={<Sparkles />} title="暂无资讯" description="稍后再来看看吧。" />}
          {news?.slice(0, 3).map((item) => (
            <div
              key={item.id}
              className="news-item"
              onClick={() => {
                if (item.url) void api.app.openExternal(item.url)
              }}
            >
              {item.image && <div className="news-thumb"><img src={item.image} alt="" /></div>}
              <div className="news-body">
                <div className="news-title">{item.title}</div>
                <div className="news-desc">{item.summary}</div>
                <div className="news-meta">
                  {item.tag && <span>{item.tag}</span>}
                  {item.date && <span>{formatDate(item.date)}</span>}
                </div>
              </div>
            </div>
          ))}
        </div>
      </Section>
    </div>
  )
}

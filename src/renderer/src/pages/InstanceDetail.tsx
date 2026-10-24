import { useEffect, useMemo, useState } from 'react'
import {
  ArrowLeft,
  Archive,
  Boxes,
  Bug,
  Camera,
  CheckCircle2,
  Clock3,
  Copy,
  Cpu,
  Download as DownloadIcon,
  FileText,
  FolderOpen,
  Gauge,
  Globe2,
  HardDrive,
  Image as ImageIcon,
  Info,
  Layers,
  ListTree,
  Loader2,
  MemoryStick,
  MoreHorizontal,
  Package,
  Play,
  RefreshCw,
  Save,
  Search,
  Server,
  Settings2,
  Sparkles,
  Square,
  Terminal,
  Trash2,
  Upload,
  X,
  Zap
} from 'lucide-react'
import type { ContentItem, CrashAnalysis, JavaInfo, LogFileInfo, ScreenshotItem, ServerEntry, ServerStatus, WorldItem } from '@shared/types'
import { api, cn, errorMessage, fileUrl, formatBytes, formatDate, formatDuration, instanceGradientVars, LOADER_LABEL, timeAgo } from '../api'
import { useStore } from '../store'
import { AccountAvatar, LoaderIcon } from '../components/cards'
import { Badge, Button, ConfirmDialog, Dropdown, EmptyState, Field, Input, Lightbox, Modal, ProgressBar, Select, Skeleton, Slider, Switch, Textarea } from '../components/ui'

type DetailTab = 'overview' | 'mods' | 'resourcepacks' | 'shaderpacks' | 'worlds' | 'screenshots' | 'servers' | 'logs' | 'settings'

const TABS: { key: DetailTab; label: string; icon: React.ReactNode }[] = [
  { key: 'overview', label: '概览', icon: <Info /> },
  { key: 'mods', label: '模组', icon: <Package /> },
  { key: 'resourcepacks', label: '资源包', icon: <Layers /> },
  { key: 'shaderpacks', label: '光影', icon: <Sparkles /> },
  { key: 'worlds', label: '世界', icon: <Globe2 /> },
  { key: 'screenshots', label: '截图', icon: <Camera /> },
  { key: 'servers', label: '服务器', icon: <Server /> },
  { key: 'logs', label: '日志', icon: <Terminal /> },
  { key: 'settings', label: '设置', icon: <Settings2 /> }
]

const CONTENT_KIND: Partial<Record<DetailTab, 'mods' | 'resourcepacks' | 'shaderpacks'>> = {
  mods: 'mods',
  resourcepacks: 'resourcepacks',
  shaderpacks: 'shaderpacks'
}

export function InstanceDetailPage({ id, tab: initialTab }: { id: string; tab?: string }) {
  const instances = useStore((s) => s.instances)
  const navigate = useStore((s) => s.navigate)
  const toast = useStore((s) => s.toast)
  const game = useStore((s) => s.game)
  const refreshInstances = useStore((s) => s.refreshInstances)
  const instance = instances.find((i) => i.id === id)
  const [tab, setTab] = useState<DetailTab>((initialTab as DetailTab) || 'overview')
  const [counts, setCounts] = useState<Record<string, number>>({})
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleteFiles, setDeleteFiles] = useState(true)
  const running = game.state === 'running' && game.instanceId === id

  useEffect(() => {
    if (!instance) return
    void Promise.all([
      api.content.list(id, 'mods').catch(() => []),
      api.content.list(id, 'resourcepacks').catch(() => []),
      api.content.list(id, 'shaderpacks').catch(() => []),
      api.content.worlds(id).catch(() => []),
      api.content.screenshots(id).catch(() => []),
      api.servers.list(id).catch(() => [])
    ]).then(([mods, packs, shaders, worlds, shots, servers]) => {
      setCounts({
        mods: mods.length,
        resourcepacks: packs.length,
        shaderpacks: shaders.length,
        worlds: worlds.length,
        screenshots: shots.length,
        servers: servers.length
      })
    })
  }, [id, instance?.id])

  if (!instance) {
    return (
      <div className="page-enter">
        <div className="card" style={{ marginTop: 30 }}>
          <EmptyState icon={<Info />} title="实例不存在" description="它可能已被删除。" action={<Button onClick={() => navigate({ name: 'instances' })}>返回实例列表</Button>} />
        </div>
      </div>
    )
  }

  const play = async (): Promise<void> => {
    if (running) {
      await api.launch.stop()
      return
    }
    const result = await api.launch.start({ instanceId: instance.id })
    if (!result.ok) toast('error', result.error ?? '启动失败')
  }

  return (
    <div className="page-enter">
      <div className="detail-head" style={instanceGradientVars(instance.color)}>
        <div className="detail-cover">
          <LoaderIcon instance={instance} className="detail-icon" />
        </div>
        <div className="detail-main">
          <h1>
            {instance.name}
            {running && <Badge tone="ok">运行中</Badge>}
          </h1>
          <div className="detail-tags">
            <span className="tag acc">{instance.mcVersion || instance.versionId}</span>
            <span className="tag">{LOADER_LABEL[instance.loader] ?? instance.loader}{instance.loaderVersion ? ` ${instance.loaderVersion}` : ''}</span>
            <span className="tag">{instance.memory ? `${instance.memory} MB` : '自动内存'}</span>
            {instance.imported && <span className="tag">从 .minecraft 导入</span>}
            {instance.lastPlayedAt && <span className="tag">上次游玩 {timeAgo(instance.lastPlayedAt)}</span>}
          </div>
        </div>
        <div className="detail-actions">
          <Button variant="ghost" size="sm" icon={<ArrowLeft size={16} />} onClick={() => navigate({ name: 'instances' })}>
            返回
          </Button>
          <Button variant={running ? 'danger' : 'primary'} icon={running ? <Square size={16} fill="currentColor" /> : <Play size={16} fill="currentColor" />} onClick={() => void play()}>
            {running ? '停止游戏' : '启动游戏'}
          </Button>
          <Dropdown
            trigger={(toggle) => (
              <Button variant="ghost" size="sm" icon={<MoreHorizontal size={16} />} onClick={toggle} />
            )}
            items={[
              { label: '打开实例文件夹', icon: <FolderOpen size={15} />, onClick: () => void api.instances.openFolder(instance.id) },
              { label: '复制实例', icon: <Copy size={15} />, onClick: () => void api.instances.copy(instance.id, `${instance.name} 副本`).then(() => { toast('success', '实例已复制'); void refreshInstances() }) },
              ...(instance.imported ? [] : [{ label: '导出实例', icon: <DownloadIcon size={15} />, onClick: () => void api.instances.export(instance.id).then((r) => toast('success', `已导出到 ${r.path}`)) }]),
              { separator: true, label: 'sep' },
              { label: '删除实例', icon: <Trash2 size={15} />, danger: true, onClick: () => { setDeleteFiles(true); setDeleteOpen(true) } }
            ]}
          />
        </div>
      </div>

      <div className="tabs">
        {TABS.map((item) => (
          <button key={item.key} className={cn('tab', tab === item.key && 'active')} onClick={() => setTab(item.key)}>
            {item.icon}
            {item.label}
            {counts[item.key] !== undefined && <span className="n">{counts[item.key]}</span>}
          </button>
        ))}
      </div>

      {tab === 'overview' && <OverviewTab id={id} />}
      {(tab === 'mods' || tab === 'resourcepacks' || tab === 'shaderpacks') && (
        <ContentTab key={tab} id={id} kind={CONTENT_KIND[tab]!} onCount={(n) => setCounts((c) => ({ ...c, [tab]: n }))} />
      )}
      {tab === 'worlds' && <WorldsTab id={id} />}
      {tab === 'screenshots' && <ScreenshotsTab id={id} />}
      {tab === 'servers' && <ServersTab id={id} />}
      {tab === 'logs' && <LogsTab id={id} />}
      {tab === 'settings' && <SettingsTab id={id} />}

      <ConfirmDialog
        open={deleteOpen}
        title={`删除实例「${instance.name}」`}
        message={
          <div>
            <p>此操作不可撤销。导入实例会一并删除其对应的版本文件夹；共享版本和 .minecraft 根目录不会被删除。</p>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 12 }}>
              <Switch checked={deleteFiles} onChange={setDeleteFiles} />
              <span>同时删除实例文件夹</span>
            </div>
          </div>
        }
        confirmText="删除"
        danger
        onClose={() => setDeleteOpen(false)}
        onConfirm={() => {
          void api.instances
            .remove(instance.id, deleteFiles)
            .then(() => {
              toast('success', '实例已删除')
              void refreshInstances()
              navigate({ name: 'instances' })
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />
    </div>
  )
}

// ---------------------------------------------------------------------------
// Overview
// ---------------------------------------------------------------------------

function OverviewTab({ id }: { id: string }) {
  const instance = useStore((s) => s.instances.find((i) => i.id === id))!
  const activeAccount = useStore((s) => s.activeAccount)
  const game = useStore((s) => s.game)
  const settings = useStore((s) => s.settings)
  const navigate = useStore((s) => s.navigate)
  const [size, setSize] = useState<{ size: number; files: number } | null>(null)
  const [crash, setCrash] = useState<CrashAnalysis | null>(null)
  const [memory, setMemory] = useState(0)

  useEffect(() => {
    void api.instances.size(id).then(setSize).catch(() => {})
    void api.logs.analyze(id).then((c) => setCrash(c)).catch(() => {})
    void api.settings.systemInfo().then((s) => setMemory(Number(s.suggestedMemory ?? 4096))).catch(() => {})
  }, [id])

  const mem = instance.memory || settings?.defaultMemory || memory

  return (
    <div className="detail-grid">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        <div className="card card-pad">
          <div className="card-title">
            <Info /> 实例信息
          </div>
          <div className="info-list" style={{ marginTop: 8 }}>
            <InfoRow k="游戏版本" v={instance.mcVersion || instance.versionId} />
            <InfoRow k="加载器" v={`${LOADER_LABEL[instance.loader] ?? instance.loader}${instance.loaderVersion ? ` ${instance.loaderVersion}` : ''}`} />
            <InfoRow k="版本 ID" v={instance.versionId} mono />
            <InfoRow k="最大内存" v={`${mem} MB`} />
            <InfoRow k="游戏目录" v={instance.gameDir} mono />
            <InfoRow k="创建时间" v={formatDate(instance.createdAt)} />
            <InfoRow k="上次游玩" v={instance.lastPlayedAt ? timeAgo(instance.lastPlayedAt) : '从未'} />
            <InfoRow k="游玩时长" v={formatDuration(instance.totalPlayTimeMs)} />
            <InfoRow k="启动次数" v={`${instance.playCount ?? 0} 次`} />
            <InfoRow k="占用空间" v={size ? `${formatBytes(size.size)} · ${size.files} 个文件` : '统计中…'} />
          </div>
        </div>

        {crash && (
          <div className="card card-pad">
            <div className="card-title" style={{ color: 'var(--danger)' }}>
              <Bug /> 上次崩溃分析
              <Badge tone={crash.confidence === 'high' ? 'err' : crash.confidence === 'medium' ? 'warn' : undefined}>{crash.cause}</Badge>
            </div>
            <div style={{ color: 'var(--text-soft)', fontSize: 12.5, lineHeight: 1.7, marginTop: 10 }}>{crash.summary}</div>
            {crash.suggestions.length > 0 && (
              <ul style={{ marginTop: 12, display: 'flex', flexDirection: 'column', gap: 6 }}>
                {crash.suggestions.map((s) => (
                  <li key={s} style={{ display: 'flex', gap: 8, fontSize: 12, color: 'var(--muted)' }}>
                    <CheckCircle2 size={14} style={{ color: 'var(--acc1)', flex: 'none', marginTop: 1 }} /> {s}
                  </li>
                ))}
              </ul>
            )}
            {crash.evidence.length > 0 && (
              <pre style={{ marginTop: 12, padding: 12, borderRadius: 12, background: 'var(--panel)', border: '1px solid var(--line)', fontSize: 10.5, color: 'var(--faint)', overflow: 'auto', maxHeight: 160, userSelect: 'text' }}>
                {crash.evidence.join('\n')}
              </pre>
            )}
          </div>
        )}

        {game.state === 'crashed' && game.instanceId === id && game.crashReport && !crash && (
          <div className="card card-pad" style={{ borderColor: 'color-mix(in srgb, var(--danger) 40%, transparent)' }}>
            <div className="card-title" style={{ color: 'var(--danger)' }}>
              <Bug /> 游戏异常退出
            </div>
            <div style={{ color: 'var(--muted)', fontSize: 12.5, marginTop: 8, userSelect: 'text' }}>{game.crashReport}</div>
          </div>
        )}
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        <div className="card setting-group">
          <h3>快捷操作</h3>
          <div className="sub">常用目录与工具</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 4 }}>
            {[
              ['实例文件夹', '', <FolderOpen size={15} />],
              ['模组目录', 'mods', <Package size={15} />],
              ['存档目录', 'saves', <Globe2 size={15} />],
              ['截图目录', 'screenshots', <Camera size={15} />],
              ['日志目录', 'logs', <Terminal size={15} />]
            ].map(([label, sub, icon]) => (
              <button key={label as string} className="menu-item" style={{ height: 36 }} onClick={() => void api.instances.openFolder(id, sub as string)}>
                {icon as React.ReactNode} {label as string}
              </button>
            ))}
            <div className="menu-sep" />
            <button className="menu-item" style={{ height: 36 }} onClick={() => navigate({ name: 'download' })}>
              <DownloadIcon size={15} /> 安装模组与资源
            </button>
          </div>
        </div>

        <div className="card setting-group">
          <h3>账户</h3>
          <div className="sub">当前用于启动游戏的账户</div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 6 }}>
            <AccountAvatar account={activeAccount} />
            <div>
              <div style={{ fontWeight: 650 }}>{activeAccount?.username ?? '未登录'}</div>
              <div style={{ color: 'var(--faint)', fontSize: 11 }}>{activeAccount ? '启动时将使用此账户' : '请先添加账户'}</div>
            </div>
          </div>
          <Button variant="ghost" size="sm" block style={{ marginTop: 10 }} onClick={() => navigate({ name: 'accounts' })}>
            管理账户
          </Button>
        </div>
      </div>
    </div>
  )
}

function InfoRow({ k, v, mono }: { k: string; v: React.ReactNode; mono?: boolean }) {
  return (
    <div className="info-row">
      <span className="k">{k}</span>
      <span className="v" style={mono ? { fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--muted)' } : undefined}>
        {v}
      </span>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Content (mods / resourcepacks / shaderpacks)
// ---------------------------------------------------------------------------

function ContentTab({ id, kind, onCount }: { id: string; kind: 'mods' | 'resourcepacks' | 'shaderpacks'; onCount: (n: number) => void }) {
  const toast = useStore((s) => s.toast)
  const navigate = useStore((s) => s.navigate)
  const [items, setItems] = useState<ContentItem[] | null>(null)
  const [query, setQuery] = useState('')
  const [removeTarget, setRemoveTarget] = useState<ContentItem | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = async (): Promise<void> => {
    const list = await api.content.list(id, kind)
    setItems(list)
    onCount(list.length)
  }

  useEffect(() => {
    setItems(null)
    void load().catch((e) => {
      setItems([])
      toast('error', errorMessage(e))
    })
  }, [id, kind])

  const filtered = useMemo(() => {
    if (!items) return null
    if (!query.trim()) return items
    const q = query.toLowerCase()
    return items.filter((i) => i.name.toLowerCase().includes(q) || i.fileName.toLowerCase().includes(q) || i.meta?.id?.toLowerCase().includes(q))
  }, [items, query])

  const enabledCount = items?.filter((i) => i.enabled).length ?? 0

  const label = kind === 'mods' ? '模组' : kind === 'resourcepacks' ? '资源包' : '光影'

  return (
    <>
      <div className="content-toolbar">
        <div className="input-with-icon" style={{ flex: 1, maxWidth: 320 }}>
          <Search />
          <input className="input search" placeholder={`搜索${label}…`} value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <span style={{ color: 'var(--faint)', fontSize: 12 }}>
          已启用 {enabledCount} / {items?.length ?? 0}
        </span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
          <Button variant="soft" size="sm" icon={<Upload size={14} />} onClick={() => void api.content.import(id, kind).then((n) => { if (n) { toast('success', `已导入 ${n} 个文件`); void load() } })}>
            导入
          </Button>
          <Button variant="ghost" size="sm" icon={<FolderOpen size={14} />} onClick={() => void api.instances.openFolder(id, kind)}>
            打开目录
          </Button>
          <Button size="sm" icon={<DownloadIcon size={14} />} onClick={() => navigate({ name: 'download' })}>
            获取更多
          </Button>
        </div>
      </div>

      {items === null && (
        <div className="content-list">
          {Array.from({ length: 4 }).map((_, i) => (
            <div className="content-row" key={i}>
              <Skeleton height={42} width={42} style={{ borderRadius: 12 }} />
              <div style={{ flex: 1 }}>
                <Skeleton height={14} width="40%" />
                <Skeleton height={11} width="70%" style={{ marginTop: 6 }} />
              </div>
            </div>
          ))}
        </div>
      )}

      {filtered?.length === 0 && (
        <div className="card">
          <EmptyState
            icon={<Package />}
            title={`还没有${label}`}
            description="从下载中心浏览 Modrinth 上的内容，或者导入本地文件。"
            action={<Button variant="primary" icon={<DownloadIcon size={16} />} onClick={() => navigate({ name: 'download' })}>浏览下载中心</Button>}
          />
        </div>
      )}

      <div className="content-list">
        {filtered?.map((item) => (
          <div className={cn('content-row', !item.enabled && 'disabled')} key={item.fileName}>
            <div className="content-icon">
              {item.iconData ? <img src={item.iconData} alt="" /> : item.icon ? <img src={fileUrl(item.icon)} alt="" /> : kind === 'mods' ? <Package size={18} /> : kind === 'shaderpacks' ? <Sparkles size={18} /> : <Layers size={18} />}
            </div>
            <div className="content-body">
              <div className="content-name">
                {item.name}
                {item.meta?.version && <span className="tag">{item.meta.version}</span>}
                {item.meta?.loader && <span className="tag acc">{item.meta.loader}</span>}
                {!item.enabled && <Badge>已禁用</Badge>}
              </div>
              {item.meta?.description && <div className="content-desc">{item.meta.description}</div>}
              <div className="content-meta">
                <span>{item.fileName}</span>
                <span>{formatBytes(item.size)}</span>
                {item.meta?.authors?.length ? <span>{item.meta.authors.slice(0, 2).join(', ')}</span> : null}
              </div>
            </div>
            <div className="row-actions">
              <Switch
                checked={item.enabled}
                onChange={() =>
                  void api.content
                    .toggle(id, kind, item.fileName)
                    .then(() => load())
                    .catch((e) => toast('error', errorMessage(e)))
                }
              />
              <button className="icon-btn" title="在文件夹中显示" onClick={() => void api.app.showItem(item.path)}>
                <FolderOpen />
              </button>
              <button className="icon-btn danger" title="删除" onClick={() => setRemoveTarget(item)}>
                <Trash2 />
              </button>
            </div>
          </div>
        ))}
      </div>

      <ConfirmDialog
        open={!!removeTarget}
        title="删除文件"
        message={`确定要删除「${removeTarget?.name}」吗？此操作不可撤销。`}
        confirmText="删除"
        danger
        onClose={() => setRemoveTarget(null)}
        onConfirm={() => {
          if (!removeTarget) return
          void api.content
            .remove(id, kind, removeTarget.fileName)
            .then(() => {
              toast('success', '已删除')
              void load()
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />
    </>
  )
}

// ---------------------------------------------------------------------------
// Worlds
// ---------------------------------------------------------------------------

function WorldsTab({ id }: { id: string }) {
  const toast = useStore((s) => s.toast)
  const [worlds, setWorlds] = useState<WorldItem[] | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<WorldItem | null>(null)

  const load = (): Promise<void> =>
    api.content.worlds(id).then((w) => setWorlds(w)).catch((e) => {
      setWorlds([])
      toast('error', errorMessage(e))
    })

  useEffect(() => {
    setWorlds(null)
    void load()
  }, [id])

  return (
    <>
      <div className="content-toolbar">
        <Button variant="ghost" size="sm" icon={<FolderOpen size={14} />} onClick={() => void api.instances.openFolder(id, 'saves')}>
          打开存档目录
        </Button>
        <Button size="sm" icon={<RefreshCw size={14} />} onClick={() => void load()}>
          刷新
        </Button>
      </div>
      {worlds === null && (
        <div className="world-grid">
          {Array.from({ length: 4 }).map((_, i) => (
            <div className="world-card" key={i}>
              <Skeleton height={116} style={{ borderRadius: 0 }} />
              <div className="world-info">
                <Skeleton height={14} width="60%" />
              </div>
            </div>
          ))}
        </div>
      )}
      {worlds?.length === 0 && (
        <div className="card">
          <EmptyState icon={<Globe2 />} title="还没有世界" description="启动游戏后创建的世界会显示在这里。" />
        </div>
      )}
      <div className="world-grid">
        {worlds?.map((world) => (
          <div className="world-card" key={world.folderName} onClick={() => void api.app.openPath(world.path)}>
            <div className="world-thumb">
              {world.icon ? <img src={world.icon} alt="" /> : '🌍'}
            </div>
            <div className="world-info">
              <div className="world-name" title={world.name}>
                {world.name}
              </div>
              <div className="world-sub">
                <span>{world.gameVersion ?? ''}</span>
                <span>{world.size ? formatBytes(world.size) : ''}</span>
              </div>
              <div className="world-sub" style={{ marginTop: 4 }}>
                {world.hardcore && <Badge tone="err">极限</Badge>}
                {world.cheats && <Badge>作弊</Badge>}
                <span style={{ marginLeft: 'auto' }}>{world.lastPlayed ? timeAgo(world.lastPlayed) : timeAgo(world.mtime)}</span>
              </div>
              <div style={{ display: 'flex', gap: 4, marginTop: 8 }} onClick={(e) => e.stopPropagation()}>
                <Button size="sm" variant="soft" icon={<Play size={13} />} onClick={() => void api.launch.start({ instanceId: id, world: world.folderName }).then((r) => { if (!r.ok) toast('error', r.error ?? '') })}>
                  游玩
                </Button>
                <button className="icon-btn" title="备份" onClick={() => void api.content.backupWorld(id, world.folderName).then((r) => toast('success', `已备份到 ${r.path}`)).catch((e) => toast('error', errorMessage(e)))}>
                  <Archive />
                </button>
                <button className="icon-btn" title="打开目录" onClick={() => void api.app.openPath(world.path)}>
                  <FolderOpen />
                </button>
                <button className="icon-btn danger" title="删除" onClick={() => setDeleteTarget(world)}>
                  <Trash2 />
                </button>
              </div>
            </div>
          </div>
        ))}
      </div>
      <ConfirmDialog
        open={!!deleteTarget}
        title="删除世界"
        message={`确定要永久删除世界「${deleteTarget?.name}」吗？建议先备份。`}
        confirmText="删除"
        danger
        onClose={() => setDeleteTarget(null)}
        onConfirm={() => {
          if (!deleteTarget) return
          void api.content
            .deleteWorld(id, deleteTarget.folderName)
            .then(() => {
              toast('success', '世界已删除')
              void load()
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />
    </>
  )
}

// ---------------------------------------------------------------------------
// Screenshots
// ---------------------------------------------------------------------------

function ScreenshotsTab({ id }: { id: string }) {
  const [shots, setShots] = useState<ScreenshotItem[] | null>(null)
  const [preview, setPreview] = useState<string | null>(null)

  useEffect(() => {
    setShots(null)
    void api.content.screenshots(id).then(setShots).catch(() => setShots([]))
  }, [id])

  return (
    <>
      <div className="content-toolbar">
        <Button variant="ghost" size="sm" icon={<FolderOpen size={14} />} onClick={() => void api.instances.openFolder(id, 'screenshots')}>
          打开截图目录
        </Button>
        <span style={{ color: 'var(--faint)', fontSize: 12 }}>{shots?.length ?? 0} 张截图</span>
      </div>
      {shots?.length === 0 && (
        <div className="card">
          <EmptyState icon={<Camera />} title="还没有截图" description="在游戏中使用 F2 截图，它们会出现在这里。" />
        </div>
      )}
      <div className="shot-grid">
        {shots?.map((shot) => (
          <div className="shot" key={shot.fileName} onClick={() => setPreview(fileUrl(shot.path) ?? null)}>
            <img src={fileUrl(shot.path)} alt={shot.fileName} loading="lazy" />
          </div>
        ))}
      </div>
      {preview && <Lightbox src={preview} onClose={() => setPreview(null)} />}
    </>
  )
}

// ---------------------------------------------------------------------------
// Servers
// ---------------------------------------------------------------------------

function ServersTab({ id }: { id: string }) {
  const toast = useStore((s) => s.toast)
  const [servers, setServers] = useState<ServerEntry[] | null>(null)
  const [statuses, setStatuses] = useState<Record<string, ServerStatus>>({})
  const [addOpen, setAddOpen] = useState(false)
  const [name, setName] = useState('')
  const [ip, setIp] = useState('')

  const load = async (): Promise<void> => {
    const list = await api.servers.list(id)
    setServers(list)
    for (const s of list) {
      void api.servers.ping(s.ip).then((status) => setStatuses((prev) => ({ ...prev, [s.ip]: status })))
    }
  }

  useEffect(() => {
    setServers(null)
    void load().catch((e) => {
      setServers([])
      toast('error', errorMessage(e))
    })
  }, [id])

  return (
    <>
      <div className="content-toolbar">
        <span style={{ color: 'var(--muted)', fontSize: 12.5 }}>服务器列表保存在实例的 servers.dat 中</span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
          <Button size="sm" icon={<RefreshCw size={14} />} onClick={() => void load()}>
            刷新
          </Button>
          <Button size="sm" variant="primary" icon={<Server size={14} />} onClick={() => setAddOpen(true)}>
            添加服务器
          </Button>
        </div>
      </div>
      {servers?.length === 0 && (
        <div className="card">
          <EmptyState icon={<Server />} title="还没有服务器" description="添加常用服务器，之后可以从游戏内直接连接。" action={<Button variant="primary" onClick={() => setAddOpen(true)}>添加服务器</Button>} />
        </div>
      )}
      <div className="content-list">
        {servers?.map((server) => {
          const status = statuses[server.ip]
          return (
            <div className="content-row" key={server.ip}>
              <div className="content-icon">
                {server.icon ? <img src={server.icon} alt="" /> : status?.favicon ? <img src={status.favicon} alt="" /> : <Server size={18} />}
              </div>
              <div className="content-body">
                <div className="content-name">
                  {server.name}
                  {status?.online ? <Badge tone="ok">{status.players?.online ?? 0}/{status.players?.max ?? 0} 在线</Badge> : <Badge tone={status ? 'err' : undefined}>{status ? '离线' : '检测中'}</Badge>}
                </div>
                <div className="content-desc">{server.ip}</div>
                <div className="content-meta">
                  {status?.version?.name && <span>{status.version.name}</span>}
                  {status?.motd?.text && <span>{status.motd.text}</span>}
                  {status?.ping !== undefined && <span>{status.ping} ms</span>}
                </div>
              </div>
              <div className="row-actions">
                <Button size="sm" variant="soft" icon={<Play size={13} />} onClick={() => void api.launch.start({ instanceId: id, server: server.ip }).then((r) => { if (!r.ok) toast('error', r.error ?? '') })}>
                  加入
                </Button>
                <button
                  className="icon-btn danger"
                  title="删除"
                  onClick={() =>
                    void api.servers
                      .remove(id, server.ip)
                      .then(setServers)
                      .catch((e) => toast('error', errorMessage(e)))
                  }
                >
                  <Trash2 />
                </button>
              </div>
            </div>
          )
        })}
      </div>

      <Modal
        open={addOpen}
        onClose={() => setAddOpen(false)}
        title="添加服务器"
        footer={
          <>
            <Button variant="ghost" onClick={() => setAddOpen(false)}>
              取消
            </Button>
            <Button
              variant="primary"
              disabled={!ip.trim()}
              onClick={() =>
                void api.servers
                  .add(id, { name: name.trim() || ip.trim(), ip: ip.trim() })
                  .then((list) => {
                    setServers(list)
                    setAddOpen(false)
                    setName('')
                    setIp('')
                    void load()
                  })
                  .catch((e) => toast('error', errorMessage(e)))
              }
            >
              添加
            </Button>
          </>
        }
      >
        <Field label="服务器名称">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="我的服务器" />
        </Field>
        <Field label="服务器地址" hint="支持域名或 IP，可带端口，例如 play.example.com:25565">
          <Input value={ip} onChange={(e) => setIp(e.target.value)} placeholder="play.example.com" />
        </Field>
      </Modal>
    </>
  )
}

// ---------------------------------------------------------------------------
// Logs
// ---------------------------------------------------------------------------

function LogsTab({ id }: { id: string }) {
  const toast = useStore((s) => s.toast)
  const [files, setFiles] = useState<LogFileInfo[] | null>(null)
  const [selected, setSelected] = useState<LogFileInfo | null>(null)
  const [content, setContent] = useState('')
  const [crash, setCrash] = useState<CrashAnalysis | null>(null)
  const [loading, setLoading] = useState(false)

  const load = async (): Promise<void> => {
    setLoading(true)
    try {
      const list = await api.logs.files(id)
      setFiles(list)
      const target = selected ?? list[0]
      if (target) {
        setSelected(target)
        setContent(await api.logs.read(target.path, 500_000))
      }
      setCrash(await api.logs.analyze(id))
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
  }, [id])

  return (
    <div className="detail-grid">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <div className="content-toolbar" style={{ marginBottom: 0 }}>
          <span style={{ fontWeight: 650, fontSize: 14 }}>{selected?.name ?? '日志'}</span>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
            <Button size="sm" icon={<RefreshCw size={14} />} loading={loading} onClick={() => void load()}>
              刷新
            </Button>
            {selected && (
              <Button size="sm" variant="ghost" icon={<FolderOpen size={14} />} onClick={() => void api.app.showItem(selected.path)}>
                文件夹
              </Button>
            )}
            {selected && (
              <Button size="sm" variant="ghost" icon={<Copy size={14} />} onClick={() => void navigator.clipboard?.writeText(content).then(() => toast('success', '日志已复制'))}>
                复制
              </Button>
            )}
          </div>
        </div>
        <pre
          style={{
            margin: 0,
            padding: 16,
            borderRadius: 16,
            background: 'var(--panel-solid)',
            border: '1px solid var(--line)',
            fontFamily: 'var(--font-mono)',
            fontSize: 11.5,
            lineHeight: 1.65,
            color: 'var(--text-soft)',
            overflow: 'auto',
            height: 'calc(100vh - 430px)',
            minHeight: 280,
            userSelect: 'text'
          }}
        >
          {content || (loading ? '加载中…' : '没有日志内容')}
        </pre>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <div className="card setting-group">
          <h3>日志文件</h3>
          <div className="sub">{files?.length ?? 0} 个文件</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 4 }}>
            {files?.slice(0, 30).map((file) => (
              <button
                key={file.path}
                className="menu-item"
                style={{ height: 'auto', padding: '8px 10px', flexDirection: 'column', alignItems: 'flex-start', gap: 2, background: selected?.path === file.path ? 'var(--panel-hover)' : undefined }}
                onClick={() => {
                  setSelected(file)
                  void api.logs.read(file.path, 500_000).then(setContent)
                }}
              >
                <span style={{ fontSize: 12, fontWeight: 620, display: 'flex', gap: 6, alignItems: 'center' }}>
                  {file.kind === 'crash' ? <Bug size={12} style={{ color: 'var(--danger)' }} /> : <FileText size={12} />}
                  {file.name}
                </span>
                <span style={{ fontSize: 10, color: 'var(--faint)' }}>
                  {formatBytes(file.size)} · {formatDate(file.mtime)}
                </span>
              </button>
            ))}
            {files?.length === 0 && <div style={{ color: 'var(--muted)', fontSize: 12 }}>还没有日志文件</div>}
          </div>
        </div>

        {crash && (
          <div className="card setting-group" style={{ borderColor: 'color-mix(in srgb, var(--danger) 32%, transparent)' }}>
            <h3 style={{ color: 'var(--danger)' }}>崩溃分析 · {crash.cause}</h3>
            <div style={{ color: 'var(--muted)', fontSize: 12, lineHeight: 1.7, marginTop: 4 }}>{crash.summary}</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 10 }}>
              {crash.suggestions.map((s) => (
                <div key={s} style={{ fontSize: 11.5, color: 'var(--text-soft)', display: 'flex', gap: 7 }}>
                  <CheckCircle2 size={13} style={{ color: 'var(--acc1)', flex: 'none', marginTop: 1 }} /> {s}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Per-instance settings
// ---------------------------------------------------------------------------

function SettingsTab({ id }: { id: string }) {
  const instance = useStore((s) => s.instances.find((i) => i.id === id))!
  const settings = useStore((s) => s.settings)!
  const toast = useStore((s) => s.toast)
  const refreshInstances = useStore((s) => s.refreshInstances)
  const navigate = useStore((s) => s.navigate)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [name, setName] = useState(instance.name)
  const [memoryOverride, setMemoryOverride] = useState(!!instance.memory)
  const [memory, setMemory] = useState(instance.memory || settings.defaultMemory || 4096)
  const [javaPath, setJavaPath] = useState(instance.javaPath ?? '')
  const [jvmArgs, setJvmArgs] = useState(instance.jvmArgs ?? '')
  const [gameArgs, setGameArgs] = useState(instance.gameArgs ?? '')
  const [width, setWidth] = useState(String(instance.width ?? 854))
  const [height, setHeight] = useState(String(instance.height ?? 480))
  const [fullscreen, setFullscreen] = useState(!!instance.fullscreen)
  const [server, setServer] = useState(instance.serverToJoin ?? '')
  const [javas, setJavas] = useState<JavaInfo[] | null>(null)
  const [saving, setSaving] = useState(false)
  const [sysMem, setSysMem] = useState(16384)

  useEffect(() => {
    void api.java.list().then(setJavas).catch(() => setJavas([]))
    void api.settings.systemInfo().then((s) => setSysMem(Number(s.memoryMB ?? 16384))).catch(() => {})
  }, [])

  const maxMem = Math.max(4096, Math.floor((sysMem * 0.75) / 256) * 256)

  const save = async (): Promise<void> => {
    setSaving(true)
    try {
      await api.instances.update(id, {
        name: name.trim() || instance.name,
        memory: memoryOverride ? memory : undefined,
        javaPath: javaPath || undefined,
        jvmArgs: jvmArgs.trim() || undefined,
        gameArgs: gameArgs.trim() || undefined,
        width: Number(width) || 854,
        height: Number(height) || 480,
        fullscreen,
        serverToJoin: server.trim() || undefined
      } as never)
      await refreshInstances()
      toast('success', '实例设置已保存')
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="detail-grid">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        <div className="card setting-group">
          <h3>基本信息</h3>
          <div className="sub">实例名称与启动选项</div>
          <Field label="实例名称">
            <Input value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label="直接进入服务器" hint="启动后自动连接，留空则不自动进入">
            <Input value={server} onChange={(e) => setServer(e.target.value)} placeholder="play.example.com" />
          </Field>
          <div style={{ display: 'flex', gap: 14 }}>
            <Field label="窗口宽度" style={{ flex: 1 }}>
              <Input value={width} onChange={(e) => setWidth(e.target.value.replace(/\D/g, ''))} />
            </Field>
            <Field label="窗口高度" style={{ flex: 1 }}>
              <Input value={height} onChange={(e) => setHeight(e.target.value.replace(/\D/g, ''))} />
            </Field>
            <div style={{ flex: 1, display: 'flex', alignItems: 'flex-end', paddingBottom: 16 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Switch checked={fullscreen} onChange={setFullscreen} />
                <span style={{ fontSize: 12.5 }}>全屏</span>
              </div>
            </div>
          </div>
        </div>

        <div className="card setting-group">
          <h3>内存与 Java</h3>
          <div className="sub">为该实例单独指定运行环境</div>
          <div className="setting-row">
            <div className="info">
              <div className="name">自定义内存</div>
              <div className="desc">关闭时使用全局默认内存（{settings.defaultMemory} MB）</div>
            </div>
            <div className="control">
              <Switch checked={memoryOverride} onChange={setMemoryOverride} />
            </div>
          </div>
          {memoryOverride && (
            <div className="setting-row">
              <div className="info">
                <div className="name">最大内存</div>
                <div className="desc">建议根据模组数量调整，系统总内存 {sysMem} MB</div>
              </div>
              <div className="control wide">
                <div className="memory-input">
                  <Slider min={1024} max={maxMem} step={256} value={memory} onChange={setMemory} />
                  <span style={{ minWidth: 78, textAlign: 'right', fontWeight: 650, fontSize: 12.5 }}>{memory} MB</span>
                </div>
              </div>
            </div>
          )}
          <div className="setting-row">
            <div className="info">
              <div className="name">Java 运行时</div>
              <div className="desc">留空则按游戏版本自动选择</div>
            </div>
            <div className="control wide">
              <Select value={javaPath} onChange={(e) => setJavaPath(e.target.value)}>
                <option value="">自动选择</option>
                {javas?.map((j) => (
                  <option key={j.path} value={j.path}>
                    Java {j.major} · {j.vendor} · {j.version}
                  </option>
                ))}
              </Select>
            </div>
          </div>
        </div>

        <div className="card setting-group">
          <h3>启动参数</h3>
          <div className="sub">留空则使用全局参数</div>
          <Field label="JVM 参数" hint="追加到 Java 启动参数，例如 -XX:+UseZGC">
            <Textarea value={jvmArgs} onChange={(e) => setJvmArgs(e.target.value)} placeholder="-XX:+UseG1GC" />
          </Field>
          <Field label="游戏参数" hint="追加到游戏启动参数末尾">
            <Textarea value={gameArgs} onChange={(e) => setGameArgs(e.target.value)} placeholder="--width 1280 --height 720" />
          </Field>
        </div>

        <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end' }}>
          <Button variant="ghost" icon={<FolderOpen size={15} />} onClick={() => void api.instances.openFolder(id)}>
            打开实例文件夹
          </Button>
          <Button variant="primary" icon={<Save size={15} />} loading={saving} onClick={() => void save()}>
            保存设置
          </Button>
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        <div className="card setting-group">
          <h3>版本信息</h3>
          <div className="sub">由安装的版本决定，不可在此修改</div>
          <div className="info-list" style={{ marginTop: 4 }}>
            <InfoRow k="游戏版本" v={instance.mcVersion} />
            <InfoRow k="加载器" v={`${LOADER_LABEL[instance.loader]}${instance.loaderVersion ? ` ${instance.loaderVersion}` : ''}`} />
            <InfoRow k="版本 ID" v={instance.versionId} mono />
            <InfoRow k="游戏目录" v={instance.gameDir} mono />
          </div>
        </div>

        <div className="card setting-group" style={{ borderColor: 'color-mix(in srgb, var(--danger) 24%, transparent)' }}>
          <h3 style={{ color: 'var(--danger)' }}>危险操作</h3>
          <div className="sub">导入实例会删除整个游戏文件夹；共享版本文件夹仍被其他实例使用时不会删除</div>
          <Button
            variant="danger"
            block
            icon={<Trash2 size={15} />}
            onClick={() => setDeleteOpen(true)}
          >
            删除实例
          </Button>
        </div>
      </div>

      <ConfirmDialog
        open={deleteOpen}
        title={`删除实例「${instance.name}」`}
        message={<p>此操作不可撤销。导入实例会一并删除整个游戏文件夹；共享版本和 .minecraft 根目录不会被删除。</p>}
        confirmText="删除"
        danger
        onClose={() => setDeleteOpen(false)}
        onConfirm={() => {
          void api.instances
            .remove(id, true)
            .then(() => {
              toast('success', '实例已删除')
              void refreshInstances()
              navigate({ name: 'instances' })
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />
    </div>
  )
}

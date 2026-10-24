import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  Boxes,
  CalendarDays,
  Download as DownloadIcon,
  Layers,
  Package,
  Palette,
  Plus,
  Search,
  Sparkles,
  Star,
  Zap
} from 'lucide-react'
import type { ContentFilterKind, CurseForgeFile, CurseForgeHit, CurseForgeProject, LoaderVersion, McVersionSummary, ModrinthHit, VersionManifest } from '@shared/types'
import { api, cn, errorMessage, formatDate, formatNumber, LOADER_LABEL } from '../api'
import { useStore, useT } from '../store'
import { LoaderMark } from '../components/cards'
import { CurseForgeTab } from './CurseForgeTab'
import { Badge, Button, Chip, EmptyState, Field, Input, Modal, Select, Skeleton, Switch } from '../components/ui'

type Tab = 'vanilla' | 'loaders' | 'mods' | 'resourcepacks' | 'shaders' | 'modpacks'

const TABS: { key: Tab; label: string; icon: React.ReactNode; type?: ContentFilterKind }[] = [
  { key: 'vanilla', label: '原版游戏', icon: <Box /> },
  { key: 'loaders', label: '模组加载器', icon: <Layers /> },
  { key: 'mods', label: '模组', icon: <Package />, type: 'mod' },
  { key: 'resourcepacks', label: '资源包', icon: <Palette />, type: 'resourcepack' },
  { key: 'shaders', label: '光影', icon: <Sparkles />, type: 'shader' },
  { key: 'modpacks', label: '整合包', icon: <Boxes />, type: 'modpack' }
]

const LOADERS = [
  { key: 'fabric', name: 'Fabric', color: '#dbb69c', desc: '轻量、更新最快的模组加载器，支持绝大多数现代化模组。', tag: '推荐' },
  { key: 'quilt', name: 'Quilt', color: '#c9a0f0', desc: 'Fabric 的社区分支，兼容 Fabric 模组并扩展了 API。', tag: '' },
  { key: 'forge', name: 'Forge', color: '#8fa8c8', desc: '历史最悠久的模组加载器，拥有海量经典整合包。', tag: '' },
  { key: 'neoforge', name: 'NeoForge', color: '#f0a07c', desc: 'Forge 的现代化分支，适合 1.20.2 之后的版本。', tag: '新' }
]

export function DownloadPage() {
  const t = useT()
  const navigate = useStore((s) => s.navigate)
  const instances = useStore((s) => s.instances)
  const settings = useStore((s) => s.settings)
  const toast = useStore((s) => s.toast)
  const downloadTab = useStore((s) => s.downloadTab)
  const setDownloadView = useStore((s) => s.setDownloadView)
  const [tab, setTab] = useState<Tab>(TABS.some((item) => item.key === downloadTab) ? (downloadTab as Tab) : 'vanilla')
  const [contentSource, setContentSource] = useState<'modrinth' | 'curseforge'>(() => useStore.getState().downloadSourceTab)

  useEffect(() => {
    setDownloadView(tab, contentSource)
  }, [tab, contentSource, setDownloadView])

  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1>{t('download.title')}</h1>
          <p>{t('download.subtitle')}</p>
        </div>
        <div className="page-actions">
          <Badge tone="acc">
            <Zap size={11} /> Modrinth · CurseForge
          </Badge>
          <span style={{ color: 'var(--muted)', fontSize: 12 }}>
            镜像源：
            {settings?.downloadSource === 'official'
              ? '官方'
              : settings?.downloadSource === 'mirror'
                ? 'BMCLAPI'
                : settings?.downloadSource === 'prefer-official'
                  ? '官方优先'
                  : '自动'}
          </span>
        </div>
      </div>

      <div className="download-tabs">
        {TABS.map((item) => (
          <button key={item.key} className={cn('chip', tab === item.key && 'active')} onClick={() => setTab(item.key)} style={{ height: 34, padding: '0 14px', fontSize: 12.5 }}>
            {item.icon}
            {item.label}
          </button>
        ))}
      </div>

      {tab !== 'vanilla' && tab !== 'loaders' && (
        <div className='download-tabs' style={{ marginBottom: 12 }}>
          <button className={cn('chip', contentSource === 'modrinth' && 'active')} onClick={() => setContentSource('modrinth')}>
            Modrinth
          </button>
          <button className={cn('chip', contentSource === 'curseforge' && 'active')} onClick={() => setContentSource('curseforge')}>
            CurseForge
          </button>
        </div>
      )}

      {tab === 'vanilla' && <VanillaTab />}
      {tab === 'loaders' && <LoaderTab />}
      {tab !== 'vanilla' && tab !== 'loaders' &&
        (contentSource === 'modrinth' ? (
          <ModrinthTab key={tab} tab={tab} type={TABS.find((x) => x.key === tab)!.type!} />
        ) : (
          <CurseForgeTab key={tab} type={TABS.find((x) => x.key === tab)!.type!} />
        ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Vanilla
// ---------------------------------------------------------------------------

function suggestUniqueName(base: string, instances: { name: string }[]): string {
  const used = new Set(instances.map((inst) => inst.name.trim().toLowerCase()))
  const clean = base.trim() || '新实例'
  if (!used.has(clean.toLowerCase())) return clean
  for (let i = 2; i < 1000; i++) {
    const candidate = `${clean} (${i})`
    if (!used.has(candidate.toLowerCase())) return candidate
  }
  return `${clean} (${Date.now()})`
}

function VanillaTab() {
  const [manifest, setManifest] = useState<VersionManifest | null>(null)
  const [filter, setFilter] = useState<'release' | 'snapshot' | 'old' | 'all'>('release')
  const [query, setQuery] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [target, setTarget] = useState<McVersionSummary | null>(null)

  useEffect(() => {
    void api.versions
      .manifest()
      .then(setManifest)
      .catch((e) => setError(errorMessage(e)))
  }, [])

  const filtered = useMemo(() => {
    if (!manifest) return []
    let list = manifest.versions
    if (filter === 'release') list = list.filter((v) => v.type === 'release')
    else if (filter === 'snapshot') list = list.filter((v) => v.type === 'snapshot')
    else if (filter === 'old') list = list.filter((v) => v.type === 'old_beta' || v.type === 'old_alpha')
    if (query.trim()) list = list.filter((v) => v.id.toLowerCase().includes(query.trim().toLowerCase()))
    return list
  }, [manifest, filter, query])

  return (
    <>
      <div className="dl-filter">
        <div className="input-with-icon" style={{ flex: 1, maxWidth: 320 }}>
          <Search />
          <input className="input search" placeholder="搜索版本…" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <div style={{ display: 'flex', gap: 6 }}>
          {[
            { key: 'release', label: '正式版' },
            { key: 'snapshot', label: '快照' },
            { key: 'old', label: '远古版本' },
            { key: 'all', label: '全部' }
          ].map((f) => (
            <Chip key={f.key} active={filter === f.key} onClick={() => setFilter(f.key as never)}>
              {f.label}
            </Chip>
          ))}
        </div>
        {manifest && <span style={{ marginLeft: 'auto', color: 'var(--faint)', fontSize: 12 }}>{filtered.length} 个版本 · 最新正式版 {manifest.latest.release}</span>}
      </div>

      {error && <div className="card card-pad" style={{ color: 'var(--danger)', fontSize: 12.5 }}>加载版本列表失败：{error}</div>}

      <div className="version-list">
        {!manifest &&
          Array.from({ length: 8 }).map((_, i) => (
            <div className="version-row" key={i}>
              <Skeleton height={20} width={120} />
              <Skeleton height={14} width={160} style={{ marginLeft: 'auto' }} />
            </div>
          ))}
        {filtered.map((v) => {
          return (
            <div className="version-row" key={v.id}>
              <div className="version-id">
                {v.id}
                {v.id === manifest?.latest.release && <Badge tone="acc">最新</Badge>}
              </div>
              <div className="version-badges">
                <Badge tone={v.type === 'release' ? 'ok' : v.type === 'snapshot' ? 'warn' : undefined}>
                  {v.type === 'release' ? '正式版' : v.type === 'snapshot' ? '快照' : '远古'}
                </Badge>
              </div>
              <div className="version-date">
                <CalendarDays size={12} style={{ marginRight: 5, verticalAlign: -2 }} />
                {formatDate(v.releaseTime)}
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Button size="sm" variant="primary" icon={<Plus size={14} />} onClick={() => setTarget(v)}>
                  新建实例
                </Button>
              </div>
            </div>
          )
        })}
        {manifest && filtered.length === 0 && <EmptyState icon={<Search />} title="没有找到匹配的版本" />}
      </div>

      <InstallVersionModal target={target} onClose={() => setTarget(null)} />
    </>
  )
}

type LoaderChoice = 'vanilla' | 'fabric' | 'quilt' | 'forge' | 'neoforge'

function InstallVersionModal({ target, onClose }: { target: McVersionSummary | null; onClose: () => void }) {
  const toast = useStore((s) => s.toast)
  const instances = useStore((s) => s.instances)
  const refreshInstances = useStore((s) => s.refreshInstances)
  const [loader, setLoader] = useState<LoaderChoice>('vanilla')
  const [loaderVersions, setLoaderVersions] = useState<LoaderVersion[] | null>(null)
  const [loaderVersion, setLoaderVersion] = useState('')
  const [name, setName] = useState('')
  const [nameTouched, setNameTouched] = useState(false)
  const [installing, setInstalling] = useState(false)

  useEffect(() => {
    if (!target) return
    setLoader('vanilla')
    setLoaderVersions(null)
    setLoaderVersion('')
    setName(target.id)
    setNameTouched(false)
  }, [target?.id])

  useEffect(() => {
    if (!target) return
    if (loader === 'vanilla') {
      setLoaderVersions(null)
      setLoaderVersion('')
      if (!nameTouched) setName(target.id)
      return
    }
    let alive = true
    setLoaderVersions(null)
    setLoaderVersion('')
    if (!nameTouched) setName(`${target.id} ${LOADER_LABEL[loader] ?? loader}`)
    void api.versions
      .loaders(loader, target.id)
      .then((list) => {
        if (!alive) return
        setLoaderVersions(list)
        if (!list.length) toast('warning', `没有找到兼容 ${target.id} 的 ${LOADER_LABEL[loader]} 版本`)
        setLoaderVersion(list.find((v) => v.stable)?.version ?? list[0]?.version ?? '')
      })
      .catch((e) => {
        if (!alive) return
        setLoaderVersions([])
        toast('error', errorMessage(e))
      })
    return () => {
      alive = false
    }
  }, [loader, target?.id])

  const duplicate = !!name.trim() && instances.some((i) => i.name.trim().toLowerCase() === name.trim().toLowerCase())
  const suggestion = duplicate ? suggestUniqueName(name, instances) : ''
  const canInstall = !!target && !!name.trim() && !duplicate && !installing && (loader === 'vanilla' || !!loaderVersion)

  const install = async (): Promise<void> => {
    if (!target || !canInstall) return
    setInstalling(true)
    onClose()
    try {
      if (loader === 'vanilla') {
        await api.install.vanilla(target.id, true, name.trim())
      } else {
        await api.install.loader({
          loader,
          mcVersion: target.id,
          loaderVersion,
          instanceName: name.trim(),
          createInstance: true
        })
      }
      await refreshInstances()
      toast('success', `已创建实例「${name.trim()}」`)
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setInstalling(false)
    }
  }

  return (
    <Modal
      open={!!target}
      onClose={onClose}
      title={`安装 Minecraft ${target?.id ?? ''}`}
      subtitle="可选择同时安装模组加载器，并为新实例命名"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" icon={<DownloadIcon size={15} />} loading={installing} disabled={!canInstall} onClick={() => void install()}>
            开始安装
          </Button>
        </>
      }
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16, padding: 12, borderRadius: 12, background: 'var(--panel)', border: '1px solid var(--line)' }}>
        <div style={{ fontSize: 22 }}>🧊</div>
        <div style={{ flex: 1 }}>
          <div style={{ fontWeight: 700, fontSize: 15 }}>Minecraft {target?.id}</div>
          <div style={{ color: 'var(--muted)', fontSize: 11.5 }}>{target ? formatDate(target.releaseTime) : ''} · {target?.type === 'release' ? '正式版' : target?.type === 'snapshot' ? '快照' : '远古版本'}</div>
        </div>

      </div>

      <Field label="模组加载器" hint="选择「原版」安装纯净游戏，或同时安装加载器（会自动补齐原版文件）">
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Chip active={loader === 'vanilla'} onClick={() => setLoader('vanilla')}>
            原版
          </Chip>
          {LOADERS.map((item) => (
            <Chip key={item.key} active={loader === item.key} onClick={() => setLoader(item.key as LoaderChoice)}>
              {item.name}
            </Chip>
          ))}
        </div>
      </Field>

      {loader !== 'vanilla' && (
        <Field label={`${LOADER_LABEL[loader] ?? loader} 版本`} hint={loaderVersions?.length ? '推荐使用最新的稳定版本' : undefined}>
          <Select value={loaderVersion} onChange={(e) => setLoaderVersion(e.target.value)} disabled={loaderVersions === null || loaderVersions.length === 0}>
            {loaderVersions === null && <option>正在获取版本列表…</option>}
            {loaderVersions?.length === 0 && <option>没有兼容版本</option>}
            {loaderVersions?.map((v) => (
              <option key={v.version} value={v.version}>
                {v.version} {v.stable ? '' : '（测试版）'}
              </option>
            ))}
          </Select>
        </Field>
      )}

      <Field label="实例名称" hint={duplicate ? '该名称已存在，请修改后再安装' : '安装完成后会出现在实例列表中'}>
        <Input
          value={name}
          onChange={(e) => {
            setNameTouched(true)
            setName(e.target.value)
          }}
          placeholder="例如：生存 1.21.1"
        />
      </Field>
      {duplicate && (
        <div style={{ marginTop: -6, marginBottom: 12, display: 'flex', alignItems: 'center', gap: 8, color: 'var(--warning)', fontSize: 11.5 }}>
          名称已存在
          <button style={{ color: 'var(--acc1)' }} onClick={() => { setName(suggestion); setNameTouched(true) }}>
            使用「{suggestion}」
          </button>
        </div>
      )}

      <div style={{ padding: 12, borderRadius: 12, background: 'var(--panel)', border: '1px solid var(--line)', fontSize: 11.5, color: 'var(--muted)', lineHeight: 1.6 }}>
        {loader === 'vanilla'
          ? '只安装原版游戏文件，并创建独立实例。'
          : `将先确保 Minecraft ${target?.id} 原版文件完整，再安装 ${LOADER_LABEL[loader]} ${loaderVersion}。Forge / NeoForge 首次安装可能需要几分钟。`}
      </div>
    </Modal>
  )
}

// ---------------------------------------------------------------------------
// Loaders
// ---------------------------------------------------------------------------

function LoaderTab() {
  const toast = useStore((s) => s.toast)
  const instances = useStore((s) => s.instances)
  const [manifest, setManifest] = useState<VersionManifest | null>(null)
  const [mcVersion, setMcVersion] = useState('')
  const [active, setActive] = useState<(typeof LOADERS)[number] | null>(null)

  useEffect(() => {
    void api.versions.manifest().then((m) => {
      setManifest(m)
      setMcVersion(instances[0]?.mcVersion || m.latest.release)
    })
  }, [])

  return (
    <>
      <div className="dl-filter">
        <span style={{ fontSize: 12.5, color: 'var(--muted)' }}>目标游戏版本</span>
        <Select value={mcVersion} onChange={(e) => setMcVersion(e.target.value)} style={{ maxWidth: 220 }}>
          {instances.map((i) => (
            <option key={`inst-${i.id}`} value={i.mcVersion}>
              {i.mcVersion}
            </option>
          ))}
          {manifest?.versions
            .filter((v) => v.type === 'release')
            .slice(0, 60)
            .map((v) => (
              <option key={v.id} value={v.id}>
                {v.id}
              </option>
            ))}
        </Select>
        <span style={{ color: 'var(--faint)', fontSize: 11.5 }}>安装加载器时会自动补齐对应原版文件</span>
      </div>

      <div className="loader-grid">
        {LOADERS.map((loader) => (
          <div className="loader-card" key={loader.key}>
            <LoaderMark loader={loader.key} className="loader-card-icon" />
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <div className="loader-name">{loader.name}</div>
              {loader.tag && <Badge tone="acc">{loader.tag}</Badge>}
            </div>
            <div className="loader-desc">{loader.desc}</div>
            <Button variant="soft" icon={<DownloadIcon size={15} />} onClick={() => setActive(loader)} disabled={!mcVersion}>
              安装到 {mcVersion}
            </Button>
          </div>
        ))}
      </div>

      <InstallLoaderModal
        loader={active}
        mcVersion={mcVersion}
        onClose={() => setActive(null)}
      />
    </>
  )
}

function InstallLoaderModal({
  loader,
  mcVersion,
  onClose
}: {
  loader: (typeof LOADERS)[number] | null
  mcVersion: string
  onClose: () => void
}) {
  const toast = useStore((s) => s.toast)
  const navigate = useStore((s) => s.navigate)
  const instances = useStore((s) => s.instances)
  const [versions, setVersions] = useState<LoaderVersion[] | null>(null)
  const [loaderVersion, setLoaderVersion] = useState('')
  const [name, setName] = useState('')
  const [createInstance, setCreateInstance] = useState(true)
  const [loading, setLoading] = useState(false)
  const duplicate = createInstance && !!name.trim() && instances.some((i) => i.name.trim().toLowerCase() === name.trim().toLowerCase())
  const suggestion = duplicate ? suggestUniqueName(name, instances) : ''
  const canInstall = !!loaderVersion && !duplicate && (!createInstance || !!name.trim())

  useEffect(() => {
    if (!loader) return
    setVersions(null)
    setName(`${mcVersion} ${loader.name}`)
    void api.versions
      .loaders(loader.key as never, mcVersion)
      .then((list) => {
        setVersions(list)
        setLoaderVersion(list[0]?.version ?? '')
      })
      .catch((e) => {
        setVersions([])
        toast('error', errorMessage(e))
      })
  }, [loader, mcVersion])

  const install = async (): Promise<void> => {
    if (!loader || !canInstall) return
    setLoading(true)
    onClose()
    try {
      const result = await api.install.loader({
        loader: loader.key as never,
        mcVersion,
        loaderVersion: loaderVersion || undefined,
        instanceName: name,
        createInstance
      })
      toast('success', `${loader.name} ${loaderVersion} 安装完成`)
      if (result.instanceId) navigate({ name: 'instance', id: result.instanceId })
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  return (
    <Modal
      open={!!loader}
      onClose={onClose}
      title={`安装 ${loader?.name ?? ''}`}
      subtitle={`Minecraft ${mcVersion}`}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" loading={loading} disabled={!canInstall} onClick={() => void install()}>
            开始安装
          </Button>
        </>
      }
    >
      <Field label="加载器版本" hint="列表来自官方元数据，推荐使用最新的稳定版">
        <Select value={loaderVersion} onChange={(e) => setLoaderVersion(e.target.value)} disabled={versions === null}>
          {versions === null && <option>正在获取版本列表…</option>}
          {versions?.length === 0 && <option>没有可用版本</option>}
          {versions?.map((v) => (
            <option key={v.version} value={v.version}>
              {v.version} {v.stable ? '' : '（测试版）'}
            </option>
          ))}
        </Select>
      </Field>
      {createInstance && (
        <Field label="实例名称" hint={duplicate ? '该名称已存在，请修改后再安装' : '安装完成后会出现在实例列表中'}>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="安装后创建的实例名称" />
        </Field>
      )}
      {duplicate && (
        <div style={{ marginTop: -6, marginBottom: 12, display: 'flex', alignItems: 'center', gap: 8, color: 'var(--warning)', fontSize: 11.5 }}>
          名称已存在
          <button style={{ color: 'var(--acc1)' }} onClick={() => setName(suggestion)}>
            使用「{suggestion}」
          </button>
        </div>
      )}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <Switch checked={createInstance} onChange={setCreateInstance} />
        <span style={{ fontSize: 12.5, color: 'var(--text-soft)' }}>安装完成后创建新实例</span>
      </div>
      <div style={{ marginTop: 14, padding: 12, borderRadius: 12, background: 'var(--panel)', border: '1px solid var(--line)', fontSize: 11.5, color: 'var(--muted)', lineHeight: 1.6 }}>
        安装过程会先确保 Minecraft {mcVersion} 的原版文件完整，然后下载加载器依赖。首次安装可能需要几分钟。
      </div>
    </Modal>
  )
}

// ---------------------------------------------------------------------------
// Modrinth
// ---------------------------------------------------------------------------

const MODRINTH_PAGE_SIZE = 40

function ModrinthTab({ tab, type }: { tab: Tab; type: ContentFilterKind }) {
  const navigate = useStore((s) => s.navigate)
  const instances = useStore((s) => s.instances)
  const settings = useStore((s) => s.settings)
  const defaultInstance = instances.find((i) => i.id === settings?.lastSelectedInstanceId) ?? instances[0]
  const [query, setQuery] = useState('')
  const [mcVersion, setMcVersion] = useState('')
  const [loader, setLoader] = useState('')
  const [sort, setSort] = useState<'relevance' | 'downloads' | 'follows' | 'newest' | 'updated'>('downloads')
  const [hits, setHits] = useState<ModrinthHit[] | null>(null)
  const [total, setTotal] = useState(0)
  const [offset, setOffset] = useState(0)
  const [loading, setLoading] = useState(false)
  const [featured, setFeatured] = useState<ModrinthHit[]>([])
  const [versionOptions, setVersionOptions] = useState<string[]>([])
    const searchSeq = useRef(0)
  const instanceVersionKey = instances.map((instance) => instance.mcVersion).filter(Boolean).join('|')

  // Populate the version filter from installed instances plus official releases.
  useEffect(() => {
    void api.versions
      .manifest()
      .then((manifest) => {
        const values = new Set<string>()
        for (const instance of instances) if (instance.mcVersion) values.add(instance.mcVersion)
        for (const version of manifest.versions) {
          if (version.type === 'release' || version.type === 'snapshot') values.add(version.id)
          if (values.size >= 160) break
        }
        setVersionOptions([...values])
      })
      .catch(() => {})
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [instanceVersionKey])

  useEffect(() => {
    void api.modrinth
      .featured(type)
      .then((list) => setFeatured(list))
      .catch(() => {})
  }, [type])

  useEffect(() => {
    const seq = ++searchSeq.current
    setLoading(true)
    setHits(null)
    setOffset(0)
    const timer = setTimeout(() => {
      void api.modrinth
        .search({
          query: query.trim() || undefined,
          projectType: type,
          mcVersion: mcVersion || undefined,
          loader: loader || undefined,
          index: query.trim() ? 'relevance' : sort,
          limit: MODRINTH_PAGE_SIZE,
          offset: 0
        })
        .then((res) => {
          if (seq !== searchSeq.current) return
          setHits(res.hits)
          setTotal(res.total_hits)
          setOffset(res.hits.length)
        })
        .catch(() => {
          if (seq === searchSeq.current) {
            setHits([])
            setTotal(0)
          }
        })
        .finally(() => {
          if (seq === searchSeq.current) setLoading(false)
        })
    }, 300)
    return () => clearTimeout(timer)
  }, [query, type, mcVersion, loader, sort])

  const loadMore = async (): Promise<void> => {
    if (loading) return
    const seq = searchSeq.current
    setLoading(true)
    try {
      const res = await api.modrinth.search({
        query: query.trim() || undefined,
        projectType: type,
        mcVersion: mcVersion || undefined,
        loader: loader || undefined,
        index: query.trim() ? 'relevance' : sort,
        limit: MODRINTH_PAGE_SIZE,
        offset
      })
      if (seq !== searchSeq.current) return
      setHits((prev) => {
        const merged = new Map((prev ?? []).map((hit) => [hit.project_id, hit]))
        for (const hit of res.hits) merged.set(hit.project_id, hit)
        return [...merged.values()]
      })
      setOffset((current) => current + res.hits.length)
      setTotal(res.total_hits)
    } catch {
      // Keep the already loaded results.
    } finally {
      if (seq === searchSeq.current) setLoading(false)
    }
  }

  return (
    <>
      <div className="dl-filter">
        <div className="input-with-icon" style={{ flex: 1, maxWidth: 380 }}>
          <Search />
          <input
            className="input search"
            placeholder={`搜索${TABS.find((x) => x.key === tab)?.label}（英文关键词更容易命中，如 sodium、jei）…`}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>
        <Select value={mcVersion} onChange={(e) => setMcVersion(e.target.value)} style={{ maxWidth: 160 }}>
          <option value="">全部版本</option>
          {versionOptions.map((version) => (
            <option key={version} value={version}>
              {version}
            </option>
          ))}
        </Select>
        {(type === 'mod' || type === 'modpack') && (
          <Select value={loader} onChange={(e) => setLoader(e.target.value)} style={{ maxWidth: 150 }}>
            <option value="">全部加载器</option>
            <option value="fabric">Fabric</option>
            <option value="quilt">Quilt</option>
            <option value="forge">Forge</option>
            <option value="neoforge">NeoForge</option>
          </Select>
        )}
        <Select value={sort} onChange={(e) => setSort(e.target.value as never)} style={{ maxWidth: 150 }}>
          <option value="downloads">下载量</option>
          <option value="follows">关注数</option>
          <option value="relevance">相关度</option>
          <option value="newest">最新发布</option>
          <option value="updated">最近更新</option>
        </Select>
        {defaultInstance && (
          <Chip
            active={mcVersion === defaultInstance.mcVersion && ((type !== 'mod' && type !== 'modpack') || loader === defaultInstance.loader)}
            onClick={() => {
              setMcVersion(defaultInstance.mcVersion)
              if (type === 'mod' || type === 'modpack') {
                setLoader(['fabric', 'quilt', 'forge', 'neoforge'].includes(defaultInstance.loader) ? defaultInstance.loader : '')
              }
            }}
          >
            兼容当前实例
          </Chip>
        )}
        {(mcVersion || loader) && (
          <button
            className="chip"
            onClick={() => {
              setMcVersion('')
              setLoader('')
            }}
          >
            清除筛选
          </button>
        )}
      </div>

      <div style={{ margin: '-6px 4px 14px', color: 'var(--faint)', fontSize: 11.5 }}>
        {!mcVersion && !loader
          ? '正在搜索全部 Modrinth 资源；可按版本 / 加载器筛选，或点击「兼容当前实例」。'
          : `筛选：${mcVersion || '全部版本'} · ${loader ? LOADER_LABEL[loader] ?? loader : '全部加载器'}（安装时会自动选择兼容目标实例的版本）`}
        {instances.length === 0 && <span style={{ color: 'var(--warning)' }}> · 尚未创建实例，安装前请先安装游戏版本</span>}
      </div>

      {!query.trim() && !mcVersion && !loader && featured.length > 0 && (
        <>
          <div className="section-head" style={{ marginTop: 4 }}>
            <h2>热门推荐</h2>
            <span style={{ color: 'var(--faint)', fontSize: 11.5 }}>按关注数排序</span>
          </div>
          <div className="result-grid" style={{ marginBottom: 26 }}>
            {featured.slice(0, 4).map((hit) => (
              <ResultCard key={hit.project_id} hit={hit} nameTranslation={hit.nameTranslation} onClick={() => navigate({ name: 'project', id: hit.project_id, extra: type, from: useStore.getState().page })} />
            ))}
          </div>
        </>
      )}

      <div className="section-head" style={{ marginTop: 4 }}>
        <h2>{query.trim() ? '搜索结果' : '浏览全部'}</h2>
        {hits && (
          <span style={{ color: 'var(--faint)', fontSize: 11.5 }}>
            已显示 {formatNumber(hits.length)} / 共 {formatNumber(total)} 个结果
          </span>
        )}
      </div>

      {loading && hits === null && (
        <div className="result-grid">
          {Array.from({ length: 8 }).map((_, i) => (
            <div className="result-card" key={i}>
              <Skeleton height={56} width={56} />
              <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 8 }}>
                <Skeleton height={16} width="60%" />
                <Skeleton height={12} />
                <Skeleton height={12} width="80%" />
              </div>
            </div>
          ))}
        </div>
      )}

      {hits && hits.length === 0 && !loading && (
        <div className="card">
          <EmptyState
            icon={<Search />}
            title="没有找到匹配的内容"
            description={mcVersion || loader ? '当前筛选条件下没有结果，试试清除筛选或更换版本。' : '尝试更换关键词；Modrinth 主要使用英文标题与描述。'}
          />
        </div>
      )}

      <div className="result-grid">
        {hits?.map((hit) => (
          <ResultCard key={hit.project_id} hit={hit} onClick={() => navigate({ name: 'project', id: hit.project_id, extra: type, from: useStore.getState().page })} />
        ))}
      </div>

      {hits && hits.length > 0 && offset < total && (
        <div style={{ display: 'flex', justifyContent: 'center', marginTop: 20 }}>
          <Button loading={loading} onClick={() => void loadMore()}>
            {loading ? '加载中…' : `加载更多（已显示 ${formatNumber(hits.length)} / 共 ${formatNumber(total)}）`}
          </Button>
        </div>
      )}
    </>
  )
}

function ResultCard({ hit, nameTranslation, onClick }: { hit: ModrinthHit; nameTranslation?: string; onClick: () => void }) {
  return (
    <div className="result-card" onClick={onClick}>
      <div className="result-icon">{hit.icon_url ? <img src={hit.icon_url} alt="" /> : hit.title.slice(0, 1).toUpperCase()}</div>
      <div className="result-body">
        <div className="result-title">
          {hit.title}
          {nameTranslation && <span style={{ color: 'var(--faint)', fontWeight: 500 }}>（{nameTranslation}）</span>}
          {hit.project_type === 'mod' && <Badge tone="acc">模组</Badge>}
        </div>
        <div className="result-desc">{hit.description}</div>
        <div className="result-tags">
          {hit.categories.slice(0, 4).map((c) => (
            <span className="tag" key={c}>
              {c}
            </span>
          ))}
        </div>
        <div className="result-stats">
          <span>
            <DownloadIcon size={12} /> {formatNumber(hit.downloads)}
          </span>
          <span>
            <Star size={12} /> {formatNumber(hit.follows)}
          </span>
          <span>{hit.author}</span>
        </div>
      </div>
    </div>
  )
}

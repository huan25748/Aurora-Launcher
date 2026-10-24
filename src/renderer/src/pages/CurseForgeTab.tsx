import { useEffect, useMemo, useRef, useState } from 'react'
import { Download as DownloadIcon, FolderOpen, Search, UserRound } from 'lucide-react'
import type {
  ContentFilterKind,
  CurseForgeCategory,
  CurseForgeFile,
  CurseForgeHit,
  CurseForgeMissingMod,
  CurseForgeProject,
  Instance,
  McVersionSummary
} from '@shared/types'
import { api, errorMessage, formatDate, formatNumber, LOADER_LABEL } from '../api'
import { useStore } from '../store'
import { Badge, Button, EmptyState, Input, Modal, Select, Skeleton } from '../components/ui'

const PAGE_SIZE = 20

const TYPE_LABEL: Record<ContentFilterKind, string> = {
  mod: '模组',
  resourcepack: '资源包',
  shader: '光影',
  modpack: '整合包',
  datapack: '数据包'
}

export function CurseForgeTab({ type }: { type: ContentFilterKind }) {
  const instances = useStore((s) => s.instances)
  const settings = useStore((s) => s.settings)
  const refreshInstances = useStore((s) => s.refreshInstances)
  const toast = useStore((s) => s.toast)
  const defaultInstance = instances.find((i) => i.id === settings?.lastSelectedInstanceId) ?? instances[0]

  const [query, setQuery] = useState('')
  const [mcVersion, setMcVersion] = useState('')
  const [loader, setLoader] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [categories, setCategories] = useState<CurseForgeCategory[]>([])
    const [sort, setSort] = useState<'relevance' | 'downloads' | 'newest' | 'updated'>('downloads')
  const [hits, setHits] = useState<CurseForgeHit[] | null>(null)
  const [total, setTotal] = useState(0)
  const [offset, setOffset] = useState(0)
  const [loading, setLoading] = useState(false)
  const [versionOptions, setVersionOptions] = useState<string[]>([])
  const [selected, setSelected] = useState<CurseForgeHit | null>(null)
  const [project, setProject] = useState<CurseForgeProject | null>(null)
  const [files, setFiles] = useState<CurseForgeFile[] | null>(null)
  const [fileId, setFileId] = useState<number | null>(null)
  const [targetInstanceId, setTargetInstanceId] = useState(defaultInstance?.id ?? '')
  const [instanceName, setInstanceName] = useState('')
  const [installing, setInstalling] = useState(false)
  const [folderOpen, setFolderOpen] = useState(false)
  const [folderFiles, setFolderFiles] = useState<CurseForgeFile[] | null>(null)
  const [folderVersion, setFolderVersion] = useState('')
  const [folderFileId, setFolderFileId] = useState<number | null>(null)
  const [missingMods, setMissingMods] = useState<CurseForgeMissingMod[]>([])
  const searchSeq = useRef(0)

  const targetInstance: Instance | undefined = instances.find((i) => i.id === targetInstanceId) ?? defaultInstance
  const showLoader = type === 'mod' || type === 'modpack'

  useEffect(() => {
    setCategoryId('')
    void api.curseforge
      .categories(type)
      .then(setCategories)
      .catch(() => setCategories([]))
  }, [type])

  useEffect(() => {
    void api.versions
      .manifest()
      .then((manifest: { versions: McVersionSummary[] }) => {
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
  }, [instances.map((i) => i.mcVersion).join('|')])

  useEffect(() => {
    const seq = ++searchSeq.current
    setLoading(true)
    setHits(null)
    setOffset(0)
    const timer = setTimeout(() => {
      void api.curseforge
        .search({
          query: query.trim() || undefined,
          projectType: type,
          categoryId: categoryId ? Number(categoryId) : undefined,
          mcVersion: mcVersion || undefined,
          loader: loader || undefined,
          sort,
          offset: 0,
          limit: PAGE_SIZE
        })
        .then((res) => {
          if (seq !== searchSeq.current) return
          setHits(res.hits)
          setTotal(res.total)
          setOffset(res.hits.length)
        })
        .catch((e) => {
          if (seq === searchSeq.current) {
            setHits([])
            setTotal(0)
            toast('error', errorMessage(e), 'CurseForge 搜索失败')
          }
        })
        .finally(() => {
          if (seq === searchSeq.current) setLoading(false)
        })
    }, 300)
    return () => clearTimeout(timer)
  }, [query, type, categoryId, mcVersion, loader, sort])

  const loadMore = async (): Promise<void> => {
    if (loading) return
    setLoading(true)
    try {
      const res = await api.curseforge.search({
        query: query.trim() || undefined,
        projectType: type,
        categoryId: categoryId ? Number(categoryId) : undefined,
        mcVersion: mcVersion || undefined,
        loader: loader || undefined,
        sort,
        offset,
        limit: PAGE_SIZE
      })
      setHits((prev) => {
        const merged = new Map((prev ?? []).map((hit) => [hit.id, hit]))
        for (const hit of res.hits) merged.set(hit.id, hit)
        return [...merged.values()]
      })
      setOffset((current) => current + res.hits.length)
      setTotal(res.total)
    } catch {
      // Keep already loaded results.
    } finally {
      setLoading(false)
    }
  }

  const openProject = async (hit: CurseForgeHit): Promise<void> => {
    setSelected(hit)
    setProject(null)
    setFiles(null)
    setFileId(null)
    setInstanceName('')
    setTargetInstanceId(defaultInstance?.id ?? '')
    void api.curseforge
      .project(hit.id)
      .then((value) => setProject(value))
      .catch((e) => toast('error', errorMessage(e), '获取项目详情失败'))
  }

  useEffect(() => {
    if (!selected) return
    let alive = true
    const instance = instances.find((i) => i.id === targetInstanceId) ?? defaultInstance
    setFiles(null)
    void api.curseforge
      .files(
        selected.id,
        type === 'modpack' ? mcVersion || undefined : instance?.mcVersion || mcVersion || undefined,
        type === 'mod' ? instance?.loader || loader || undefined : type === 'modpack' ? loader || undefined : undefined
      )
      .then((list) => {
        if (!alive) return
        setFiles(list)
        setFileId((current) => (current && list.some((f) => f.id === current) ? current : list[0]?.id ?? null))
      })
      .catch((e) => {
        if (!alive) return
        setFiles([])
        toast('error', errorMessage(e), '获取文件列表失败')
      })
    return () => {
      alive = false
    }
  }, [selected?.id, targetInstanceId, type, loader, mcVersion])

  const selectedFile = files?.find((file) => file.id === fileId) ?? null

  const folderVersions = useMemo(() => {
    const values = new Set<string>()
    for (const file of folderFiles ?? []) {
      for (const gameVersion of file.gameVersions ?? []) {
        if (/^\d+\.\d+(\.\d+)?$/.test(gameVersion)) values.add(gameVersion)
      }
    }
    return [...values].sort((a, b) => b.localeCompare(a, undefined, { numeric: true }))
  }, [folderFiles])

  const folderFilesForGame = useMemo(
    () => (folderFiles ?? []).filter((file) => (file.gameVersions ?? []).includes(folderVersion)),
    [folderFiles, folderVersion]
  )
  const folderFile = folderFilesForGame.find((file) => file.id === folderFileId) ?? folderFilesForGame[0] ?? null

  const openFolderInstall = (): void => {
    if (!selected) return
    setFolderOpen(true)
    setFolderFiles(null)
    setFolderVersion('')
    void api.curseforge
      .files(selected.id)
      .then((list) => {
        setFolderFiles(list)
        const first = list.flatMap((file) => file.gameVersions ?? []).find((version) => /^\d+\.\d+(\.\d+)?$/.test(version))
        setFolderVersion(first ?? '')
      })
      .catch((e) => {
        toast('error', errorMessage(e), '获取游戏版本失败')
        setFolderOpen(false)
      })
  }

  const installToFolder = async (): Promise<void> => {
    if (!selected || !folderFile) return
    const picked = await api.app.pickDirectory('选择安装文件夹', targetInstance?.gameDir)
    if (picked.canceled || !picked.paths[0]) return
    setInstalling(true)
    setFolderOpen(false)
    setSelected(null)
    try {
      const result = await api.curseforge.installToFolder({ modId: selected.id, fileId: folderFile.id, destDir: picked.paths[0] })
      toast('success', `已安装到文件夹：${result.installed.join(', ')}`)
    } catch (e) {
      toast('error', errorMessage(e), '安装到文件夹失败')
    } finally {
      setInstalling(false)
    }
  }

  const install = async (): Promise<void> => {
    if (!selected || !fileId) return
    setInstalling(true)
    setSelected(null)
    try {
      if (type === 'modpack') {
        const result = await api.curseforge.installPack({
          modId: selected.id,
          fileId,
          instanceName: instanceName.trim() || undefined
        })
        await refreshInstances()
        if (result.missing.length) setMissingMods(result.missing)
        else toast('success', '整合包安装完成，已创建实例')
        return
      }
      if (!targetInstance) {
        toast('warning', '请先创建一个实例')
        return
      }
      const result = await api.curseforge.install({
        instanceId: targetInstance.id,
        modId: selected.id,
        fileId
      })
      await refreshInstances()
      toast('success', `已安装 ${result.installed.length} 个文件${result.skipped.length ? `，跳过 ${result.skipped.length} 个` : ''}`)
    } catch (e) {
      toast('error', errorMessage(e), '安装失败')
    } finally {
      setInstalling(false)
    }
  }

  const clearFilters = (): void => {
    setMcVersion('')
    setLoader('')
    setCategoryId('')
  }

  return (
    <>
      <div className="dl-filter">
        <div className="input-with-icon" style={{ flex: 1, maxWidth: 380 }}>
          <Search />
          <input
            className="input search"
            placeholder={`搜索 CurseForge ${TYPE_LABEL[type]}（英文关键词更容易命中）…`}
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
        {showLoader && (
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
          <option value="relevance">相关度</option>
          <option value="newest">最新发布</option>
          <option value="updated">最近更新</option>
        </Select>
        <Select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} style={{ maxWidth: 180 }}>
          <option value=''>全部分类</option>
          {categories.map((category) => (
            <option key={category.id} value={category.id}>
              {category.name}
            </option>
          ))}
        </Select>
        {(mcVersion || loader || categoryId) && (
          <button className='chip' onClick={clearFilters}>
            清除筛选
          </button>
        )}
      </div>

      <div style={{ margin: '-6px 4px 14px', color: 'var(--faint)', fontSize: 11.5 }}>
        数据来自 CurseForge，通过 MCIM 代理获取；文件下载使用 CurseForge 官方地址。CurseForge API 单次查询最多返回 1 万条，请用关键词 / 版本 / 加载器 / 分类缩小范围。
        {instances.length === 0 && <span style={{ color: 'var(--warning)' }}> · 尚未创建实例，安装模组前请先安装游戏版本</span>}
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
            description="CurseForge 搜索主要使用英文标题；也可以先清除筛选。"
          />
        </div>
      )}

      <div className="result-grid">
        {hits?.map((hit) => (
          <CurseForgeCard key={hit.id} hit={hit} nameTranslation={hit.nameTranslation} onClick={() => void openProject(hit)} />
        ))}
      </div>

      {hits && hits.length > 0 && (
        <div className="section-head" style={{ marginTop: 16 }}>
          <span style={{ color: 'var(--faint)', fontSize: 11.5 }}>
            已显示 {formatNumber(hits.length)} / {total >= 10000 ? '1 万+' : formatNumber(total)} 个结果
          </span>
          {offset < total && (
            <Button loading={loading} onClick={() => void loadMore()} size="sm">
              加载更多
            </Button>
          )}
        </div>
      )}

      <Modal
        open={!!selected}
        onClose={() => !installing && setSelected(null)}
        title={project?.name ?? selected?.name}
        subtitle={project ? `${TYPE_LABEL[type]} · ${project.authors.join(', ') || '未知作者'}` : '加载中…'}
        wide
        footer={
          <>
            <Button variant="ghost" onClick={() => setSelected(null)} disabled={installing}>
              取消
            </Button>
            {type !== 'modpack' && (
              <Button variant='soft' icon={<FolderOpen size={15} />} disabled={!selected || installing} onClick={openFolderInstall}>
                安装到文件夹
              </Button>
            )}
            <Button
              variant="primary"
              loading={installing}
              disabled={!fileId || (type !== 'modpack' && !targetInstance)}
              onClick={() => void install()}
            >
              {type === 'modpack' ? '安装并创建实例' : '安装到实例'}
            </Button>
          </>
        }
      >
        {project ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div style={{ display: 'flex', gap: 14, alignItems: 'flex-start' }}>
              {project.logo && <img src={project.logo} alt="" style={{ width: 64, height: 64, borderRadius: 16, objectFit: 'cover' }} />}
              <div style={{ minWidth: 0 }}>
                <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 8 }}>
                  {project.categories.slice(0, 5).map((category) => (
                    <span className="tag" key={category}>
                      {category}
                    </span>
                  ))}
                </div>
                <div style={{ display: 'flex', gap: 14, color: 'var(--muted)', fontSize: 11.5, flexWrap: 'wrap' }}>
                  <span>
                    <DownloadIcon size={12} /> {formatNumber(project.downloads)}
                  </span>
                  <span>
                    <UserRound size={12} /> {project.authors.join(', ') || '未知作者'}
                  </span>
                  <span>更新于 {formatDate(project.dateModified)}</span>
                </div>
              </div>
            </div>
            <div style={{ color: 'var(--text-soft)', fontSize: 12.5, lineHeight: 1.8, whiteSpace: 'pre-wrap', maxHeight: 180, overflow: 'auto' }}>
              {project.description || project.summary}
            </div>

            {type === 'modpack' ? (
              <label className="field">
                <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>新实例名称</span>
                <Input value={instanceName} onChange={(e) => setInstanceName(e.target.value)} placeholder={project.name} />
              </label>
            ) : (
              <label className="field">
                <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>目标实例</span>
                <Select value={targetInstanceId} onChange={(e) => setTargetInstanceId(e.target.value)}>
                  {instances.map((instance) => (
                    <option key={instance.id} value={instance.id}>
                      {instance.name} · {instance.mcVersion} {LOADER_LABEL[instance.loader] ?? instance.loader}
                    </option>
                  ))}
                </Select>
              </label>
            )}

            <label className="field">
              <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>文件版本</span>
              <Select value={fileId ?? ''} onChange={(e) => setFileId(Number(e.target.value))}>
                {!files && <option value="">加载中…</option>}
                {files?.length === 0 && <option value="">没有兼容当前实例的版本</option>}
                {files?.map((file) => (
                  <option key={file.id} value={file.id}>
                    {file.displayName || file.fileName} · {file.releaseType === 1 ? '正式版' : file.releaseType === 2 ? 'Beta' : 'Alpha'}
                  </option>
                ))}
              </Select>
            </label>
            {selectedFile && (
              <div style={{ fontSize: 11.5, color: 'var(--faint)' }}>
                {selectedFile.fileName} · {selectedFile.fileLength ? `${(selectedFile.fileLength / 1024 / 1024).toFixed(1)} MB` : '大小未知'}
              </div>
            )}
            {selectedFile && !selectedFile.downloadUrl && (
              <div style={{ fontSize: 11.5, color: 'var(--warning)' }}>
                该文件没有直接下载地址；如果作者禁止第三方分发，安装会失败。
              </div>
            )}
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Skeleton height={18} width="50%" />
            <Skeleton height={100} />
          </div>
        )}
      </Modal>

      <Modal
        open={folderOpen}
        onClose={() => !installing && setFolderOpen(false)}
        title="选择游戏版本"
        subtitle="列出 CurseForge 项目支持的全部 MC 版本，不受实例筛选影响"
        footer={
          <>
            <Button variant="ghost" onClick={() => setFolderOpen(false)} disabled={installing}>
              取消
            </Button>
            <Button variant="primary" icon={<FolderOpen size={15} />} disabled={!folderFile} onClick={() => void installToFolder()}>
              选择文件夹并下载
            </Button>
          </>
        }
      >
        <label className="field">
          <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>游戏版本</span>
          <Select
            value={folderVersion}
            onChange={(e) => {
              const gameVersion = e.target.value
              setFolderVersion(gameVersion)
              const first = folderFiles?.find((file) => (file.gameVersions ?? []).includes(gameVersion))
              setFolderFileId(first?.id ?? null)
            }}
          >
            {folderFiles === null && <option value="">加载中…</option>}
            {folderFiles?.length === 0 && <option value="">没有可用版本</option>}
            {folderVersions.map((version) => (
              <option key={version} value={version}>
                {version}
              </option>
            ))}
          </Select>
        </label>
        <label className='field' style={{ marginTop: 12 }}>
          <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>模组版本</span>
          <Select value={folderFile?.id ?? ''} onChange={(e) => setFolderFileId(Number(e.target.value))}>
            {folderFilesForGame.length === 0 && <option value=''>没有可用模组版本</option>}
            {folderFilesForGame.map((file) => (
              <option key={file.id} value={file.id}>
                {file.displayName || file.fileName} · {file.releaseType === 1 ? '正式版' : file.releaseType === 2 ? 'Beta' : 'Alpha'}
              </option>
            ))}
          </Select>
        </label>
        {folderFile && (
          <div style={{ marginTop: 10, color: 'var(--faint)', fontSize: 11.5 }}>
            将下载：{folderFile.fileName}
          </div>
        )}
        <div style={{ marginTop: 8, color: 'var(--faint)', fontSize: 11.5 }}>
          只下载当前选择的文件，不自动安装前置依赖。
        </div>
      </Modal>

      <Modal
        open={missingMods.length > 0}
        onClose={() => setMissingMods([])}
        title={`有 ${missingMods.length} 个文件未能安装`}
        subtitle="整合包已安装完成，但以下文件没有成功下载，通常是作者未开放第三方下载或接口暂不可用。"
        footer={
          <Button variant="primary" onClick={() => setMissingMods([])}>
            知道了
          </Button>
        }
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxHeight: 360, overflow: 'auto' }}>
          {missingMods.map((item) => (
            <div
              key={`${item.projectId}-${item.fileId}`}
              style={{
                padding: '9px 12px',
                border: '1px solid var(--line)',
                borderRadius: 8,
                background: 'var(--panel)'
              }}
            >
              <div style={{ fontSize: 13, fontWeight: 600 }}>{item.name}</div>
              <div style={{ marginTop: 2, color: 'var(--faint)', fontSize: 11 }}>
                项目 {item.projectId} · 文件 {item.fileId}
                {item.fileName ? ` · ${item.fileName}` : ''}
              </div>
              <div style={{ marginTop: 3, color: 'var(--muted)', fontSize: 11.5 }}>{item.reason}</div>
            </div>
          ))}
        </div>
      </Modal>
    </>
  )
}

function CurseForgeCard({ hit, nameTranslation, onClick }: { hit: CurseForgeHit; nameTranslation?: string; onClick: () => void }) {
  return (
    <div className="result-card" onClick={onClick}>
      <div className="result-icon">
        {hit.logo ? <img src={hit.logo} alt="" /> : hit.name.slice(0, 1).toUpperCase()}
      </div>
      <div className="result-body">
        <div className="result-title">
          {hit.name}
          {nameTranslation && <span style={{ color: 'var(--faint)', fontWeight: 500 }}>（{nameTranslation}）</span>}
          <Badge tone="acc">{TYPE_LABEL[hit.projectType]}</Badge>
        </div>
        <div className="result-desc">{hit.summary}</div>
        <div className="result-tags">
          {hit.categories.slice(0, 4).map((category) => (
            <span className="tag" key={category}>
              {category}
            </span>
          ))}
        </div>
        <div className="result-stats">
          <span>
            <DownloadIcon size={12} /> {formatNumber(hit.downloads)}
          </span>
          {hit.authors.length > 0 && (
            <span>
              <UserRound size={12} /> {hit.authors.slice(0, 2).join(', ')}
            </span>
          )}
        </div>
      </div>
    </div>
  )
}

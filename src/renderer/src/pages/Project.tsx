import { useEffect, useMemo, useState } from 'react'
import { ArrowLeft, CalendarDays, Download as DownloadIcon, ExternalLink, FolderOpen, Package, Star, Tag, Users } from 'lucide-react'
import type { ModrinthProject, ModrinthVersion } from '@shared/types'
import { api, cn, errorMessage, formatDate, formatNumber, LOADER_LABEL } from '../api'
import { useStore } from '../store'
import { Badge, Button, EmptyState, Modal, Select, Skeleton } from '../components/ui'

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

function renderMarkdown(text: string): string {
  const lines = escapeHtml(text || '').split(/\r?\n/)
  const out: string[] = []
  let inList = false
  let inCode = false
  const inline = (line: string): string =>
    line
      .replace(/!\[([^\]]*)\]\((https?:[^)\s]+)\)/g, '<img src="$2" alt="$1" />')
      .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>')
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/`([^`]+)`/g, '<code>$1</code>')
  for (const raw of lines) {
    const line = raw.trimEnd()
    if (line.startsWith('```')) {
      if (inCode) {
        out.push('</code></pre>')
        inCode = false
      } else {
        out.push('<pre><code>')
        inCode = true
      }
      continue
    }
    if (inCode) {
      out.push(line + '\n')
      continue
    }
    if (!line.trim()) {
      if (inList) {
        out.push('</ul>')
        inList = false
      }
      continue
    }
    const heading = line.match(/^(#{1,4})\s+(.*)$/)
    if (heading) {
      if (inList) {
        out.push('</ul>')
        inList = false
      }
      const level = Math.min(3, heading[1].length)
      out.push(`<h${level}>${inline(heading[2])}</h${level}>`)
      continue
    }
    const list = line.match(/^\s*[-*]\s+(.*)$/)
    if (list) {
      if (!inList) {
        out.push('<ul>')
        inList = true
      }
      out.push(`<li>${inline(list[1])}</li>`)
      continue
    }
    if (inList) {
      out.push('</ul>')
      inList = false
    }
    if (line.startsWith('&gt;')) {
      out.push(`<blockquote>${inline(line.slice(4))}</blockquote>`)
      continue
    }
    out.push(`<p>${inline(line)}</p>`)
  }
  if (inList) out.push('</ul>')
  if (inCode) out.push('</code></pre>')
  return out.join('\n')
}

export function ProjectPage({ id }: { id: string }) {
  const navigate = useStore((s) => s.navigate)
  const back = useStore((s) => s.back)
  const page = useStore((s) => s.page)
  const instances = useStore((s) => s.instances)
  const settings = useStore((s) => s.settings)
  const toast = useStore((s) => s.toast)
  const [project, setProject] = useState<ModrinthProject | null>(null)
  const [versions, setVersions] = useState<ModrinthVersion[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [instanceId, setInstanceId] = useState(settings?.lastSelectedInstanceId || instances[0]?.id || '')
  const [versionId, setVersionId] = useState('')
  const [installing, setInstalling] = useState(false)
  const [folderOpen, setFolderOpen] = useState(false)
  const [folderVersionId, setFolderVersionId] = useState('')
  const [folderModVersionId, setFolderModVersionId] = useState('')
 
  const instance = instances.find((i) => i.id === instanceId) ?? instances[0]

  useEffect(() => {
    setProject(null)
    setVersions(null)
    setError(null)
    void api.modrinth
      .project(id)
      .then(setProject)
      .catch((e) => setError(errorMessage(e)))
    void api.modrinth
      .versions(id)
      .then((list) => {
        setVersions(list)
        setVersionId(list[0]?.id ?? '')
      })
      .catch(() => setVersions([]))
  }, [id])

  useEffect(() => {
    if (instance?.id && instance.id !== instanceId) setInstanceId(instance.id)
  }, [instance?.id])

  const compatible = useMemo(() => {
    if (!versions) return []
    if (!instance) return versions
    return versions.filter((v) => {
      const mcOk = v.game_versions.length === 0 || v.game_versions.includes(instance.mcVersion)
      const loaderOk = instance.loader === 'vanilla' || instance.loader === 'optifine' || v.loaders.includes(instance.loader)
      return mcOk && loaderOk
    })
  }, [versions, instance])

  useEffect(() => {
    if (compatible.length && !compatible.some((v) => v.id === versionId)) setVersionId(compatible[0].id)
  }, [compatible])

  const folderOptions = useMemo(() => {
    const map = new Map<string, ModrinthVersion>()
    for (const version of versions ?? []) {
      for (const gameVersion of version.game_versions ?? []) {
        if (gameVersion && !map.has(gameVersion)) map.set(gameVersion, version)
      }
    }
    return [...map.entries()].sort((a, b) => b[0].localeCompare(a[0], undefined, { numeric: true }))
  }, [versions])

  const folderGameVersions = folderOptions.map(([gameVersion]) => gameVersion)
  const folderModVersions = useMemo(
    () => (versions ?? []).filter((version) => (version.game_versions ?? []).includes(folderVersionId)),
    [versions, folderVersionId]
  )
  const folderVersion = folderModVersions.find((version) => version.id === folderModVersionId) ?? folderModVersions[0]

  const selectedVersion = versions?.find((v) => v.id === versionId) ?? compatible[0]

  const install = async (): Promise<void> => {
    if (!instance || !project || !selectedVersion) return
    setInstalling(true)
    try {
      if (project.project_type === 'modpack') {
        const res = await api.modrinth.installPack(project.project_id, project.title, selectedVersion.id)
        toast('success', `整合包 ${project.title} 安装完成`)
        navigate({ name: 'instance', id: res.instanceId })
        return
      }
      await api.modrinth.install({ projectId: project.project_id, instanceId: instance.id, versionId: selectedVersion.id })
      toast('success', `${project.title} 已安装到「${instance.name}」`)
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setInstalling(false)
    }
  }

  const openFolderInstall = (): void => {
    if (!project || !versions?.length) {
      toast('warning', '还没有加载到可用版本')
      return
    }
    const firstGame = folderOptions[0]?.[0] ?? ''
    const firstVersion = (versions ?? []).find((version) => (version.game_versions ?? []).includes(firstGame))
    setFolderVersionId(firstGame)
    setFolderModVersionId(firstVersion?.id ?? '')
    setFolderOpen(true)
  }

  const installToFolder = async (): Promise<void> => {
    if (!project || !folderVersion) return
    const picked = await api.app.pickDirectory('选择安装文件夹', instance?.gameDir)
    if (picked.canceled || !picked.paths[0]) return
    setInstalling(true)
    setFolderOpen(false)
    try {
      const result = await api.modrinth.installToFolder({
        projectId: project.project_id,
        versionId: folderVersion.id,
        destDir: picked.paths[0]
      })
      toast('success', `已安装到文件夹：${result.installed.join(', ')}`)
    } catch (e) {
      toast('error', errorMessage(e), '安装到文件夹失败')
    } finally {
      setInstalling(false)
    }
  }

  if (error) {
    return (
      <div className="page-enter">
        <div className="page-head">
          <div className="titles">
            <h1>项目详情</h1>
          </div>
        </div>
        <div className="card card-pad" style={{ color: 'var(--danger)' }}>加载失败：{error}</div>
      </div>
    )
  }

  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1 style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
            <Button variant="ghost" size="sm" icon={<ArrowLeft size={16} />} onClick={() => (page.from ? navigate(page.from) : back())}>
              返回
            </Button>
            {project?.title ?? '项目详情'}
          </h1>
          <p>{project ? `由 ${project.author || '社区作者'} 发布 · ${formatDate(project.updated)} 更新` : '正在加载项目信息…'}</p>
        </div>
      </div>

      <div className="project-detail">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
          <div className="card card-pad">
            {!project ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                <Skeleton height={28} width="40%" />
                <Skeleton height={16} />
                <Skeleton height={16} width="80%" />
              </div>
            ) : (
              <>
                <div className="project-hero">
                  <div className="project-icon">{project.icon_url ? <img src={project.icon_url} alt="" /> : project.title.slice(0, 1)}</div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontSize: 21, fontWeight: 750 }}>
                      {project.title}
                      {project.nameTranslation && <span style={{ color: 'var(--faint)', fontWeight: 500 }}>（{project.nameTranslation}）</span>}
                    </div>
                    <div style={{ color: 'var(--muted)', fontSize: 12.5, marginTop: 4, lineHeight: 1.6 }}>{project.description}</div>
                    <div className="detail-tags" style={{ marginTop: 10 }}>
                      {(project.categories ?? []).slice(0, 8).map((c) => (
                        <span className="tag acc" key={c}>
                          {c}
                        </span>
                      ))}
                      {project.license && <span className="tag">{project.license.name}</span>}
                    </div>
                    <div className="result-stats" style={{ marginTop: 10 }}>
                      <span>
                        <DownloadIcon size={12} /> {formatNumber(project.downloads)}
                      </span>
                      <span>
                        <Star size={12} /> {formatNumber(project.follows)}
                      </span>
                      <span>
                        <Users size={12} /> {project.author || '社区作者'}
                      </span>
                    </div>
                  </div>
                </div>

                <div className="divider" />
                <div className="markdown" dangerouslySetInnerHTML={{ __html: renderMarkdown(project.body) }} />
              </>
            )}
          </div>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
          <div className="card setting-group">
            <h3>安装</h3>
            <div className="sub">选择目标实例与版本</div>
            {instances.length === 0 ? (
              <EmptyState icon={<Package />} title="还没有实例" description="请先安装游戏并创建实例，再安装内容。" action={<Button variant="primary" onClick={() => navigate({ name: 'download' })}>前往安装游戏</Button>} />
            ) : (
              <>
                <div className="field">
                  <label>目标实例</label>
                  <Select value={instanceId} onChange={(e) => setInstanceId(e.target.value)}>
                    {instances.map((i) => (
                      <option key={i.id} value={i.id}>
                        {i.name} · {i.mcVersion} {LOADER_LABEL[i.loader] ?? i.loader}
                      </option>
                    ))}
                  </Select>
                </div>
                <div className="field">
                  <label>版本</label>
                  <Select value={versionId} onChange={(e) => setVersionId(e.target.value)} disabled={!compatible.length}>
                    {compatible.length === 0 && <option>没有兼容当前实例的版本</option>}
                    {compatible.slice(0, 40).map((v) => (
                      <option key={v.id} value={v.id}>
                        {v.version_number} · {v.version_type} · {v.game_versions.slice(0, 3).join(', ')}
                      </option>
                    ))}
                  </Select>
                </div>
                {selectedVersion && (
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, marginBottom: 12 }}>
                    {(selectedVersion.loaders ?? []).map((l) => (
                      <span className="tag" key={l}>
                        {l}
                      </span>
                    ))}
                    {(selectedVersion.game_versions ?? []).slice(0, 6).map((g) => (
                      <span className="tag acc" key={g}>
                        {g}
                      </span>
                    ))}
                  </div>
                )}
                <Button
                  block
                  variant="primary"
                  icon={<DownloadIcon size={16} />}
                  loading={installing}
                  disabled={!selectedVersion || compatible.length === 0 || project?.project_type === 'modpack'}
                  onClick={() => void install()}
                >
                  安装到实例
                </Button>
                {project?.project_type === 'modpack' && (
                  <Button
                    block
                    variant="soft"
                    style={{ marginTop: 10 }}
                    icon={<Package size={16} />}
                    loading={installing}
                    disabled={!selectedVersion}
                    onClick={() => void install()}
                  >
                    安装整合包并创建实例
                  </Button>
                )}
                {project?.project_type !== 'modpack' && (
                  <Button
                    block
                    variant='soft'
                    style={{ marginTop: 10 }}
                    icon={<FolderOpen size={16} />}
                    disabled={!versions?.length}
                    onClick={openFolderInstall}
                  >
                    选择版本安装到指定文件夹
                  </Button>
                )}
                <div style={{ marginTop: 12, fontSize: 11, color: 'var(--faint)', lineHeight: 1.6 }}>
                  安装到实例时会自动下载所需的前置依赖；安装到指定文件夹只下载所选文件。
                </div>
              </>
            )}
          </div>

          <div className="card setting-group">
            <h3>版本历史</h3>
            <div className="sub">最近的 {Math.min(versions?.length ?? 0, 12)} 个版本</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              {versions === null && <Skeleton height={44} />}
              {versions?.slice(0, 12).map((v) => (
                <button
                  key={v.id}
                  onClick={() => setVersionId(v.id)}
                  className={cn('menu-item')}
                  style={{
                    height: 'auto',
                    padding: '10px 10px',
                    alignItems: 'flex-start',
                    flexDirection: 'column',
                    gap: 3,
                    background: v.id === versionId ? 'var(--panel-hover)' : undefined
                  }}
                >
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8, width: '100%' }}>
                    <span style={{ fontWeight: 650 }}>{v.version_number}</span>
                    <Badge tone={v.version_type === 'release' ? 'ok' : v.version_type === 'beta' ? 'warn' : undefined}>{v.version_type}</Badge>
                    <span style={{ marginLeft: 'auto', color: 'var(--faint)', fontSize: 10.5 }}>{formatDate(v.date_published)}</span>
                  </span>
                  <span style={{ color: 'var(--muted)', fontSize: 10.5, display: 'flex', gap: 8, alignItems: 'center' }}>
                    <Tag size={11} /> {(v.game_versions ?? []).slice(0, 4).join(', ')}
                    {v.loaders.length > 0 && <span>· {v.loaders.join(', ')}</span>}
                  </span>
                </button>
              ))}
              {versions?.length === 0 && <div style={{ color: 'var(--muted)', fontSize: 12 }}>暂无版本</div>}
            </div>
          </div>

          {project && (project.source_url || project.issues_url || project.wiki_url) && (
            <div className="card setting-group">
              <h3>链接</h3>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                {[
                  ['源代码', project.source_url],
                  ['问题反馈', project.issues_url],
                  ['Wiki', project.wiki_url],
                  ['Discord', project.discord_url]
                ]
                  .filter(([, url]) => !!url)
                  .map(([label, url]) => (
                    <button key={label} className="menu-item" onClick={() => void api.app.openExternal(url as string)}>
                      <ExternalLink size={14} /> {label}
                    </button>
                  ))}
              </div>
            </div>
          )}
        </div>
      </div>

      <Modal
        open={folderOpen}
        onClose={() => !installing && setFolderOpen(false)}
        title="选择游戏版本"
        subtitle="列出该项目支持的全部 MC 版本，不受实例筛选影响"
        footer={
          <>
            <Button variant="ghost" onClick={() => setFolderOpen(false)} disabled={installing}>
              取消
            </Button>
            <Button variant="primary" icon={<FolderOpen size={16} />} disabled={!folderVersion} onClick={() => void installToFolder()}>
              选择文件夹并下载
            </Button>
          </>
        }
      >
        <label className="field">
          <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>游戏版本</span>
          <Select
            value={folderVersionId}
            onChange={(e) => {
              const gameVersion = e.target.value
              setFolderVersionId(gameVersion)
              const first = (versions ?? []).find((version) => (version.game_versions ?? []).includes(gameVersion))
              setFolderModVersionId(first?.id ?? '')
            }}
          >
            {folderGameVersions.length === 0 && <option value="">没有可用版本</option>}
            {folderGameVersions.map((gameVersion) => (
              <option key={gameVersion} value={gameVersion}>
                {gameVersion}
              </option>
            ))}
          </Select>
        </label>
        <label className="field" style={{ marginTop: 12 }}>
          <span style={{ fontSize: 12, color: 'var(--text-soft)', marginBottom: 6 }}>模组版本</span>
          <Select value={folderVersion?.id ?? ''} onChange={(e) => setFolderModVersionId(e.target.value)}>
            {folderModVersions.length === 0 && <option value="">没有可用模组版本</option>}
            {folderModVersions.map((version) => (
              <option key={version.id} value={version.id}>
                {version.version_number} · {version.version_type} · {(version.loaders ?? []).join('/') || '通用'}
              </option>
            ))}
          </Select>
        </label>
        <div style={{ marginTop: 10, color: 'var(--faint)', fontSize: 11.5 }}>
          只下载当前选择的文件，不自动安装前置依赖；下载后可在目标文件夹中查看。
        </div>
      </Modal>
    </div>
  )
}

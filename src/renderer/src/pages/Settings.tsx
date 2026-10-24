import { useEffect, useState } from 'react'
import {
  Boxes,
  Cpu,
  Download,
  FolderOpen,
  Gauge,
  Globe2,
  HardDrive,
  Info,
  Languages,
  Monitor,
  Moon,
  Palette,
  RefreshCw,
  RotateCcw,
  Sliders,
  Sparkles,
  Sun,
  Trash2,
  UserRound,
  Zap
} from 'lucide-react'
import type { EndpointTestResult, JavaInfo, Settings, StorageStats } from '@shared/types'
import { api, cn, errorMessage, formatBytes, GRADIENTS } from '../api'
import { useStore, useT } from '../store'
import { Badge, Button, ConfirmDialog, Field, Input, Segmented, Select, Slider, Switch, Textarea } from '../components/ui'

type Section = 'general' | 'game' | 'download' | 'java' | 'advanced' | 'storage'

const ACCENTS: { key: string; name: string }[] = [
  { key: 'aurora', name: '极光' },
  { key: 'sunset', name: '日落' },
  { key: 'ocean', name: '深海' },
  { key: 'forest', name: '森林' },
  { key: 'candy', name: '糖果' },
  { key: 'mono', name: '极简' }
]

function Row({
  name,
  desc,
  children,
  wide,
  icon
}: {
  name: React.ReactNode
  desc?: React.ReactNode
  children: React.ReactNode
  wide?: boolean
  icon?: React.ReactNode
}) {
  return (
    <div className="setting-row">
      <div className="info">
        <div className="name">
          {icon}
          {name}
        </div>
        {desc && <div className="desc">{desc}</div>}
      </div>
      <div className={cn('control', wide && 'wide')}>{children}</div>
    </div>
  )
}

export function SettingsPage() {
  const t = useT()
  const settings = useStore((s) => s.settings)
  const patch = useStore((s) => s.patchSettings)
  const toast = useStore((s) => s.toast)
  const section = useStore((s) => s.settingsSection)
  const setSection = useStore((s) => s.setSettingsSection)
  const checkUpdates = useStore((s) => s.checkUpdates)
  const [checkingUpdates, setCheckingUpdates] = useState(false)
  const [sys, setSys] = useState<Record<string, unknown>>({})
  const [javas, setJavas] = useState<JavaInfo[] | null>(null)
  const [scanning, setScanning] = useState(false)
  const [resetOpen, setResetOpen] = useState(false)
  const [downloading, setDownloading] = useState<number | null>(null)
  const [proxyHost, setProxyHost] = useState(settings?.proxyHost ?? '127.0.0.1')
  const [proxyPort, setProxyPort] = useState(String(settings?.proxyPort ?? 7890))
  const [msClientId, setMsClientId] = useState(settings?.msClientId ?? '')
  const [testingNetwork, setTestingNetwork] = useState(false)
  const [networkResults, setNetworkResults] = useState<EndpointTestResult[] | null>(null)
  const [storage, setStorage] = useState<StorageStats | null>(null)
  const [storageBusy, setStorageBusy] = useState(false)
  const [cleanOpen, setCleanOpen] = useState(false)
  const [pruneOpen, setPruneOpen] = useState(false)

  const testNetwork = async (): Promise<void> => {
    setTestingNetwork(true)
    try {
      setNetworkResults(await api.network.test())
    } catch (e) {
      toast('error', errorMessage(e), '连通性检测失败')
    } finally {
      setTestingNetwork(false)
    }
  }

  useEffect(() => {
    void api.settings.systemInfo().then(setSys).catch(() => {})
  }, [])

  useEffect(() => {
    if (section !== 'java' || javas !== null) return
    void api.java.list().then(setJavas).catch(() => setJavas([]))
  }, [section, javas])

  if (!settings) return null

  const totalMem = Number(sys.memoryMB ?? 8192)
  const suggested = Number(sys.suggestedMemory ?? 4096)
  const maxMem = Math.max(2048, Math.floor(totalMem * 0.75 / 256) * 256)
  const memory = settings.autoMemory ? suggested : settings.defaultMemory || suggested

  const scanJava = async (): Promise<void> => {
    setScanning(true)
    try {
      setJavas(await api.java.scan())
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setScanning(false)
    }
  }

  const downloadJava = async (major: number): Promise<void> => {
    setDownloading(major)
    try {
      const info = await api.java.download(major)
      setJavas(await api.java.list())
      toast('success', `Java ${info.version} 已安装`)
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setDownloading(null)
    }
  }

  useEffect(() => {
    if (section !== 'storage') return
    void api.storage.stats().then(setStorage).catch(() => {})
  }, [section])

  const runClean = async (): Promise<void> => {
    setCleanOpen(false)
    setStorageBusy(true)
    try {
      const result = await api.storage.clean()
      setStorage(await api.storage.stats())
      toast('success', `已清理 ${formatBytes(result.freed)}`)
    } catch (e) {
      toast('error', errorMessage(e), '清理缓存失败')
    } finally {
      setStorageBusy(false)
    }
  }

  const runPrune = async (): Promise<void> => {
    setPruneOpen(false)
    setStorageBusy(true)
    try {
      const result = await api.storage.prune()
      setStorage(await api.storage.stats())
      toast('success', `已释放 ${formatBytes(result.freed)}：依赖库 ${formatBytes(result.libraries)}，资源 ${formatBytes(result.assets)}`)
    } catch (e) {
      toast('error', errorMessage(e), '精简依赖库失败')
    } finally {
      setStorageBusy(false)
    }
  }

  const sections: { key: Section; label: string; icon: React.ReactNode }[] = [
    { key: 'general', label: t('settings.general'), icon: <Sparkles /> },
    { key: 'game', label: t('settings.game'), icon: <Boxes /> },
    { key: 'download', label: t('settings.download'), icon: <Download /> },
    { key: 'java', label: t('settings.java'), icon: <Cpu /> },
    { key: 'advanced', label: t('settings.advanced'), icon: <Sliders /> },
    { key: 'storage', label: '存储', icon: <HardDrive /> }
  ]

  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1>{t('settings.title')}</h1>
          <p>{t('settings.subtitle')}</p>
        </div>
      </div>

      <div className="settings-layout">
        <div className="settings-nav">
          {sections.map((s) => (
            <button key={s.key} className={cn(section === s.key && 'active')} onClick={() => setSection(s.key)}>
              {s.icon}
              {s.label}
            </button>
          ))}
        </div>

        <div className="settings-section">
          {section === 'general' && (
            <>
              <div className="card setting-group">
                <h3>个性化</h3>
                <div className="sub">选择界面语言与主题</div>
                <Row
                  name={<><Languages size={15} /> {t('settings.language')}</>}
                  desc="切换启动器界面语言"
                >
                  <Select value={settings.language} onChange={(e) => void patch({ language: e.target.value as 'zh-CN' | 'en-US' })}>
                    <option value="zh-CN">简体中文</option>
                    <option value="en-US">English</option>
                  </Select>
                </Row>
                <Row name={<><Monitor size={15} /> {t('settings.appearance')}</>} desc="选择深色或浅色外观">
                  <Segmented
                    value={settings.theme}
                    onChange={(v) => void patch({ theme: v })}
                    options={[
                      { value: 'dark', label: <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}><Moon size={13} /> 深色</span> },
                      { value: 'light', label: <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5 }}><Sun size={13} /> 浅色</span> },
                      { value: 'system', label: '跟随系统' }
                    ]}
                  />
                </Row>
                <Row name={<><Palette size={15} /> {t('settings.accent')}</>} desc="界面强调色，用于选中态与强调元素">
                  <div className="accent-swatches">
                    {ACCENTS.map((a) => {
                      const g = GRADIENTS[a.key] ?? GRADIENTS.aurora
                      return (
                        <button
                          key={a.key}
                          title={a.name}
                          className={cn('swatch', settings.accent === a.key && 'active')}
                          style={{ background: `linear-gradient(135deg, ${g[0]}, ${g[1]})` }}
                          onClick={() => void patch({ accent: a.key })}
                        />
                      )
                    })}
                  </div>
                </Row>
              </div>

              <div className="card setting-group">
                <h3>窗口行为</h3>
                <div className="sub">控制启动器窗口与游戏启动时的互动</div>
                <Row name="关闭窗口时" desc="选择关闭启动器主窗口后的行为">
                  <Select value={settings.closeBehavior} onChange={(e) => void patch({ closeBehavior: e.target.value as 'exit' | 'tray' })}>
                    <option value="exit">退出启动器</option>
                    <option value="tray">最小化到系统托盘</option>
                  </Select>
                </Row>
                <Row name="启动游戏后最小化" desc="游戏启动后自动最小化启动器窗口">
                  <Switch checked={settings.minimizeOnLaunch} onChange={(v) => void patch({ minimizeOnLaunch: v })} />
                </Row>
                <Row name="启动时显示控制台" desc="自动展开任务与日志面板">
                  <Switch checked={settings.showConsoleOnLaunch} onChange={(v) => void patch({ showConsoleOnLaunch: v })} />
                </Row>
                <Row name="自动导入 .minecraft 版本" desc="首次启动时把已有 .minecraft 中的版本直接显示为实例">
                  <Switch checked={settings.autoImportVersions} onChange={(v) => void patch({ autoImportVersions: v })} />
                </Row>
              </div>
            </>
          )}

          {section === 'game' && (
            <>
              <div className="card setting-group">
                <h3>内存分配</h3>
                <div className="sub">为游戏分配的最大内存，自动模式根据系统内存计算</div>
                <Row name={<><Zap size={15} /> 自动内存</>} desc={`根据系统内存自动选择（当前建议 ${suggested} MB）`}>
                  <Switch checked={settings.autoMemory} onChange={(v) => void patch({ autoMemory: v })} />
                </Row>
                <Row name="默认最大内存" desc={`系统总内存 ${totalMem} MB · 建议不超过 ${maxMem} MB`} wide>
                  <div className="memory-input">
                    <Slider
                      min={1024}
                      max={maxMem}
                      step={256}
                      value={memory}
                      onChange={(v) => void patch({ defaultMemory: v, autoMemory: false })}
                    />
                    <span style={{ minWidth: 78, textAlign: 'right', fontWeight: 650, fontSize: 12.5 }}>{memory} MB</span>
                  </div>
                </Row>
                <Row name="资源文件完整性" desc="启动时检查并补全缺失的资源与依赖库">
                  <Switch checked={settings.downloadAssets} onChange={(v) => void patch({ downloadAssets: v })} />
                </Row>
                <Row name="首次启动切换中文" desc="实例第一次启动时把游戏语言设为简体中文；之后不再覆盖手动修改">
                  <Switch checked={settings.firstLaunchChinese} onChange={(v) => void patch({ firstLaunchChinese: v })} />
                </Row>

              </div>

              <div className="card setting-group">
                <h3>启动参数</h3>
                <div className="sub">留空则使用启动器推荐的参数，实例设置可覆盖</div>
                <Field label={t('settings.jvmArgs') as string} hint="例如：-XX:+UseZGC -Dfile.encoding=UTF-8（按空格分隔）">
                  <Textarea value={settings.jvmArgs} onChange={(e) => void patch({ jvmArgs: e.target.value })} placeholder="-XX:+UseG1GC" />
                </Field>
                <Field label={t('settings.gameArgs') as string} hint="将追加到游戏启动参数末尾">
                  <Textarea value={settings.gameArgs} onChange={(e) => void patch({ gameArgs: e.target.value })} placeholder="--width 1280 --height 720" />
                </Field>
              </div>
            </>
          )}

          {section === 'download' && (
            <>
              <div className="card setting-group">
                <h3>下载设置</h3>
                <div className="sub">控制游戏与资源的下载方式</div>
                <Row name={<><Globe2 size={15} /> {t('settings.source')}</>} desc="默认下载源；可在下方为原版、Forge、Fabric 等单独设置">
                  <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                    <Select
                      value={settings.downloadSource}
                      onChange={(e) => void patch({ downloadSource: e.target.value as Settings['downloadSource'] })}
                      style={{ minWidth: 220 }}
                    >
                      <option value="auto">自动（官方优先，失败回退镜像）</option>
                      <option value="mirror">镜像（BMCLAPI / MCIM）</option>
                      <option value="official">官方</option>
                    </Select>
                    <Button
                      size="sm"
                      variant="ghost"
                      icon={<RefreshCw size={14} />}
                      loading={testingNetwork}
                      onClick={() => void testNetwork()}
                    >
                      检测连通性
                    </Button>
                  </div>
                </Row>
                <div className="sub" style={{ marginTop: 10 }}>分类下载源：留空则跟随默认源</div>
                {([
                  ['vanilla', '原版游戏'],
                  ['forge', 'Forge'],
                  ['neoforge', 'NeoForge'],
                  ['fabric', 'Fabric'],
                  ['quilt', 'Quilt']
                ] as const).map(([key, label]) => (
                  <Row key={key} name={label} desc="仅影响此类型的版本列表与安装文件">
                    <Select
                      value={settings.downloadSources?.[key] ?? ''}
                      onChange={(e) =>
                        void patch({
                          downloadSources: { ...settings.downloadSources, [key]: (e.target.value || undefined) as Settings['downloadSource'] | undefined }
                        })
                      }
                      style={{ minWidth: 220 }}
                    >
                      <option value="">跟随默认</option>
                      <option value="auto">自动</option>
                      <option value="mirror">镜像</option>
                      <option value="official">官方</option>
                    </Select>
                  </Row>
                ))}
                {networkResults && (
                  <div
                    style={{
                      display: 'grid',
                      gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))',
                      gap: 6,
                      margin: '0 0 8px'
                    }}
                  >
                    {networkResults.map((result) => (
                      <div
                        key={result.id}
                        title={result.error ?? result.url}
                        style={{
                          display: 'flex',
                          justifyContent: 'space-between',
                          gap: 8,
                          padding: '7px 10px',
                          borderRadius: 9,
                          background: 'var(--panel)',
                          border: '1px solid var(--line)',
                          fontSize: 11.5
                        }}
                      >
                        <span style={{ color: 'var(--text-soft)' }}>{result.label}</span>
                        <span style={{ color: result.ok ? 'var(--success)' : 'var(--danger)', fontWeight: 600 }}>
                          {result.ok ? `${result.ms} ms` : '不可用'}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
                <Row name={<><Gauge size={15} /> {t('settings.concurrency')}</>} desc="同时下载的文件数量，过高可能造成网络拥塞" wide>
                  <div className="memory-input">
                    <Slider min={4} max={64} step={2} value={settings.maxConcurrency} onChange={(v) => void patch({ maxConcurrency: v })} />
                    <span style={{ minWidth: 40, textAlign: 'right', fontWeight: 650, fontSize: 12.5 }}>{settings.maxConcurrency}</span>
                  </div>
                </Row>
                <Row name={<><Gauge size={15} /> {t('settings.downloadThreads')}</>} desc="全局最大并发分段数，最高 2048，默认 128；单文件自动限制分段数" wide>
                  <div className="memory-input">
                    <Slider
                      min={1}
                      max={2048}
                      step={1}
                      value={settings.downloadThreads ?? 128}
                      onChange={(v) => void patch({ downloadThreads: v })}
                    />
                    <span style={{ minWidth: 44, textAlign: 'right', fontWeight: 650, fontSize: 12.5 }}>{settings.downloadThreads ?? 128}</span>
                  </div>
                </Row>
                <Row name="使用代理" desc="下载与登录请求将通过 HTTP 代理发送">
                  <Switch checked={settings.proxyEnabled} onChange={(v) => void patch({ proxyEnabled: v })} />
                </Row>
                {settings.proxyEnabled && (
                  <Row name="代理地址" desc="例如 127.0.0.1:7890">
                    <div style={{ display: 'flex', gap: 8 }}>
                      <Input style={{ width: 150 }} value={proxyHost} onChange={(e) => setProxyHost(e.target.value)} onBlur={() => void patch({ proxyHost })} />
                      <Input
                        style={{ width: 80 }}
                        value={proxyPort}
                        onChange={(e) => setProxyPort(e.target.value.replace(/\D/g, ''))}
                        onBlur={() => void patch({ proxyPort: Number(proxyPort) || 7890 })}
                      />
                    </div>
                  </Row>
                )}
              </div>

              <div className="card setting-group">
                <h3>存储位置</h3>
                <div className="sub">每个启动器文件夹独立保存配置与实例；切换游戏文件夹后会自动导入该目录中的版本</div>
                <Row name={<><HardDrive size={15} /> {t('settings.rootDir')}</>} desc={settings.rootDir} wide>
                  <Button
                    variant="ghost"
                    onClick={() => {
                      void api.settings
                        .usePortableRoot()
                        .then((s) => {
                          useStore.setState({ settings: s })
                          void api.app.info().then((info) => useStore.setState({ appInfo: info }))
                          void useStore.getState().refreshInstances()
                          toast('success', '已切换到启动器所在目录的 .minecraft')
                        })
                        .catch((e) => toast('error', errorMessage(e)))
                    }}
                  >
                    启动器文件夹
                  </Button>
                  <Button
                    icon={<FolderOpen size={15} />}
                    onClick={() => {
                      void api.settings
                        .pickRoot()
                        .then((s) => {
                          useStore.setState({ settings: s })
                          void api.app.info().then((info) => useStore.setState({ appInfo: info }))
                          void useStore.getState().refreshInstances()
                          toast('success', '数据目录已更新')
                        })
                        .catch((e) => toast('error', errorMessage(e)))
                    }}
                  >
                    切换游戏文件夹
                  </Button>
                  <Button variant="ghost" onClick={() => void api.app.openPath(settings.rootDir)}>
                    打开
                  </Button>
                </Row>
              </div>
            </>
          )}

          {section === 'java' && (
            <>
              <div className="card setting-group">
                <h3>Java 运行时</h3>
                <div className="sub">启动器会自动选择匹配游戏版本的 Java，也可以手动指定</div>
                <Row name={<><Cpu size={15} /> {t('settings.javaPath')}</>} desc={settings.javaPath || '自动选择已检测到的 Java'} wide>
                  <Input
                    style={{ width: 260 }}
                    value={settings.javaPath}
                    placeholder="自动"
                    onChange={(e) => void patch({ javaPath: e.target.value })}
                  />
                  <Button
                    variant="soft"
                    onClick={() =>
                      void api.app
                        .pickFile([{ name: 'Java', extensions: ['exe'] }])
                        .then((r) => {
                          if (!r.canceled && r.paths[0]) void patch({ javaPath: r.paths[0] })
                        })
                    }
                  >
                    浏览
                  </Button>
                </Row>
                <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 14, flexWrap: 'wrap' }}>
                  <Button size="sm" icon={<RefreshCw size={14} />} loading={scanning} onClick={() => void scanJava()}>
                    重新扫描
                  </Button>
                  {[8, 17, 21, 25].map((major) => (
                    <Button key={major} size="sm" variant="soft" loading={downloading === major} onClick={() => void downloadJava(major)}>
                      下载 Java {major}
                    </Button>
                  ))}
                </div>
                <div className="java-list" style={{ marginTop: 16 }}>
                  {javas === null && <div style={{ color: 'var(--muted)', fontSize: 12 }}>正在扫描 Java…</div>}
                  {javas?.length === 0 && <div style={{ color: 'var(--muted)', fontSize: 12 }}>未检测到 Java，请下载一个运行时。</div>}
                  {javas?.map((j) => (
                    <div className="java-row" key={j.path}>
                      <Badge tone={j.managed ? 'acc' : undefined}>{j.managed ? '内置' : '系统'}</Badge>
                      <strong style={{ fontSize: 12.5 }}>Java {j.major}</strong>
                      <span style={{ color: 'var(--muted)' }}>{j.version || '未知版本'} · {j.vendor}</span>
                      <span className="path" title={j.path}>
                        {j.path}
                      </span>
                      {settings.javaPath !== j.path && (
                        <Button size="sm" variant="ghost" onClick={() => void patch({ javaPath: j.path })}>
                          设为默认
                        </Button>
                      )}
                      {j.managed && (
                        <button
                          className="icon-btn danger"
                          title="删除内置 Java"
                          onClick={() =>
                            void api.java
                              .delete(j.path)
                              .then(() => setJavas((prev) => prev?.filter((x) => x.path !== j.path) ?? null))
                              .catch((e) => toast('error', errorMessage(e)))
                          }
                        >
                          <Trash2 />
                        </button>
                      )}
                    </div>
                  ))}
                </div>
              </div>
            </>
          )}

          {section === 'advanced' && (
            <>
              <div className="card setting-group">
                <h3>账户</h3>
                <div className="sub">微软登录使用 OAuth 设备代码流，可填写自己的 Azure 应用 ID</div>
                <Row name={<><UserRound size={15} /> 微软登录 Client ID</>} desc="留空使用启动器内置的公共应用 ID" wide>
                  <Input
                    style={{ width: 340 }}
                    value={msClientId}
                    onChange={(e) => setMsClientId(e.target.value)}
                    onBlur={() => void patch({ msClientId: msClientId.trim() })}
                    placeholder="c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb"
                  />
                </Row>
              </div>

              <div className="card setting-group">
                <h3>信息</h3>
                <div className="sub">系统与启动器运行信息</div>
                <Row name="系统" desc={`${String(sys.os ?? '')} · ${String(sys.cpu ?? '')} · ${String(sys.cores ?? '')} 核`}>
                  <span style={{ color: 'var(--muted)', fontSize: 12 }}>{String(sys.memoryMB ?? '')} MB</span>
                </Row>
                <Row name="配置目录" desc={String(sys.configPath ?? '')} wide>
                  <Button variant="ghost" icon={<FolderOpen size={15} />} onClick={() => void api.app.openPath(String(sys.configPath ?? ''))}>
                    打开
                  </Button>
                </Row>
                <Row name="重置设置" desc="将所有设置恢复为默认值（不会删除游戏文件）">
                  <Button variant="danger" icon={<RotateCcw size={15} />} onClick={() => setResetOpen(true)}>
                    重置
                  </Button>
                </Row>
              </div>

              <div className="card setting-group">
                <h3>关于版本</h3>
                <div className="sub">Aurora Launcher 由社区驱动，使用 Electron 与 React 构建</div>
                <Row name="启动器版本" desc={`Electron ${String(sys.electron ?? '')} · Chromium ${String(sys.chrome ?? '')} · Node ${String(sys.node ?? '')}`}>
                  <Badge tone="acc">v{String(sys.appVersion ?? '1.0.0')}</Badge>
                </Row>
                <Row name="自动检查更新" desc="启动后自动检查 GitHub Releases；只有发现新版本时才弹窗">
                  <Switch checked={settings.checkUpdates} onChange={(v) => void patch({ checkUpdates: v })} />
                </Row>
                <Row name="检查更新" desc="立即检查 GitHub 上的最新版本">
                  <Button
                    variant="ghost"
                    loading={checkingUpdates}
                    onClick={() => {
                      setCheckingUpdates(true)
                      void checkUpdates(true).finally(() => setCheckingUpdates(false))
                    }}
                  >
                    检查更新
                  </Button>
                </Row>
                <Row name="游戏数据目录" desc={settings.rootDir} wide>
                  <Button variant="ghost" icon={<FolderOpen size={15} />} onClick={() => void api.app.openPath(settings.rootDir)}>
                    打开
                  </Button>
                </Row>
              </div>
            </>
          )}
          {section === 'storage' && (
            <>
              <div className="card setting-group">
                <h3>存储占用</h3>
                <div className="sub">游戏数据目录：{storage?.root ?? settings.rootDir}</div>
                <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginBottom: 14 }}>
                  <Button variant="ghost" icon={<RefreshCw size={14} />} loading={storageBusy} onClick={() => void api.storage.stats().then(setStorage)}>
                    重新统计
                  </Button>
                  <Button variant="soft" icon={<Trash2 size={14} />} disabled={storageBusy} onClick={() => setCleanOpen(true)}>
                    清理缓存与安装器
                  </Button>
                  <Button variant="soft" icon={<HardDrive size={14} />} disabled={storageBusy} onClick={() => setPruneOpen(true)}>
                    精简未使用依赖库与资源
                  </Button>
                </div>
                <div className="info-list">
                  {[
                    ['总占用', storage?.total],
                    ['资源 assets', storage?.assets],
                    ['依赖库 libraries', storage?.libraries],
                    ['版本与实例 versions', storage?.versions],
                    ['实例记录 instances', storage?.instances],
                    ['启动器缓存 cache', storage?.cache],
                    ['整合包缓存 packs', storage?.packs],
                    ['本地库 natives', storage?.natives],
                    ['安装器残留', storage?.installerArtifacts]
                  ].map(([label, value]) => (
                    <div className="info-row" key={String(label)}>
                      <span className="k">{label}</span>
                      <span className="v">{typeof value === 'number' ? formatBytes(value) : '计算中…'}</span>
                    </div>
                  ))}
                </div>
                <div style={{ marginTop: 12, color: 'var(--muted)', fontSize: 11.5, lineHeight: 1.7 }}>
                  下载的版本、资源与依赖库会被复用，所以再次安装同版本会更快；它们不是重复副本，但会持续占用磁盘。
                  assets 与 libraries 是多个版本共享的，不能直接删整个目录。精简功能只删除未被任何已安装版本引用的文件。
                </div>
              </div>
            </>
          )}

        </div>
      </div>

      <ConfirmDialog
        open={cleanOpen}
        title="清理缓存与安装器"
        message={<p>将删除启动器缓存、整合包压缩包、natives、日志以及安装器 jar/log。不会删除已安装的游戏版本、依赖库、资源或实例。</p>}
        confirmText="开始清理"
        onClose={() => setCleanOpen(false)}
        onConfirm={() => void runClean()}
      />

      <ConfirmDialog
        open={pruneOpen}
        title="精简未使用的依赖库与资源"
        message={<p>会扫描所有已安装版本的 JSON，只删除没有被任何版本引用的 libraries 与 assets 文件。若扫描数据不完整会自动跳过，不会删除整个共享目录。</p>}
        confirmText="开始精简"
        danger
        onClose={() => setPruneOpen(false)}
        onConfirm={() => void runPrune()}
      />

      <ConfirmDialog
        open={resetOpen}
        title="重置所有设置"
        message="确定要将所有启动器设置恢复为默认值吗？此操作不会删除已安装的游戏或实例。"
        confirmText="重置"
        danger
        onClose={() => setResetOpen(false)}
        onConfirm={() => {
          void api.settings
            .reset()
            .then((s) => {
              useStore.setState({ settings: s })
              toast('success', '设置已重置')
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />
    </div>
  )
}

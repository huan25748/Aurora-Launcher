import { Boxes, ExternalLink, Heart, Package, Terminal, Zap } from 'lucide-react'
import { api, formatDuration } from '../api'
import { useStore, useT } from '../store'
import { Badge, Button } from '../components/ui'
import { BrandMark } from '../components/TitleBar'
import bangbangAvatar from '../assets/credits/bangbang93.png'
import z0z0r4Avatar from '../assets/credits/z0z0r4.png'
import crafatarIcon from '../assets/credits/crafatar.png'
import mcmodIcon from '../assets/credits/mcmod.png'
import mymemoryIcon from '../assets/credits/mymemory.png'
import modrinthLogo from '../assets/credits/modrinth.svg'
import curseforgeLogo from '../assets/credits/curseforge.svg'

export function AboutPage() {
  const t = useT()
  const appInfo = useStore((s) => s.appInfo)
  const versions = useStore((s) => s.instances)
  const links = [
    { label: 'Minecraft 官方网站', url: 'https://www.minecraft.net/', icon: <ExternalLink size={15} /> },
    { label: 'Fabric 模组加载器', url: 'https://fabricmc.net/', icon: <Package size={15} /> },
    { label: 'Modrinth 资源中心', url: 'https://modrinth.com/', icon: <Boxes size={15} /> },
    { label: 'Electron', url: 'https://www.electronjs.org/', icon: <Terminal size={15} /> },
    { label: 'React', url: 'https://react.dev/', icon: <Zap size={15} /> }
  ]
  const credits = [
    {
      name: 'bangbang93',
      desc: '提供 BMCLAPI 镜像源和 Forge 安装工具，详见 bmclapi.bangbang93.com',
      action: '赞助镜像源',
      url: 'https://bmclapi.bangbang93.com/',
      bg: '#f97316',
      icon: <img src={bangbangAvatar} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
    },
    {
      name: 'MC 百科',
      desc: '提供了 Mod 名称的中文翻译和更多相关信息！',
      action: '打开百科',
      url: 'https://www.mcmod.cn/',
      bg: '#ffffff',
      icon: <img src={mcmodIcon} alt="" style={{ width: 26, height: 26, objectFit: 'contain' }} />
    },
    {
      name: 'z0z0r4',
      desc: '提供了 MCIM 社区资源镜像源和帮助库图床！',
      action: 'MCIM 主页',
      url: 'https://mod.mcimirror.top/',
      bg: '#334155',
      icon: <img src={z0z0r4Avatar} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
    },
    {
      name: 'Modrinth',
      desc: '模组、整合包、资源包与光影的数据与下载接口',
      action: 'Modrinth',
      url: 'https://modrinth.com/',
      bg: '#ffffff',
      icon: <img src={modrinthLogo} alt="" style={{ width: 26, height: 26, objectFit: 'contain' }} />
    },
    {
      name: 'CurseForge',
      desc: '模组与整合包数据接口，通过 MCIM 公益代理访问',
      action: 'CurseForge',
      url: 'https://www.curseforge.com/',
      bg: '#ffffff',
      icon: <img src={curseforgeLogo} alt="" style={{ width: 26, height: 26, objectFit: 'contain' }} />
    },
    {
      name: 'MyMemory',
      desc: '中文搜索词转英文的免费翻译接口',
      action: 'MyMemory',
      url: 'https://mymemory.translated.net/',
      bg: '#ffffff',
      icon: <img src={mymemoryIcon} alt="" style={{ width: 24, height: 24, objectFit: 'contain' }} />
    },
    {
      name: 'Crafatar / MC-Heads',
      desc: '提供 3D 玩家头像渲染与皮肤预览',
      action: 'Crafatar',
      url: 'https://crafatar.com/',
      bg: '#1e293b',
      icon: <img src={crafatarIcon} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
    }
  ]

  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1>{t('about.title')}</h1>
          <p>{t('about.subtitle')}</p>
        </div>
      </div>

      <div className="detail-grid" style={{ alignItems: 'stretch' }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
          <div className="card card-pad" style={{ display: 'flex', gap: 20, alignItems: 'center' }}>
            <div className="detail-cover" style={{ ['--c1' as never]: 'var(--acc1)', ['--c2' as never]: 'var(--acc2)', ['--c3' as never]: 'var(--acc3)' }}>
              <BrandMark />
            </div>
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 24, fontWeight: 750, letterSpacing: '-0.4px' }}>Aurora Launcher</div>
              <div style={{ color: 'var(--muted)', fontSize: 13, marginTop: 4 }}>
                专注性能与体验的 Minecraft 启动器 · 版本 v{appInfo?.version ?? '1.0.0'}
              </div>
              <div style={{ display: 'flex', gap: 6, marginTop: 12, flexWrap: 'wrap' }}>
                <Badge tone="acc">Electron {appInfo?.electron}</Badge>
                <Badge>Chromium {appInfo?.chrome}</Badge>
                <Badge>Node {appInfo?.node}</Badge>
                <Badge>{appInfo?.platform} / {appInfo?.arch}</Badge>
              </div>
            </div>
          </div>

          <div className="card setting-group">
            <h3>功能一览</h3>
            <div className="sub">Aurora Launcher 提供的核心能力</div>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 10, marginTop: 6 }}>
              {[
                ['多版本管理', '原版、Fabric、Quilt、Forge、NeoForge 一键安装'],
                ['实例隔离', '每个实例拥有独立的模组、存档与配置'],
                ['资源中心', '内置 Modrinth 搜索与依赖自动安装'],
                ['账户支持', '离线、微软 OAuth 与第三方认证'],
                ['Java 管理', '自动检测并下载匹配的 Java 运行时'],
                ['下载加速', '官方源与国内镜像自动切换'],
                ['崩溃分析', '自动识别常见崩溃原因并给出建议'],
                ['现代化界面', '深色 / 浅色主题与自定义强调色']
              ].map(([title, desc]) => (
                <div key={title} style={{ padding: 14, borderRadius: 14, background: 'var(--panel)', border: '1px solid var(--line)' }}>
                  <div style={{ fontWeight: 650, fontSize: 12.5 }}>{title}</div>
                  <div style={{ color: 'var(--muted)', fontSize: 11.5, marginTop: 4, lineHeight: 1.55 }}>{desc}</div>
                </div>
              ))}
            </div>
          </div>

          <div className="card setting-group">
            <h3>数据统计</h3>
            <div className="sub">本地启动器数据</div>
            <div className="info-list" style={{ marginTop: 4 }}>
              <div className="info-row">
                <span className="k">实例数量</span>
                <span className="v">{appInfo?.totalInstances ?? 0}</span>
              </div>
              <div className="info-row">
                <span className="k">已安装版本</span>
                <span className="v">{appInfo?.totalVersions ?? 0}</span>
              </div>
              <div className="info-row">
                <span className="k">总游戏时长</span>
                <span className="v">{formatDuration(appInfo?.totalPlayTimeMs ?? 0)}</span>
              </div>
              <div className="info-row">
                <span className="k">实例记录</span>
                <span className="v">{versions.length}</span>
              </div>
            </div>
          </div>

          <div className="card setting-group">
            <h3>目录</h3>
            <div className="sub">游戏数据与配置</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {[
                ['游戏数据', appInfo?.rootDir ?? ''],
                ['版本', appInfo?.versionsDir ?? ''],
                ['资源', appInfo?.assetsDir ?? ''],
                ['依赖库', appInfo?.librariesDir ?? '']
              ].map(([label, value]) => (
                <div key={label}>
                  <div style={{ fontSize: 11, color: 'var(--muted)', fontWeight: 600 }}>{label}</div>
                  <button
                    onClick={() => value && void api.app.openPath(value)}
                    style={{
                      fontSize: 10.5,
                      color: 'var(--faint)',
                      fontFamily: 'var(--font-mono)',
                      textAlign: 'left',
                      wordBreak: 'break-all',
                      lineHeight: 1.5
                    }}
                  >
                    {value}
                  </button>
                </div>
              ))}
            </div>
          </div>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
          <div className="card setting-group">
            <h3>特别感谢</h3>
            <div className="sub">本项目使用的镜像源、数据接口与开放服务</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 6 }}>
              {credits.map((credit) => (
                <div
                  key={credit.name}
                  style={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 10,
                    padding: '9px 0',
                    borderBottom: '1px solid var(--line)'
                  }}
                >
                  <div
                    style={{
                      width: 34,
                      height: 34,
                      borderRadius: 10,
                      flex: 'none',
                      display: 'grid',
                      placeItems: 'center',
                      overflow: 'hidden',
                      background: credit.bg
                    }}
                  >
                    {credit.icon}
                  </div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontWeight: 650, fontSize: 12.5 }}>{credit.name}</div>
                    <div style={{ color: 'var(--muted)', fontSize: 10.5, marginTop: 2, lineHeight: 1.45 }}>{credit.desc}</div>
                  </div>
                  <Button size="sm" variant="soft" onClick={() => void api.app.openExternal(credit.url)}>
                    {credit.action}
                  </Button>
                </div>
              ))}
            </div>
          </div>

          <div className="card setting-group">
            <h3>友情链接</h3>
            <div className="sub">本项目使用的开放协议与社区资源</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              {links.map((link) => (
                <button
                  key={link.url}
                  className="menu-item"
                  style={{ height: 34 }}
                  onClick={() => void api.app.openExternal(link.url)}
                >
                  {link.icon}
                  {link.label}
                  <ExternalLink size={13} style={{ marginLeft: 'auto', color: 'var(--faint)' }} />
                </button>
              ))}
            </div>
          </div>

          <div className="card card-pad" style={{ textAlign: 'center' }}>
            <Heart size={22} style={{ color: 'var(--acc3)', margin: '0 auto 8px' }} />
            <div style={{ fontWeight: 650, fontSize: 13 }}>{t('about.thanks')}</div>
            <div style={{ color: 'var(--muted)', fontSize: 11.5, marginTop: 6, lineHeight: 1.6 }}>
              本项目与 Mojang、Microsoft 无隶属关系。Minecraft 是 Mojang Studios 的商标。
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}

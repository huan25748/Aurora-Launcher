import { useEffect, useState } from 'react'
import { Clock, Copy, FolderOpen, MoreHorizontal, Package, Play, Settings2, Trash2, Download } from 'lucide-react'
import type { Account, Instance } from '@shared/types'
import { cn, fileUrl, formatBytes, gradientOf, initials, instanceGradientVars, LOADER_LABEL, timeAgo } from '../api'
import { Dropdown } from './ui'
import forgeLogo from '../assets/loaders/forge-anvil.svg'
import neoforgeLogo from '../assets/loaders/neoforge-transparent.png'
import fabricLogo from '../assets/loaders/fabric.png'
import quiltLogo from '../assets/loaders/quilt.svg'
import vanillaLogo from '../assets/loaders/vanilla-block.png'
import optifineLogo from '../assets/loaders/optifine-leaf.svg'

const LOADER_LOGO: Partial<Record<string, string>> = {
  fabric: fabricLogo,
  forge: forgeLogo,
  neoforge: neoforgeLogo,
  quilt: quiltLogo,
  vanilla: vanillaLogo,
  optifine: optifineLogo
}

const PIXEL_LOGO = new Set(['neoforge'])

/** Unified black loader tile (also used by the download center). */
export function LoaderMark({ loader, className }: { loader: string; className?: string }) {
  const logo = LOADER_LOGO[loader]
  return (
    <div className={cn('loader-icon', className)} data-loader={loader}>
      {logo ? <img src={logo} alt="" className={cn('loader-art', PIXEL_LOGO.has(loader) && 'pixel')} /> : <span className="emoji">🎮</span>}
    </div>
  )
}

/** 3D render or the new high-quality 2D fallback; old skin crops are not rendered. */
function AccountFace({ account, onError }: { account?: Account | null; onError?: () => void }) {
  const avatar =
    account?.avatarVersion === 2 || account?.avatarMode === 'head3d'
      ? account.avatarUrl?.replace(/^http:\/\//i, 'https://')
      : undefined
  if (!avatar || !avatar.startsWith('data:')) return null
  return <img src={avatar} alt="" className={account?.avatarMode === 'face' ? 'face-full face-2d' : 'face-full'} onError={onError} />
}

/** Just the cut-out artwork, without the black tile (hero / selectors). */
export function BareLoaderIcon({ instance, className }: { instance: Instance; className?: string }) {
  const [failed, setFailed] = useState(false)
  useEffect(() => setFailed(false), [instance.loader])
  // Hero/selectors always show the loader glyph; pack covers stay in the list cards.
  const source = LOADER_LOGO[instance.loader]
  if (!source || failed) return <span className={cn('bare-loader-fallback', className)}>{instance.icon || '🌌'}</span>
  return (
    <img
      className={cn('bare-loader', PIXEL_LOGO.has(instance.loader) && 'pixel', className)}
      src={source}
      alt=""
      onError={() => setFailed(true)}
    />
  )
}

/** Pack-provided cover wins; otherwise the official loader logo on black. */
export function LoaderIcon({ instance, className }: { instance: Instance; className?: string }) {
  const [failed, setFailed] = useState(false)
  useEffect(() => setFailed(false), [instance.iconPath])
  const cover = fileUrl(instance.iconPath)
  const logo = LOADER_LOGO[instance.loader]
  return (
    <div className={cn('loader-icon', className)} data-loader={instance.loader}>
      {cover && !failed ? (
        <img className="pack-icon" src={cover} alt="" onError={() => setFailed(true)} />
      ) : logo ? (
        <img src={logo} alt="" className={cn('loader-art', PIXEL_LOGO.has(instance.loader) && 'pixel')} />
      ) : (
        <span className="emoji">{instance.icon || '🌌'}</span>
      )}
    </div>
  )
}

export type InstanceAction = 'play' | 'open' | 'settings' | 'copy' | 'export' | 'export-pack' | 'delete'

export function InstanceCard({
  instance,
  onPlay,
  onOpen,
  onAction
}: {
  instance: Instance
  onPlay: (id: string) => void
  onOpen: (id: string) => void
  onAction: (action: InstanceAction, instance: Instance) => void
}) {
  return (
    <div className="instance-card" style={instanceGradientVars(instance.color)} onClick={() => onOpen(instance.id)}>
      <div className="instance-cover">
        <span className="loader-tag">{LOADER_LABEL[instance.loader] ?? instance.loader}</span>
        <LoaderIcon instance={instance} className="cover-icon" />
        <button
          className="quick-play"
          title="启动游戏"
          onClick={(e) => {
            e.stopPropagation()
            onPlay(instance.id)
          }}
        >
          <Play size={16} fill="currentColor" />
        </button>
      </div>
      <div className="instance-card-body">
        <div className="instance-name">
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{instance.name}</span>
          <Dropdown
            trigger={(toggle) => (
              <button className="more" onClick={toggle} aria-label="更多操作">
                <MoreHorizontal size={16} />
              </button>
            )}
            items={[
              { label: '启动游戏', icon: <Play size={15} />, onClick: () => onAction('play', instance) },
              { label: '打开文件夹', icon: <FolderOpen size={15} />, onClick: () => onAction('open', instance) },
              { label: '实例设置', icon: <Settings2 size={15} />, onClick: () => onAction('settings', instance) },
              { separator: true, label: 'sep', onClick: undefined },
              { label: '复制实例', icon: <Copy size={15} />, onClick: () => onAction('copy', instance) },
              ...(instance.imported ? [] : [{ label: '导出实例', icon: <Download size={15} />, onClick: () => onAction('export', instance as Instance) }]),
              { label: '导出整合包', icon: <Package size={15} />, onClick: () => onAction('export-pack', instance) },
              { label: '删除实例', icon: <Trash2 size={15} />, danger: true, onClick: () => onAction('delete', instance) }
            ]}
          />
        </div>
        <div className="instance-tags">
          <span className="tag acc">{instance.mcVersion || instance.versionId || '未知版本'}</span>
          {instance.imported && <span className="tag">导入</span>}
          {instance.loaderVersion && <span className="tag">{instance.loaderVersion}</span>}
          {instance.memory ? <span className="tag">{instance.memory} MB</span> : null}
        </div>
        <div className="instance-foot">
          <Clock size={12} />
          {instance.lastPlayedAt ? timeAgo(instance.lastPlayedAt) : '尚未启动'}
        </div>
      </div>
    </div>
  )
}

export function InstanceRow({
  instance,
  onPlay,
  onOpen,
  onAction
}: {
  instance: Instance
  onPlay: (id: string) => void
  onOpen: (id: string) => void
  onAction: (action: InstanceAction, instance: Instance) => void
}) {
  return (
    <div className="instance-row" onClick={() => onOpen(instance.id)}>
      <LoaderIcon instance={instance} className="row-icon" />
      <div className="instance-row-main">
        <div className="instance-row-title">
          <span className="instance-row-name">{instance.name}</span>
          <span className="tag acc">{LOADER_LABEL[instance.loader] ?? instance.loader}</span>
          {instance.imported && <span className="tag">导入</span>}
        </div>
        <div className="instance-row-meta">
          <span>{instance.mcVersion || instance.versionId || '未知版本'}</span>
          {instance.loaderVersion && <span>{LOADER_LABEL[instance.loader] ?? instance.loader} {instance.loaderVersion}</span>}
          {instance.memory ? <span>{instance.memory} MB</span> : null}
          <span className="instance-row-dot" />
          <span className="instance-row-time">
            <Clock size={12} />
            {instance.lastPlayedAt ? timeAgo(instance.lastPlayedAt) : '尚未启动'}
          </span>
        </div>
      </div>
      <div className="instance-row-actions">
        <button
          className="row-play"
          title="启动游戏"
          onClick={(e) => {
            e.stopPropagation()
            onPlay(instance.id)
          }}
        >
          <Play size={16} fill="currentColor" />
        </button>
        <Dropdown
          trigger={(toggle) => (
            <button
              className="row-more"
              onClick={(e) => {
                e.stopPropagation()
                toggle()
              }}
              aria-label="更多操作"
            >
              <MoreHorizontal size={17} />
            </button>
          )}
          items={[
            { label: '启动游戏', icon: <Play size={15} />, onClick: () => onAction('play', instance) },
            { label: '打开文件夹', icon: <FolderOpen size={15} />, onClick: () => onAction('open', instance) },
            { label: '实例设置', icon: <Settings2 size={15} />, onClick: () => onAction('settings', instance) },
            { separator: true, label: 'sep', onClick: undefined },
            { label: '复制实例', icon: <Copy size={15} />, onClick: () => onAction('copy', instance) },
            ...(instance.imported ? [] : [{ label: '导出实例', icon: <Download size={15} />, onClick: () => onAction('export', instance as Instance) }]),
            { label: '导出整合包', icon: <Package size={15} />, onClick: () => onAction('export-pack', instance) },
            { label: '删除实例', icon: <Trash2 size={15} />, danger: true, onClick: () => onAction('delete', instance) }
          ]}
        />
      </div>
    </div>
  )
}

export function AccountAvatar({ account, size, className }: { account?: Account | null; size?: 'sm' | 'lg'; className?: string }) {
  const name = account?.username ?? '?'
  const skin =
    account?.avatarVersion === 2 || account?.avatarMode === 'head3d'
      ? account.avatarUrl?.replace(/^http:\/\//i, 'https://')
      : undefined
  const [a, b] = account ? gradientOf(hashColor(account.uuid || name)) : ['#5eead4', '#818cf8']
  const [skinFailed, setSkinFailed] = useState(false)
  useEffect(() => setSkinFailed(false), [skin])
  return (
    <div className={cn('avatar', size, className)} style={{ background: `linear-gradient(135deg, ${a}, ${b})` }} title={name}>
      {skin && !skinFailed ? (
        <AccountFace account={account} onError={() => setSkinFailed(true)} />
      ) : (
        <span style={{ fontSize: size === 'lg' ? 20 : size === 'sm' ? 12 : 16 }}>{initials(name)}</span>
      )}
    </div>
  )
}

const COLORS = ['aurora', 'sunset', 'ocean', 'forest', 'candy', 'ember', 'violet', 'mint']

function hashColor(seed: string): string {
  let h = 0
  for (let i = 0; i < seed.length; i++) h = (h * 31 + seed.charCodeAt(i)) >>> 0
  return COLORS[h % COLORS.length]
}

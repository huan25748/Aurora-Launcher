import { useEffect, useMemo, useState } from 'react'
import { Copy, Download, FolderOpen, Plus, Search, Trash2, Upload } from 'lucide-react'
import type { Instance, InstalledVersion } from '@shared/types'
import { api, errorMessage, formatBytes, instanceGradientVars, LOADER_LABEL, GRADIENTS } from '../api'
import { useStore, useT } from '../store'
import { InstanceRow, type InstanceAction } from '../components/cards'
import { Button, ConfirmDialog, EmptyState, Field, Input, Modal, Segmented, Select, Switch } from '../components/ui'

const EMOJIS = ['🌌', '🧊', '🧵', '🧶', '🔨', '⚙️', '🌿', '🔥', '⚡', '🌊', '🍀', '🌸', '🪐', '🦊', '🐲', '🍉', '🎨', '🛠️']
const COLORS = Object.keys(GRADIENTS)

export function InstancesPage() {
  const t = useT()
  const instances = useStore((s) => s.instances)
  const navigate = useStore((s) => s.navigate)
  const toast = useStore((s) => s.toast)
  const refreshInstances = useStore((s) => s.refreshInstances)

  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<'all' | 'vanilla' | 'modded'>('all')
  const [createOpen, setCreateOpen] = useState(false)
  const [importingExisting, setImportingExisting] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<Instance | null>(null)
  const [deleteFiles, setDeleteFiles] = useState(true)
  const [copyTarget, setCopyTarget] = useState<Instance | null>(null)
  const [copyName, setCopyName] = useState('')

  const filtered = useMemo(() => {
    let list = instances
    if (filter === 'vanilla') list = list.filter((i) => i.loader === 'vanilla' || i.loader === 'optifine')
    if (filter === 'modded') list = list.filter((i) => i.loader !== 'vanilla' && i.loader !== 'optifine')
    if (query.trim()) {
      const q = query.trim().toLowerCase()
      list = list.filter((i) => i.name.toLowerCase().includes(q) || i.mcVersion.toLowerCase().includes(q) || i.versionId.toLowerCase().includes(q))
    }
    return list
  }, [instances, filter, query])

  const play = async (id: string): Promise<void> => {
    const result = await api.launch.start({ instanceId: id })
    if (!result.ok) toast('error', result.error ?? '启动失败', '启动失败')
  }

  const onAction = (action: InstanceAction, inst: Instance): void => {
    switch (action) {
      case 'play':
        void play(inst.id)
        break
      case 'open':
        void api.instances.openFolder(inst.id)
        break
      case 'settings':
        navigate({ name: 'instance', id: inst.id, extra: 'settings' })
        break
      case 'copy':
        setCopyTarget(inst)
        setCopyName(`${inst.name} 副本`)
        break
      case 'export':
        void api.instances
          .export(inst.id)
          .then((r) => toast('success', `已导出到 ${r.path}`, '导出完成'))
          .catch((e) => toast('error', errorMessage(e)))
        break
      case 'export-pack':
        void api.instances
          .exportPack(inst.id)
          .then((r) => toast('success', `已导出到 ${r.path}`, '整合包已导出'))
          .catch((e) => toast('error', errorMessage(e)))
        break
      case 'delete':
        setDeleteFiles(true)
        setDeleteTarget(inst)
        break
    }
  }

  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1>{t('instances.title')}</h1>
          <p>{t('instances.subtitle')}</p>
        </div>
        <div className="page-actions">
          <Button
            icon={<FolderOpen size={16} />}
            loading={importingExisting}
            onClick={() => {
              setImportingExisting(true)
              void api.instances
                .importExisting()
                .then(async (count) => {
                  await refreshInstances()
                  toast(count ? 'success' : 'info', count ? `已从 .minecraft 导入 ${count} 个版本` : '没有发现可导入的新版本')
                })
                .catch((e) => toast('error', errorMessage(e)))
                .finally(() => setImportingExisting(false))
            }}
          >
            导入 .minecraft 版本
          </Button>
          <Button
            icon={<Upload size={16} />}
            onClick={() =>
              void api.instances
                .import()
                .then((inst) => {
                  if (inst) {
                    void refreshInstances()
                    toast('success', `已导入 ${inst.name}`)
                  }
                })
                .catch((e) => toast('error', errorMessage(e)))
            }
          >
            导入整合包 / 压缩包
          </Button>
          <Button variant="primary" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
            {t('instances.new')}
          </Button>
        </div>
      </div>

      <div className="dl-filter" style={{ marginTop: 4 }}>
        <div className="input-with-icon" style={{ flex: 1, maxWidth: 320 }}>
          <Search />
          <input className="input search" placeholder={t('instances.search')} value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <Segmented
          value={filter}
          onChange={setFilter}
          options={[
            { value: 'all', label: `全部 ${instances.length}` },
            { value: 'vanilla', label: '原版' },
            { value: 'modded', label: '模组' }
          ]}
        />
      </div>

      {filtered.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={<Plus />}
            title={instances.length ? '没有匹配的实例' : t('instances.empty')}
            description={instances.length ? '换个关键词或筛选条件试试。' : '安装游戏版本后，启动器会自动为你创建实例。'}
            action={
              <Button variant="primary" icon={<Download size={16} />} onClick={() => navigate({ name: 'download' })}>
                前往下载中心
              </Button>
            }
          />
        </div>
      ) : (
        <div className="instance-list">
          {filtered.map((inst) => (
            <InstanceRow
              key={inst.id}
              instance={inst}
              onPlay={(id) => void play(id)}
              onOpen={(id) => navigate({ name: 'instance', id })}
              onAction={onAction}
            />
          ))}
        </div>
      )}

      <CreateInstanceModal open={createOpen} onClose={() => setCreateOpen(false)} onCreated={(id) => navigate({ name: 'instance', id })} />

      <ConfirmDialog
        open={!!deleteTarget}
        title={`删除实例「${deleteTarget?.name ?? ''}」`}
        message={
          <div>
            <p>此操作不可撤销。导入实例会一并删除其对应的版本文件夹；共享版本和 .minecraft 根目录不会被删除。</p>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 14 }}>
              <Switch checked={deleteFiles} onChange={setDeleteFiles} />
              <span>同时删除实例文件夹（模组、存档等）</span>
            </div>
          </div>
        }
        confirmText="删除"
        danger
        onClose={() => setDeleteTarget(null)}
        onConfirm={() => {
          if (!deleteTarget) return
          void api.instances
            .remove(deleteTarget.id, deleteFiles)
            .then(() => {
              toast('success', '实例已删除')
              void refreshInstances()
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />

      <Modal
        open={!!copyTarget}
        onClose={() => setCopyTarget(null)}
        title="复制实例"
        footer={
          <>
            <Button variant="ghost" onClick={() => setCopyTarget(null)}>
              取消
            </Button>
            <Button
              variant="primary"
              icon={<Copy size={16} />}
              onClick={() => {
                if (!copyTarget) return
                void api.instances
                  .copy(copyTarget.id, copyName)
                  .then(() => {
                    toast('success', '实例已复制')
                    setCopyTarget(null)
                    void refreshInstances()
                  })
                  .catch((e) => toast('error', errorMessage(e)))
              }}
            >
              复制
            </Button>
          </>
        }
      >
        <Field label="新实例名称">
          <Input value={copyName} onChange={(e) => setCopyName(e.target.value)} autoFocus />
        </Field>
        <div style={{ color: 'var(--muted)', fontSize: 12 }}>将复制实例的全部文件与设置，可能需要一些时间。</div>
      </Modal>
    </div>
  )
}

function CreateInstanceModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: string) => void }) {
  const t = useT()
  const toast = useStore((s) => s.toast)
  const refreshInstances = useStore((s) => s.refreshInstances)
  const navigate = useStore((s) => s.navigate)
  const [versions, setVersions] = useState<InstalledVersion[] | null>(null)
  const [name, setName] = useState('')
  const [versionId, setVersionId] = useState('')
  const [icon, setIcon] = useState('🌌')
  const [color, setColor] = useState('aurora')
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!open) return
    setVersions(null)
    api.versions
      .installed()
      .then((list) => {
        setVersions(list)
        if (list.length) {
          const first = list[0]
          setVersionId(first.id)
          setName(first.mcVersion ?? first.id)
        }
      })
      .catch(() => setVersions([]))
  }, [open])

  const create = async (): Promise<void> => {
    const version = versions?.find((v) => v.id === versionId)
    if (!version) return
    setLoading(true)
    try {
      const inst = await api.instances.create({
        name: name.trim() || version.mcVersion || version.id,
        versionId: version.id,
        mcVersion: version.mcVersion ?? version.id,
        loader: version.loader ?? 'vanilla',
        loaderVersion: version.loaderVersion,
        icon,
        color
      })
      await refreshInstances()
      toast('success', `实例「${inst.name}」已创建`)
      onClose()
      onCreated(inst.id)
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('instances.new')}
      subtitle="使用已安装的游戏版本创建独立实例"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" loading={loading} disabled={!versions?.length || !versionId} onClick={() => void create()}>
            创建实例
          </Button>
        </>
      }
    >
      {versions === null ? (
        <div style={{ color: 'var(--muted)', fontSize: 12 }}>正在读取已安装版本…</div>
      ) : versions.length === 0 ? (
        <div>
          <EmptyState
            icon={<Download />}
            title="还没有已安装的版本"
            description="请先在下载中心安装一个游戏版本，安装完成后会自动创建实例。"
            action={
              <Button
                variant="primary"
                onClick={() => {
                  onClose()
                  navigate({ name: 'download' })
                }}
              >
                前往下载中心
              </Button>
            }
          />
        </div>
      ) : (
        <>
          <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start' }}>
            <div
              style={{
                width: 84,
                height: 84,
                borderRadius: 24,
                display: 'grid',
                placeItems: 'center',
                fontSize: 38,
                flex: 'none',
                ...instanceGradientVars(color)
              }}
            >
              <div style={{ width: '100%', height: '100%', borderRadius: 24, display: 'grid', placeItems: 'center', background: 'linear-gradient(135deg, var(--c1), var(--c2) 60%, var(--c3))' }}>
                {icon}
              </div>
            </div>
            <div style={{ flex: 1 }}>
              <Field label="实例名称" style={{ marginBottom: 12 }}>
                <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="例如：生存 1.20.1" />
              </Field>
              <Field label="游戏版本" style={{ marginBottom: 0 }}>
                <Select
                  value={versionId}
                  onChange={(e) => {
                    setVersionId(e.target.value)
                    const v = versions.find((x) => x.id === e.target.value)
                    if (v && !name.trim()) setName(v.mcVersion ?? v.id)
                  }}
                >
                  {versions.map((v) => (
                    <option key={v.id} value={v.id}>
                      {v.mcVersion ?? v.id} · {LOADER_LABEL[v.loader ?? 'vanilla'] ?? v.loader}
                      {v.loaderVersion ? ` ${v.loaderVersion}` : ''}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
          </div>

          <div className="divider" />

          <Field label="图标">
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {EMOJIS.map((e) => (
                <button
                  key={e}
                  onClick={() => setIcon(e)}
                  style={{
                    width: 36,
                    height: 36,
                    borderRadius: 10,
                    fontSize: 18,
                    background: icon === e ? 'var(--panel-hover)' : 'transparent',
                    border: `1px solid ${icon === e ? 'var(--acc2)' : 'var(--line)'}`
                  }}
                >
                  {e}
                </button>
              ))}
            </div>
          </Field>

          <Field label="主题色">
            <div className="accent-swatches">
              {COLORS.map((c) => {
                const [a, b] = GRADIENTS[c]
                return (
                  <button
                    key={c}
                    className={`swatch ${color === c ? 'active' : ''}`}
                    style={{ background: `linear-gradient(135deg, ${a}, ${b})` }}
                    onClick={() => setColor(c)}
                  />
                )
              })}
            </div>
          </Field>
        </>
      )}
    </Modal>
  )
}

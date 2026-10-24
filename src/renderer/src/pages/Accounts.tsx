import { useEffect, useRef, useState } from 'react'
import { Check, Copy, ExternalLink, KeyRound, LogIn, Plus, RefreshCw, Trash2, UserPlus, Users } from 'lucide-react'
import type { Account, DeviceCodeInfo } from '@shared/types'
import { api, cn, errorMessage, formatDate } from '../api'
import { useStore, useT } from '../store'
import { AccountAvatar } from '../components/cards'
import { Badge, Button, ConfirmDialog, EmptyState, Field, Input, Modal } from '../components/ui'

type AddTab = 'offline' | 'microsoft' | 'authlib'

const TYPE_LABEL: Record<Account['type'], string> = {
  offline: '离线',
  microsoft: '微软',
  authlib: '第三方'
}

export function AccountsPage() {
  const t = useT()
  const accounts = useStore((s) => s.accounts)
  const activeAccount = useStore((s) => s.activeAccount)
  const refreshAccounts = useStore((s) => s.refreshAccounts)
  const toast = useStore((s) => s.toast)
  const patchSettings = useStore((s) => s.patchSettings)
  const navigate = useStore((s) => s.navigate)
  const [addOpen, setAddOpen] = useState(false)
  const [tab, setTab] = useState<AddTab>('offline')
  const [offlineName, setOfflineName] = useState('')
  const [authServer, setAuthServer] = useState('')
  const [authUser, setAuthUser] = useState('')
  const [authPass, setAuthPass] = useState('')
  const [loading, setLoading] = useState(false)
  const [flow, setFlow] = useState<DeviceCodeInfo | null>(null)
  const [pollState, setPollState] = useState<'idle' | 'pending' | 'error' | 'expired'>('idle')
  const [pollMessage, setPollMessage] = useState('')
  const [removeId, setRemoveId] = useState<string | null>(null)
  const pollTimer = useRef<number | null>(null)

  useEffect(() => {
    return () => {
      if (pollTimer.current) window.clearTimeout(pollTimer.current)
    }
  }, [])

  const startMicrosoft = async (): Promise<void> => {
    setLoading(true)
    try {
      const info = await api.accounts.startMicrosoft()
      setFlow(info)
      setPollState('pending')
      setPollMessage('等待你在浏览器中完成授权…')
      void navigator.clipboard?.writeText(info.userCode).catch(() => {})
      schedulePoll(info, info.interval)
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  const schedulePoll = (info: DeviceCodeInfo, seconds: number): void => {
    if (pollTimer.current) window.clearTimeout(pollTimer.current)
    pollTimer.current = window.setTimeout(async () => {
      try {
        const result = await api.accounts.pollMicrosoft(info.flowId)
        if (result.status === 'success') {
          setPollState('idle')
          setFlow(null)
          setAddOpen(false)
          await refreshAccounts()
          toast('success', `已登录 ${result.account?.username ?? ''}`, '微软登录成功')
          return
        }
        if (result.status === 'pending') {
          setPollState('pending')
          setPollMessage('等待你在浏览器中完成授权…')
          schedulePoll(info, info.interval)
          return
        }
        setPollState(result.status === 'expired' ? 'expired' : 'error')
        setPollMessage(result.message ?? '登录失败')
      } catch (e) {
        setPollState('error')
        setPollMessage(errorMessage(e))
      }
    }, Math.max(2, seconds) * 1000)
  }

  const cancelFlow = async (): Promise<void> => {
    if (pollTimer.current) window.clearTimeout(pollTimer.current)
    if (flow) await api.accounts.cancelMicrosoft(flow.flowId).catch(() => {})
    setFlow(null)
    setPollState('idle')
  }

  const addOffline = async (): Promise<void> => {
    setLoading(true)
    try {
      const acc = await api.accounts.addOffline(offlineName)
      await refreshAccounts()
      await api.accounts.setActive(acc.id)
      await refreshAccounts()
      toast('success', `已添加离线账户 ${acc.username}`)
      setAddOpen(false)
      setOfflineName('')
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  const addAuthlib = async (): Promise<void> => {
    setLoading(true)
    try {
      const acc = await api.accounts.addAuthlib(authServer, authUser, authPass)
      await refreshAccounts()
      await api.accounts.setActive(acc.id)
      await refreshAccounts()
      toast('success', `已登录 ${acc.username}`)
      setAddOpen(false)
      setAuthPass('')
    } catch (e) {
      toast('error', errorMessage(e))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="page-enter">
      <div className="page-head">
        <div className="titles">
          <h1>{t('accounts.title')}</h1>
          <p>{t('accounts.subtitle')}</p>
        </div>
        <div className="page-actions">
          <Button variant="primary" icon={<UserPlus size={16} />} onClick={() => setAddOpen(true)}>
            {t('accounts.add')}
          </Button>
        </div>
      </div>

      {accounts.length === 0 ? (
        <div className="card">
          <EmptyState
            icon={<Users />}
            title="还没有账户"
            description="添加离线账户即可开始游戏；正版用户可以使用微软账户登录以访问皮肤与服务器。"
            action={
              <Button variant="primary" icon={<Plus size={16} />} onClick={() => setAddOpen(true)}>
                添加账户
              </Button>
            }
          />
        </div>
      ) : (
        <div className="account-list">
          {accounts.map((acc) => {
            const active = activeAccount?.id === acc.id
            return (
              <div className={cn('account-row', active && 'active')} key={acc.id}>
                <AccountAvatar account={acc} size="lg" />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div className="account-name">
                    {acc.username}
                    <Badge tone={acc.type === 'microsoft' ? 'acc' : undefined}>{TYPE_LABEL[acc.type]}</Badge>
                    {active && (
                      <Badge tone="ok">
                        <Check size={10} /> {t('accounts.active')}
                      </Badge>
                    )}
                  </div>
                  <div className="account-uuid">{acc.uuid}</div>
                  <div style={{ color: 'var(--faint)', fontSize: 10.5, marginTop: 3 }}>
                    {acc.authServerName ?? (acc.type === 'offline' ? '本地离线账户' : 'Microsoft 账户')}
                    {acc.createdAt ? ` · 添加于 ${formatDate(acc.createdAt)}` : ''}
                  </div>
                </div>
                <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                  {!active && (
                    <Button
                      size="sm"
                      variant="soft"
                      onClick={() =>
                        void api.accounts
                          .setActive(acc.id)
                          .then(() => refreshAccounts())
                          .catch((e) => toast('error', errorMessage(e)))
                      }
                    >
                      {t('accounts.setActive')}
                    </Button>
                  )}
                  {acc.type === 'microsoft' && (
                    <button
                      className="icon-btn"
                      title="刷新令牌"
                      onClick={() =>
                        void api.accounts
                          .refresh(acc.id)
                          .then(() => {
                            toast('success', '令牌已刷新')
                            return refreshAccounts()
                          })
                          .catch((e) => toast('error', errorMessage(e)))
                      }
                    >
                      <RefreshCw />
                    </button>
                  )}
                  <button className="icon-btn danger" title="删除账户" onClick={() => setRemoveId(acc.id)}>
                    <Trash2 />
                  </button>
                </div>
              </div>
            )
          })}
        </div>
      )}

      <div className="card card-pad" style={{ marginTop: 20, display: 'flex', gap: 14, alignItems: 'flex-start' }}>
        <KeyRound size={20} style={{ color: 'var(--acc1)', flex: 'none', marginTop: 2 }} />
        <div style={{ fontSize: 12, color: 'var(--muted)', lineHeight: 1.7 }}>
          <strong style={{ color: 'var(--text-soft)' }}>关于账户安全：</strong>
          账户令牌仅保存在本机的启动器配置目录中，不会上传到任何服务器。微软登录使用官方 OAuth 设备代码流程，启动器不会接触你的密码。
          如需使用自己的 Azure 应用，可在
          <button
            style={{ color: 'var(--acc1)', padding: '0 3px' }}
            onClick={() => {
              useStore.getState().setSettingsSection('advanced')
              navigate({ name: 'settings' })
            }}
          >
            设置 → 高级
          </button>
          中修改 Client ID。
        </div>
      </div>

      {/* Add account modal */}
      <Modal
        open={addOpen}
        onClose={() => {
          setAddOpen(false)
          void cancelFlow()
        }}
        title={t('accounts.add')}
        subtitle="选择账户类型"
        footer={
          <>
            <Button variant="ghost" onClick={() => setAddOpen(false)}>
              取消
            </Button>
            {tab === 'offline' && (
              <Button variant="primary" loading={loading} disabled={!offlineName.trim()} onClick={() => void addOffline()}>
                添加离线账户
              </Button>
            )}
            {tab === 'microsoft' && (
              <Button variant="primary" icon={<LogIn size={16} />} loading={loading} disabled={!!flow} onClick={() => void startMicrosoft()}>
                开始微软登录
              </Button>
            )}
            {tab === 'authlib' && (
              <Button variant="primary" loading={loading} disabled={!authServer || !authUser || !authPass} onClick={() => void addAuthlib()}>
                登录
              </Button>
            )}
          </>
        }
      >
        <div className="segmented" style={{ marginBottom: 18 }}>
          {[
            { key: 'offline', label: '离线账户' },
            { key: 'microsoft', label: '微软账户' },
            { key: 'authlib', label: '第三方验证' }
          ].map((item) => (
            <button
              key={item.key}
              className={cn(tab === item.key && 'active')}
              onClick={() => {
                setTab(item.key as AddTab)
                if (item.key !== 'microsoft') void cancelFlow()
              }}
            >
              {item.label}
            </button>
          ))}
        </div>

        {tab === 'offline' && (
          <>
            <Field label="用户名" hint="1-16 个字符，仅限字母、数字与下划线。离线账户只能进入离线服务器。">
              <Input value={offlineName} onChange={(e) => setOfflineName(e.target.value)} placeholder="Steve" autoFocus onKeyDown={(e) => e.key === 'Enter' && void addOffline()} />
            </Field>
          </>
        )}

        {tab === 'microsoft' && (
          <>
            {!flow && (
              <>
                <div style={{ fontSize: 12.5, color: 'var(--text-soft)', lineHeight: 1.75 }}>
                  点击下方按钮获取设备代码，然后在浏览器中打开微软登录页面并输入代码即可完成授权。授权后启动器会自动保存账户。
                </div>
                <div className="login-steps">
                  {['打开登录页面并输入设备代码', '使用微软账户完成授权', '返回启动器，账户将自动添加'].map((step, i) => (
                    <div className="login-step" key={step}>
                      <span className="step-num">{i + 1}</span>
                      {step}
                    </div>
                  ))}
                </div>
              </>
            )}
            {flow && (
              <>
                <div className="device-code">{flow.userCode}</div>
                <div style={{ display: 'flex', gap: 8, marginTop: 12, justifyContent: 'center' }}>
                  <Button size="sm" icon={<ExternalLink size={14} />} onClick={() => void api.app.openExternal(flow.verificationUri)}>
                    打开登录页面
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    icon={<Copy size={14} />}
                    onClick={() => {
                      void navigator.clipboard?.writeText(flow.userCode)
                      toast('success', '设备代码已复制')
                    }}
                  >
                    复制代码
                  </Button>
                </div>
                <div style={{ marginTop: 18, textAlign: 'center', color: pollState === 'error' || pollState === 'expired' ? 'var(--danger)' : 'var(--muted)', fontSize: 12.5 }}>
                  {pollState === 'pending' ? (
                    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                      <span className="spinner" style={{ width: 14, height: 14 }} /> {pollMessage}
                    </span>
                  ) : (
                    pollMessage
                  )}
                </div>
              </>
            )}
          </>
        )}

        {tab === 'authlib' && (
          <>
            <Field label="验证服务器地址" hint="支持任意 authlib-injector 兼容的 Yggdrasil 服务，例如 https://littleskin.cn/api/yggdrasil">
              <Input value={authServer} onChange={(e) => setAuthServer(e.target.value)} placeholder="https://example.com/api/yggdrasil" />
            </Field>
            <Field label="用户名 / 邮箱">
              <Input value={authUser} onChange={(e) => setAuthUser(e.target.value)} />
            </Field>
            <Field label="密码" hint="密码只用于向验证服务器换取令牌，不会被保存。">
              <Input type="password" value={authPass} onChange={(e) => setAuthPass(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && void addAuthlib()} />
            </Field>
          </>
        )}
      </Modal>

      <ConfirmDialog
        open={!!removeId}
        title="删除账户"
        message="确定要删除这个账户吗？已保存的登录令牌会一并移除。"
        danger
        confirmText="删除"
        onClose={() => setRemoveId(null)}
        onConfirm={() => {
          if (!removeId) return
          void api.accounts
            .remove(removeId)
            .then(() => {
              toast('success', '账户已删除')
              return refreshAccounts()
            })
            .catch((e) => toast('error', errorMessage(e)))
        }}
      />
    </div>
  )
}

import { Download, ExternalLink } from 'lucide-react'
import { api } from '../api'
import { useStore } from '../store'
import { Badge, Button, Modal } from './ui'

export function UpdateDialog() {
  const info = useStore((s) => s.updateInfo)
  const setUpdateInfo = useStore((s) => s.setUpdateInfo)
  if (!info) return null
  const close = (): void => setUpdateInfo(null)
  return (
    <Modal
      open
      onClose={close}
      title="发现新版本"
      subtitle={`${info.currentVersion} → ${info.latestVersion}`}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            稍后
          </Button>
          <Button
            variant="primary"
            icon={<Download size={15} />}
            onClick={() => {
              void api.app.openExternal(info.releaseUrl)
              close()
            }}
          >
            前往下载
          </Button>
        </>
      }
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
        <Badge tone="acc">GitHub Releases</Badge>
        <span style={{ color: 'var(--faint)', fontSize: 11.5 }}>{info.title}</span>
      </div>
      {info.notes ? (
        <pre
          style={{
            margin: 0,
            maxHeight: 260,
            overflow: 'auto',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            fontSize: 12,
            lineHeight: 1.65,
            color: 'var(--text-soft)',
            userSelect: 'text'
          }}
        >
          {info.notes.slice(0, 8000)}
        </pre>
      ) : (
        <p style={{ margin: 0, color: 'var(--muted)', fontSize: 12.5 }}>新版本已经发布，点击「前往下载」查看更新内容。</p>
      )}
      <button
        className="menu-item"
        style={{ marginTop: 14, paddingLeft: 0 }}
        onClick={() => {
          void api.app.openExternal('https://github.com/huan25748/Aurora-Launcher/releases')
          close()
        }}
      >
        <ExternalLink size={14} /> 在浏览器中打开 Releases
      </button>
    </Modal>
  )
}

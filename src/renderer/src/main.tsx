import React from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import './styles/global.css'

const LOG_HINT = '%APPDATA%\\AuroraLauncher\\startup.log'

function FatalPanel({ title, detail }: { title: string; detail: string }) {
  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'grid',
        placeItems: 'center',
        padding: 32,
        background: 'radial-gradient(900px 600px at 80% 0%, #1b2a46 0%, #080b12 55%, #05070c 100%)',
        color: '#e9eefb',
        fontFamily: '"Segoe UI", "Microsoft YaHei UI", sans-serif'
      }}
    >
      <div
        style={{
          width: 'min(680px, 100%)',
          background: 'rgba(17, 24, 38, 0.9)',
          border: '1px solid rgba(255,255,255,0.12)',
          borderRadius: 22,
          padding: 28,
          boxShadow: '0 30px 80px -30px rgba(0,0,0,0.8)'
        }}
      >
        <div style={{ fontSize: 12, letterSpacing: '0.18em', textTransform: 'uppercase', color: '#8592a8', fontWeight: 700 }}>
          Aurora Launcher
        </div>
        <h1 style={{ margin: '10px 0 8px', fontSize: 24 }}>{title}</h1>
        <p style={{ color: '#b6c1d4', fontSize: 13, lineHeight: 1.7, margin: 0 }}>{detail}</p>
        <div
          style={{
            marginTop: 18,
            padding: 14,
            borderRadius: 12,
            background: 'rgba(255,255,255,0.04)',
            border: '1px solid rgba(255,255,255,0.08)',
            fontFamily: 'Consolas, monospace',
            fontSize: 11.5,
            color: '#8592a8',
            wordBreak: 'break-all',
            userSelect: 'text'
          }}
        >
          诊断日志：{LOG_HINT}
          <br />
          如果使用单文件版，请确认 exe 完整未被杀毒软件隔离；也可尝试在 exe 旁创建 disable-gpu.txt 后重新启动。
        </div>
        <div style={{ display: 'flex', gap: 10, marginTop: 20 }}>
          <button
            onClick={() => location.reload()}
            style={{
              height: 40,
              padding: '0 20px',
              borderRadius: 11,
              border: 'none',
              background: 'linear-gradient(120deg, #5eead4, #818cf8)',
              color: '#061018',
              fontWeight: 700,
              cursor: 'pointer'
            }}
          >
            重新加载
          </button>
          <button
            onClick={() => void navigator.clipboard?.writeText(LOG_HINT)}
            style={{
              height: 40,
              padding: '0 20px',
              borderRadius: 11,
              border: '1px solid rgba(255,255,255,0.14)',
              background: 'transparent',
              color: '#e9eefb',
              cursor: 'pointer'
            }}
          >
            复制日志路径
          </button>
        </div>
      </div>
    </div>
  )
}

class ErrorBoundary extends React.Component<{ children: React.ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null }

  static getDerivedStateFromError(error: Error): { error: Error } {
    return { error }
  }

  componentDidCatch(error: Error, info: React.ErrorInfo): void {
    console.error('Renderer crashed:', error, info)
  }

  render(): React.ReactNode {
    if (this.state.error) {
      return <FatalPanel title="界面发生错误" detail={`${this.state.error.name}: ${this.state.error.message}`} />
    }
    return this.props.children
  }
}

const rootElement = document.getElementById('root') as HTMLElement

if (!window.aurora) {
  rootElement.innerHTML = ''
  createRoot(rootElement).render(
    <FatalPanel
      title="启动器初始化失败"
      detail="预加载脚本（preload）没有正确加载，因此界面无法访问启动器功能。这通常意味着安装文件不完整。"
    />
  )
} else {
  try {
    createRoot(rootElement).render(
      <React.StrictMode>
        <ErrorBoundary>
          <App />
        </ErrorBoundary>
      </React.StrictMode>
    )
  } catch (error) {
    rootElement.innerHTML = ''
    const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error)
    createRoot(rootElement).render(<FatalPanel title="启动器启动失败" detail={message} />)
  }
}

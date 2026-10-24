import React from 'react'
import { Button } from './ui'

export class PageErrorBoundary extends React.Component<{ children: React.ReactNode; onReset?: () => void }, { error: Error | null }> {
  state: { error: Error | null } = { error: null }

  static getDerivedStateFromError(error: Error): { error: Error } {
    return { error }
  }

  componentDidCatch(error: Error, info: React.ErrorInfo): void {
    console.error('Page crashed:', error, info)
  }

  private retry = (): void => {
    this.setState({ error: null })
  }

  render(): React.ReactNode {
    if (!this.state.error) return this.props.children
    return (
      <div className="card" style={{ margin: '24px 4px', padding: 28 }}>
        <div style={{ fontSize: 18, fontWeight: 700, color: 'var(--danger)' }}>这个页面出错了</div>
        <div style={{ color: 'var(--muted)', fontSize: 12.5, lineHeight: 1.7, marginTop: 8, userSelect: 'text' }}>
          {this.state.error.name}: {this.state.error.message}
        </div>
        <div style={{ color: 'var(--faint)', fontSize: 11.5, marginTop: 10, lineHeight: 1.6 }}>
          其他页面仍可正常使用。你可以重试，或返回首页后重新操作；如果问题反复出现，请查看启动日志。
        </div>
        <div style={{ display: 'flex', gap: 10, marginTop: 18 }}>
          <Button variant="primary" onClick={this.retry}>
            重试
          </Button>
          <Button variant="ghost" onClick={() => this.props.onReset?.()}>
            返回首页
          </Button>
        </div>
      </div>
    )
  }
}

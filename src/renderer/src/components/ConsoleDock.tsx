import { useEffect, useRef } from 'react'
import { ChevronDown, ChevronUp, FolderOpen, Trash2, Square, Terminal } from 'lucide-react'
import { useStore } from '../store'
import { cn, formatBytes } from '../api'
import { Button, ProgressBar } from './ui'
import type { ProgressGroup } from '@shared/types'

function timeText(ts: number): string {
  const d = new Date(ts)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`
}

function GroupCard({ group }: { group: ProgressGroup }) {
  const running = group.status === 'running'
  const current = group.steps.find((s) => s.status === 'running') ?? group.steps[group.steps.length - 1]
  return (
    <div className={cn('task-group', group.status)}>
      <div className="task-group-head">
        <span style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
          {running ? <span className="spinner" style={{ width: 12, height: 12 }} /> : group.status === 'done' ? <span style={{ color: 'var(--success)' }}>✓</span> : <span style={{ color: 'var(--danger)' }}>✕</span>}
          {group.title}
        </span>
        <span style={{ color: 'var(--faint)', fontSize: 10.5 }}>
          {group.status === 'running' ? '进行中' : group.status === 'done' ? '完成' : group.status === 'cancelled' ? '已取消' : '失败'}
        </span>
      </div>
      {group.error && (
        <div style={{ color: 'var(--danger)', fontSize: 10.5, marginTop: 6, lineHeight: 1.5, wordBreak: 'break-word' }}>{group.error}</div>
      )}
      <div className="task-steps">
        {group.steps.slice(-6).map((step) => (
          <div className="task-step" key={step.id}>
            <div className="top">
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{step.title}</span>
              <span>{step.total > 0 ? `${Math.min(100, Math.round((step.current / step.total) * 100))}%` : ''}</span>
            </div>
            <ProgressBar current={step.current} total={step.total} solid={step.status !== 'running'} />
            {step.detail && (
              <div style={{ color: 'var(--faint)', fontSize: 10, marginTop: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {step.detail}
              </div>
            )}
          </div>
        ))}
        {current && group.steps.length === 0 && <div className="task-step">准备中…</div>}
      </div>
    </div>
  )
}

export function ConsoleDock() {
  const open = useStore((s) => s.consoleOpen)
  const setOpen = useStore((s) => s.setConsoleOpen)
  const progress = useStore((s) => s.progress)
  const logs = useStore((s) => s.logs)
  const clearLogs = useStore((s) => s.clearLogs)
  const game = useStore((s) => s.game)
  const navigate = useStore((s) => s.navigate)
  const logRef = useRef<HTMLDivElement>(null)

  const running = progress.groups.find((g) => g.status === 'running')
  const gameRunning = game.state === 'running'

  useEffect(() => {
    if (open && logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight
  }, [logs, open])

  const groups = [...progress.groups].sort((a, b) => {
    if (a.status === 'running' && b.status !== 'running') return -1
    if (b.status === 'running' && a.status !== 'running') return 1
    return b.startedAt - a.startedAt
  })

  return (
    <div className={cn('console-dock', open && 'open')}>
      <div className="console-head" onClick={() => setOpen(!open)}>
        <div className="console-title">
          <Terminal size={15} />
          控制台
          <span className={cn('dot', (running || gameRunning) && 'live')} />
        </div>
        <div className="console-progress">
          {running ? (
            (() => {
              const step = running.steps.find((s) => s.status === 'running') ?? running.steps[running.steps.length - 1]
              if (!step) return null
              return (
                <>
                  <span style={{ fontSize: 11.5, color: 'var(--muted)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: 280 }}>
                    {step.title}
                    {step.detail ? ` · ${step.detail}` : ''}
                  </span>
                  <div style={{ flex: 1, maxWidth: 260 }}>
                    <ProgressBar current={step.current} total={step.total} />
                  </div>
                  <span style={{ fontSize: 10.5, color: 'var(--faint)', whiteSpace: 'nowrap' }}>
                    {step.total > 0 ? `${Math.round((step.current / step.total) * 100)}%` : ''}
                  </span>
                </>
              )
            })()
          ) : gameRunning ? (
            <span style={{ fontSize: 11.5, color: 'var(--muted)' }}>游戏运行中 · PID {game.pid ?? '-'}</span>
          ) : (
            <span style={{ fontSize: 11.5, color: 'var(--faint)' }}>{logs.length ? `${logs.length} 条日志` : '暂无任务'}</span>
          )}
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }} onClick={(e) => e.stopPropagation()}>
          {gameRunning && (
            <Button size="sm" variant="danger" icon={<Square size={13} />} onClick={() => void window.aurora.launch.stop()}>
              停止
            </Button>
          )}
          <button
            className="icon-btn"
            title="打开游戏目录"
            onClick={() => {
              if (game.instanceId) void window.aurora.instances.openFolder(game.instanceId)
            }}
          >
            <FolderOpen />
          </button>
          <button className="icon-btn" title="清空日志" onClick={clearLogs}>
            <Trash2 />
          </button>
          <button className="icon-btn" onClick={() => setOpen(!open)} title={open ? '收起' : '展开'}>
            {open ? <ChevronDown /> : <ChevronUp />}
          </button>
        </div>
      </div>
      <div className="console-body">
        <div className="task-panel">
          {groups.length === 0 && (
            <div style={{ color: 'var(--faint)', fontSize: 11.5, textAlign: 'center', padding: '24px 8px' }}>任务进度会显示在这里</div>
          )}
          {groups.map((g) => (
            <GroupCard key={g.id} group={g} />
          ))}
        </div>
        <div className="log-panel" ref={logRef}>
          {logs.length === 0 && <div style={{ color: 'var(--faint)' }}>等待日志输出…</div>}
          {logs.map((line, i) => (
            <div className={cn('log-line', line.level)} key={`${line.ts}-${i}`}>
              <span className="time">{timeText(line.ts)}</span>
              <span className="src">{line.source === 'game' ? 'GAME' : line.source === 'download' ? 'DL' : line.source === 'auth' ? 'AUTH' : 'APP'}</span>
              <span>{line.text}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}

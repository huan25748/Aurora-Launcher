import { emit } from './bus'
import type { ProgressGroup, ProgressSnapshot, ProgressStep, TaskStatus } from '../../shared/types'
import { uid } from './fsx'

interface GroupInternal extends ProgressGroup {
  controller: AbortController
}

const groups = new Map<string, GroupInternal>()
let broadcastTimer: NodeJS.Timeout | null = null

function prune(): void {
  const now = Date.now()
  for (const [id, g] of groups) {
    if (g.status !== 'running' && g.finishedAt && now - g.finishedAt > 90_000) groups.delete(id)
  }
  // keep max 12 groups
  if (groups.size > 12) {
    const sorted = [...groups.values()].sort((a, b) => a.startedAt - b.startedAt)
    for (const g of sorted.slice(0, groups.size - 12)) groups.delete(g.id)
  }
}

export function snapshot(): ProgressSnapshot {
  const list = [...groups.values()]
    .sort((a, b) => a.startedAt - b.startedAt)
    .map((g) => {
      const { controller, ...rest } = g
      return rest
    })
  const active = [...list].reverse().find((g) => g.status === 'running')
  return { groups: list, activeGroupId: active?.id }
}

function broadcast(): void {
  if (broadcastTimer) return
  broadcastTimer = setTimeout(() => {
    broadcastTimer = null
    emit('progress:update', snapshot())
  }, 120)
}

export class TaskGroupHandle {
  readonly id: string
  readonly controller: AbortController

  constructor(title: string) {
    this.id = uid('task-')
    this.controller = new AbortController()
    groups.set(this.id, {
      id: this.id,
      title,
      steps: [],
      status: 'running',
      startedAt: Date.now(),
      controller: this.controller
    })
    prune()
    broadcast()
  }

  get signal(): AbortSignal {
    return this.controller.signal
  }

  step(title: string, total = 1, detail?: string): TaskStepHandle {
    const id = uid('step-')
    const step: ProgressStep = { id, title, current: 0, total, status: 'pending', detail }
    groups.get(this.id)?.steps.push(step)
    broadcast()
    return new TaskStepHandle(this.id, id)
  }

  finish(status: 'done' | 'error' | 'cancelled' = 'done', error?: string): void {
    const g = groups.get(this.id)
    if (!g) return
    // mark unfinished steps as skipped/cancelled
    for (const s of g.steps) {
      if (s.status === 'pending' || s.status === 'running') {
        s.status = status === 'error' ? 'skipped' : 'cancelled'
        if (s.status === 'skipped') s.current = s.total
      }
    }
    g.status = status
    g.error = error
    g.finishedAt = Date.now()
    broadcast()
  }

  fail(error: unknown): void {
    this.finish('error', error instanceof Error ? error.message : String(error))
  }

  cancel(): void {
    this.controller.abort()
    this.finish('cancelled')
  }

  checkCancelled(): void {
    if (this.controller.signal.aborted) throw new CancelledError()
  }
}

export class TaskStepHandle {
  constructor(
    private groupId: string,
    private stepId: string
  ) {}

  private get step(): ProgressStep | undefined {
    return groups.get(this.groupId)?.steps.find((s) => s.id === this.stepId)
  }

  start(total?: number, detail?: string): this {
    const s = this.step
    if (s) {
      s.status = 'running'
      if (total !== undefined) s.total = total
      if (detail !== undefined) s.detail = detail
    }
    broadcast()
    return this
  }

  progress(current: number, detail?: string, total?: number): this {
    const s = this.step
    if (s) {
      s.current = current
      if (total !== undefined) s.total = total
      if (detail !== undefined) s.detail = detail
      if (s.status === 'pending') s.status = 'running'
    }
    broadcast()
    return this
  }

  bump(detail?: string): this {
    const s = this.step
    if (s) {
      s.current = Math.min(s.total, s.current + 1)
      if (detail !== undefined) s.detail = detail
      if (s.status === 'pending') s.status = 'running'
    }
    broadcast()
    return this
  }

  done(detail?: string): this {
    const s = this.step
    if (s) {
      s.status = 'done'
      s.current = s.total
      if (detail !== undefined) s.detail = detail
    }
    broadcast()
    return this
  }

  fail(detail?: string): this {
    const s = this.step
    if (s) {
      s.status = 'error'
      if (detail !== undefined) s.detail = detail
    }
    broadcast()
    return this
  }

  get current(): ProgressStep | undefined {
    return this.step
  }
}

export class CancelledError extends Error {
  constructor() {
    super('已取消')
    this.name = 'CancelledError'
  }
}

export function cancelGroup(groupId: string): void {
  const g = groups.get(groupId)
  if (g) g.controller.abort()
}

export function cancelAll(): void {
  for (const g of groups.values()) if (g.status === 'running') g.controller.abort()
}

export function clearGroups(): void {
  groups.clear()
  broadcast()
}

export function isCancelledError(err: unknown): boolean {
  return err instanceof CancelledError || (err instanceof Error && (err.name === 'AbortError' || err.message === '已取消'))
}

export function taskStatusOf(err: unknown): TaskStatus {
  return isCancelledError(err) ? 'cancelled' : 'error'
}

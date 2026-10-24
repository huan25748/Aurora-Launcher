import React, { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { AlertTriangle, Check, ChevronDown, Info, X, XCircle } from 'lucide-react'
import { cn } from '../api'
import { useStore } from '../store'

// ---------------------------------------------------------------------------
// Button
// ---------------------------------------------------------------------------

type ButtonVariant = 'primary' | 'soft' | 'ghost' | 'danger' | 'default'

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: 'sm' | 'md' | 'lg'
  icon?: React.ReactNode
  loading?: boolean
  block?: boolean
}

export function Button({ variant = 'default', size = 'md', icon, loading, block, className, children, disabled, ...rest }: ButtonProps) {
  return (
    <button
      className={cn(
        'btn',
        variant === 'primary' && 'btn-primary',
        variant === 'soft' && 'btn-soft',
        variant === 'ghost' && 'btn-ghost',
        variant === 'danger' && 'btn-danger',
        size === 'sm' && 'btn-sm',
        size === 'lg' && 'btn-lg',
        block && 'btn-block',
        className
      )}
      disabled={disabled || loading}
      {...rest}
    >
      {loading ? <span className="spinner" /> : icon}
      {children}
    </button>
  )
}

// ---------------------------------------------------------------------------
// Inputs
// ---------------------------------------------------------------------------

export function Input(props: React.InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={cn('input', props.className)} />
}

export function Textarea(props: React.TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea {...props} className={cn('textarea', props.className)} />
}

export function Select({ children, ...props }: React.SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select {...props} className={cn('select', props.className)}>
      {children}
    </select>
  )
}

export function Field({
  label,
  hint,
  children,
  style
}: {
  label?: React.ReactNode
  hint?: React.ReactNode
  children: React.ReactNode
  style?: React.CSSProperties
}) {
  return (
    <div className="field" style={style}>
      {label && <label>{label}</label>}
      {children}
      {hint && <div className="hint">{hint}</div>}
    </div>
  )
}

export function Switch({ checked, onChange, disabled }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <button
      type="button"
      className={cn('switch', checked && 'on')}
      onClick={() => !disabled && onChange(!checked)}
      disabled={disabled}
      aria-checked={checked}
      role="switch"
    />
  )
}

export function Slider({
  value,
  min,
  max,
  step = 1,
  onChange
}: {
  value: number
  min: number
  max: number
  step?: number
  onChange: (v: number) => void
}) {
  const percent = ((value - min) / Math.max(1, max - min)) * 100
  return (
    <input
      type="range"
      className="slider"
      min={min}
      max={max}
      step={step}
      value={value}
      style={{ ['--fill' as never]: `${percent}%` }}
      onChange={(e) => onChange(Number(e.target.value))}
    />
  )
}

export function Segmented<T extends string>({
  options,
  value,
  onChange
}: {
  options: { value: T; label: React.ReactNode }[]
  value: T
  onChange: (v: T) => void
}) {
  return (
    <div className="segmented">
      {options.map((o) => (
        <button key={o.value} className={cn(o.value === value && 'active')} onClick={() => onChange(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function Badge({ children, tone, className }: { children: React.ReactNode; tone?: 'acc' | 'ok' | 'warn' | 'err'; className?: string }) {
  return <span className={cn('badge', tone, className)}>{children}</span>
}

export function Chip({
  children,
  active,
  onClick,
  icon
}: {
  children: React.ReactNode
  active?: boolean
  onClick?: () => void
  icon?: React.ReactNode
}) {
  return (
    <button className={cn('chip', active && 'active')} onClick={onClick}>
      {icon}
      {children}
    </button>
  )
}

// ---------------------------------------------------------------------------
// Modal
// ---------------------------------------------------------------------------

export function Modal({
  open,
  onClose,
  title,
  subtitle,
  children,
  footer,
  wide,
  dismissable = true
}: {
  open: boolean
  onClose: () => void
  title?: React.ReactNode
  subtitle?: React.ReactNode
  children: React.ReactNode
  footer?: React.ReactNode
  wide?: boolean
  dismissable?: boolean
}) {
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && dismissable) onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open, onClose, dismissable])

  if (!open) return null
  return createPortal(
    <div className="overlay" onMouseDown={(e) => e.target === e.currentTarget && dismissable && onClose()}>
      <div className={cn('modal', wide && 'wide')} onMouseDown={(e) => e.stopPropagation()}>
        {(title || dismissable) && (
          <div className="modal-head">
            <div>
              {title && <h3>{title}</h3>}
              {subtitle && <p>{subtitle}</p>}
            </div>
            {dismissable && (
              <button className="icon-btn" onClick={onClose} aria-label="关闭">
                <X />
              </button>
            )}
          </div>
        )}
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>,
    document.body
  )
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmText = '确认',
  danger,
  onConfirm,
  onClose
}: {
  open: boolean
  title: string
  message: React.ReactNode
  confirmText?: string
  danger?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Modal
      open={open}
      onClose={onClose}
      title={title}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            取消
          </Button>
          <Button
            variant={danger ? 'danger' : 'primary'}
            onClick={() => {
              onConfirm()
              onClose()
            }}
          >
            {confirmText}
          </Button>
        </>
      }
    >
      <div style={{ color: 'var(--text-soft)', fontSize: 13, lineHeight: 1.7 }}>{message}</div>
    </Modal>
  )
}

// ---------------------------------------------------------------------------
// Misc
// ---------------------------------------------------------------------------

export function ProgressBar({ current, total, solid }: { current: number; total: number; solid?: boolean }) {
  if (total <= 0) {
    return (
      <div className="progress-track">
        <div className="progress-fill indeterminate" />
      </div>
    )
  }
  const percent = Math.min(100, (current / total) * 100)
  return (
    <div className="progress-track">
      <div className={cn('progress-fill', solid && 'solid')} style={{ width: `${percent}%` }} />
    </div>
  )
}

export function EmptyState({
  icon,
  title,
  description,
  action
}: {
  icon?: React.ReactNode
  title: string
  description?: React.ReactNode
  action?: React.ReactNode
}) {
  return (
    <div className="empty">
      {icon && <div className="ico">{icon}</div>}
      <h3>{title}</h3>
      {description && <p>{description}</p>}
      {action}
    </div>
  )
}

export function Section({
  title,
  count,
  extra,
  children
}: {
  title: React.ReactNode
  count?: number
  extra?: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <>
      <div className="section-head">
        <h2>
          {title}
          {count !== undefined && <span className="count">{count}</span>}
        </h2>
        {extra}
      </div>
      {children}
    </>
  )
}

export interface MenuItem {
  label: string
  icon?: React.ReactNode
  onClick?: () => void
  danger?: boolean
  separator?: boolean
  disabled?: boolean
}

export function Dropdown({
  trigger,
  items,
  align = 'right'
}: {
  trigger: (toggle: () => void, open: boolean) => React.ReactNode
  items: MenuItem[]
  align?: 'left' | 'right'
}) {
  const [open, setOpen] = useState(false)
  const [pos, setPos] = useState<{ top: number; left?: number; right?: number; maxHeight: number } | null>(null)
  const wrapRef = useRef<HTMLDivElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const handler = (e: MouseEvent) => {
      const target = e.target as Node
      if (!wrapRef.current?.contains(target) && !menuRef.current?.contains(target)) setOpen(false)
    }
    const close = () => setOpen(false)
    // Ignore scrolls that happen inside the menu itself (its own scrollbar).
    const onScroll = (e: Event) => {
      if (menuRef.current?.contains(e.target as Node)) return
      setOpen(false)
    }
    document.addEventListener('mousedown', handler)
    window.addEventListener('resize', close)
    window.addEventListener('scroll', onScroll, true)
    return () => {
      document.removeEventListener('mousedown', handler)
      window.removeEventListener('resize', close)
      window.removeEventListener('scroll', onScroll, true)
    }
  }, [open])

  const toggle = (): void => {
    if (!open && wrapRef.current) {
      const rect = wrapRef.current.getBoundingClientRect()
      const estimated = Math.min(460, items.filter((i) => !i.separator).length * 36 + items.length * 4 + 14)
      const below = window.innerHeight - rect.bottom - 10
      const openUp = below < estimated && rect.top > below
      setPos({
        top: openUp ? Math.max(8, rect.top - estimated - 6) : rect.bottom + 6,
        ...(align === 'right' ? { right: Math.max(8, window.innerWidth - rect.right) } : { left: Math.max(8, rect.left) }),
        maxHeight: Math.max(160, openUp ? rect.top - 16 : below)
      })
    }
    setOpen((v) => !v)
  }

  return (
    <>
      <div className="dropdown" ref={wrapRef} onClick={(e) => e.stopPropagation()}>
        {trigger(toggle, open)}
      </div>
      {open &&
        pos &&
        createPortal(
          <div
            className="menu portal"
            ref={menuRef}
            style={{ top: pos.top, left: pos.left, right: pos.right, maxHeight: pos.maxHeight, overflowY: 'auto', overscrollBehavior: 'contain' }}
            onClick={(e) => e.stopPropagation()}
            onWheel={(e) => e.stopPropagation()}
          >
            {items.map((item, index) =>
              item.separator ? (
                <div className="menu-sep" key={`sep-${index}`} />
              ) : (
                <button
                  key={item.label}
                  className={cn('menu-item', item.danger && 'danger')}
                  disabled={item.disabled}
                  onClick={() => {
                    setOpen(false)
                    item.onClick?.()
                  }}
                >
                  {item.icon}
                  {item.label}
                </button>
              )
            )}
          </div>,
          document.body
        )}
    </>
  )
}

export function Toasts() {
  const toasts = useStore((s) => s.toasts)
  const dismiss = useStore((s) => s.dismissToast)
  const icons = {
    success: <Check />,
    error: <XCircle />,
    warning: <AlertTriangle />,
    info: <Info />
  }
  if (!toasts.length) return null
  return createPortal(
    <div className="toast-stack">
      {toasts.map((t) => (
        <div key={t.id} className={cn('toast', t.type, t.leaving && 'leaving')} onClick={() => dismiss(t.id)}>
          <div className="ico">{icons[t.type]}</div>
          <div className="content">
            {t.title && <div className="t">{t.title}</div>}
            <div className="m">{t.message}</div>
          </div>
        </div>
      ))}
    </div>,
    document.body
  )
}

export function Lightbox({ src, onClose }: { src: string; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])
  return createPortal(
    <div className="overlay" style={{ background: 'rgba(2,4,9,0.86)' }} onClick={onClose}>
      <img
        src={src}
        alt=""
        style={{ maxWidth: '92vw', maxHeight: '88vh', borderRadius: 16, boxShadow: '0 30px 90px rgba(0,0,0,0.7)' }}
        onClick={(e) => e.stopPropagation()}
      />
    </div>,
    document.body
  )
}

export function Skeleton({ height = 16, width = '100%', style }: { height?: number; width?: number | string; style?: React.CSSProperties }) {
  return <div className="skeleton" style={{ height, width, ...style }} />
}

export function Divider() {
  return <div className="divider" />
}

export function useClickOutside(ref: React.RefObject<HTMLElement | null>, onOutside: () => void) {
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) onOutside()
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [ref, onOutside])
}

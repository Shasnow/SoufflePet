// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react'

interface PageTabsProps<T extends string> {
  /** 标签组名称，供读屏软件识别 */
  label: string
  tabs: readonly { id: T; label: string }[]
  value: T
  onChange: (value: T) => void
  /** 面板内容：由调用方决定只渲染当前标签，还是保持常驻以保留编辑中的状态 */
  children: ReactNode
}

/**
 * 页内标签：补齐 tab/tabpanel 语义与左右方向键切换。
 * 与 Tabs 的“只挂载当前面板”不同，这里由调用方决定面板是否常驻。
 */
export function PageTabs<T extends string>({ label, tabs, value, onChange, children }: PageTabsProps<T>) {
  const baseId = useId()
  const buttons = useRef<Record<string, HTMLButtonElement | null>>({})
  const panelId = `${baseId}-panel`

  const moveFocus = (event: KeyboardEvent<HTMLDivElement>) => {
    const current = tabs.findIndex((item) => item.id === value)
    if (current < 0) return
    const offset =
      event.key === 'ArrowRight'
        ? 1
        : event.key === 'ArrowLeft'
          ? -1
          : event.key === 'Home'
            ? -current
            : event.key === 'End'
              ? tabs.length - 1 - current
              : 0
    if (offset === 0) return
    event.preventDefault()
    const next = tabs[(current + offset + tabs.length) % tabs.length]
    onChange(next.id)
    buttons.current[next.id]?.focus()
  }

  return (
    <>
      <div className="page-tabs" role="tablist" aria-label={label} onKeyDown={moveFocus}>
        {tabs.map((item) => (
          <button
            key={item.id}
            ref={(node) => {
              buttons.current[item.id] = node
            }}
            type="button"
            role="tab"
            id={`${baseId}-tab-${item.id}`}
            aria-selected={item.id === value}
            aria-controls={panelId}
            tabIndex={item.id === value ? 0 : -1}
            className="page-tab"
            data-active={(item.id === value).toString()}
            onClick={() => onChange(item.id)}
          >
            {item.label}
          </button>
        ))}
      </div>
      <div className="page-tabpanel" role="tabpanel" id={panelId} aria-labelledby={`${baseId}-tab-${value}`}>
        {children}
      </div>
    </>
  )
}

// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import type { ReactNode } from 'react'

/** 设置页通用卡片：标题 + 说明 + 右侧操作。 */
export function SettingsCard({
  title,
  desc,
  actions,
  children,
}: {
  title: string
  desc?: string
  actions?: ReactNode
  children: ReactNode
}) {
  return (
    <section className="card settings-card">
      <div className="settings-card-head">
        <div style={{ minWidth: 0 }}>
          <div className="card-title">{title}</div>
          {desc ? <div className="card-desc">{desc}</div> : null}
        </div>
        {actions ? <div className="settings-card-actions">{actions}</div> : null}
      </div>
      {children}
    </section>
  )
}

export function SettingsError({ message }: { message: string | null | undefined }) {
  if (!message) return null
  return (
    <div className="settings-error" role="alert">
      {message}
    </div>
  )
}

export function SettingsEmpty({ children }: { children: ReactNode }) {
  return <div className="settings-empty">{children}</div>
}

export function Toggle({
  checked,
  onChange,
  label,
  disabled = false,
}: {
  checked: boolean
  onChange: (value: boolean) => void
  label: string
  disabled?: boolean
}) {
  return (
    <label className="settings-toggle">
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
      />
      <span>{label}</span>
    </label>
  )
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/** 多行文本 → 去空行后的数组（MCP 参数等）。 */
export function splitLines(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
}

/** KEY=VALUE 行 ↔ 对象；解析失败返回 null。 */
export function parseKeyValueLines(text: string): Record<string, string> | null {
  const result: Record<string, string> = {}
  for (const line of splitLines(text)) {
    const index = line.indexOf('=')
    if (index <= 0) return null
    result[line.slice(0, index).trim()] = line.slice(index + 1).trim()
  }

  return result
}

export function formatKeyValueLines(map: Record<string, string> | undefined): string {
  if (!map) return ''
  return Object.entries(map)
    .map(([key, value]) => `${key}=${value}`)
    .join('\n')
}

export function formatMapForDisplay(map: Record<string, string> | undefined): string {
  return formatKeyValueLines(map)
}

// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

const relative = new Intl.RelativeTimeFormat('zh-CN', { numeric: 'auto' })

export function formatClock(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return new Intl.DateTimeFormat('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(date)
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return new Intl.DateTimeFormat('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(date)
}

/** 侧栏用的相对时间：刚刚 / 5 分钟前 / 昨天 / 3 天前 / 具体日期。 */
export function formatRelative(iso: string, now = Date.now()): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''

  const seconds = Math.round((date.getTime() - now) / 1000)
  const absolute = Math.abs(seconds)
  if (absolute < 60) return '刚刚'
  if (absolute < 3600) return relative.format(Math.round(seconds / 60), 'minute')
  if (absolute < 86_400) return relative.format(Math.round(seconds / 3600), 'hour')
  if (absolute < 86_400 * 7) return relative.format(Math.round(seconds / 86_400), 'day')
  return formatDateTime(iso)
}

export function formatTokens(value: number): string {
  if (!Number.isFinite(value) || value < 0) return '0 tok'
  if (value < 1000) return `${Math.round(value)} tok`
  if (value < 1_000_000) return `${(value / 1000).toFixed(1)}K tok`
  return `${(value / 1_000_000).toFixed(2)}M tok`
}

export function formatCompactTokens(value: number): string {
  if (!Number.isFinite(value) || value < 0) return '0'
  if (value < 1000) return `${Math.round(value)}`
  if (value < 1_000_000) return `${(value / 1000).toFixed(1)}K`
  return `${(value / 1_000_000).toFixed(2)}M`
}

export function formatPercent(value: number, digits = 1): string {
  if (!Number.isFinite(value)) return '—'
  return `${(value * 100).toFixed(digits)}%`
}

/** 会话统计用的耗时：小于 1 分钟保留 1 位小数秒，更长换算成分钟/小时。 */
export function formatSeconds(seconds: number): string {
  const value = Number.isFinite(seconds) ? Math.max(0, seconds) : 0
  if (value < 60) return `${value.toFixed(1)} 秒`
  if (value < 3600) return `${(value / 60).toFixed(1)} 分钟`
  const hours = Math.floor(value / 3600)
  const minutes = Math.round((value % 3600) / 60)
  return minutes > 0 ? `${hours} 小时 ${minutes} 分钟` : `${hours} 小时`
}

/** 输出速度：低速保留小数，避免把 0.4 tok/s 显示成 0 tok/s。 */
export function formatTps(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '0 tok/s'
  const speed = value < 1 ? value.toFixed(2) : value < 10 ? value.toFixed(1) : `${Math.round(value)}`
  return `${speed} tok/s`
}

export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return '0 秒'
  if (seconds < 60) return `${Math.round(seconds)} 秒`
  if (seconds < 3600) return `${Math.round(seconds / 60)} 分钟`
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.round((seconds % 3600) / 60)
  return minutes > 0 ? `${hours} 小时 ${minutes} 分钟` : `${hours} 小时`
}

export function previewText(text: string, max = 30): string {
  const single = text.replace(/\s+/g, ' ').trim()
  if (!single) return '新会话'
  return single.length > max ? `${single.slice(0, max)}…` : single
}

export function firstLine(text: string): string | null {
  for (const line of text.split('\n')) {
    const trimmed = line.trim()
    if (trimmed) return trimmed
  }
  return null
}

export function latestLine(text: string): string | null {
  for (let index = text.length - 1; index >= 0; index--) {
    if (text[index] !== '\n') continue
    const line = text.slice(index + 1).trim()
    if (line) return line
    text = text.slice(0, index)
  }
  return text.trim() || null
}
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useSyncExternalStore } from 'react'

export type SettingsPanel =
  | 'llm'
  | 'mcp'
  | 'skills'
  | 'plugins'
  | 'tasks'
  | 'models'
  | 'appearance'

export const SETTINGS_PANELS: { id: SettingsPanel; label: string }[] = [
  { id: 'llm', label: '对话模型' },
  { id: 'mcp', label: 'MCP 服务器' },
  { id: 'skills', label: '技能' },
  { id: 'plugins', label: '插件' },
  { id: 'tasks', label: '自动化任务' },
  { id: 'models', label: 'Live2D 模型' },
  { id: 'appearance', label: '外观' },
]

export type Route =
  | { kind: 'chat'; sessionId?: string }
  | { kind: 'settings'; panel: SettingsPanel }
  | { kind: 'unknown'; hash: string }

const listeners = new Set<() => void>()

function notify() {
  for (const listener of listeners) listener()
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

if (typeof window !== 'undefined') {
  window.addEventListener('hashchange', notify)
}

function isSettingsPanel(value: string): value is SettingsPanel {
  return SETTINGS_PANELS.some((panel) => panel.id === value)
}

export function parseHash(hash: string): Route {
  const clean = hash.replace(/^#/, '')
  const parts = clean.split('/').filter(Boolean)
  if (parts.length === 0) return { kind: 'chat' }
  if (parts[0] === 'chat') {
    return parts[1] ? { kind: 'chat', sessionId: decodeURIComponent(parts[1]) } : { kind: 'chat' }
  }
  if (parts[0] === 'settings' && parts[1] && isSettingsPanel(parts[1])) {
    return { kind: 'settings', panel: parts[1] }
  }
  return { kind: 'unknown', hash }
}

export function routeHash(route: Route): string {
  if (route.kind === 'chat') {
    return route.sessionId ? `#/chat/${encodeURIComponent(route.sessionId)}` : '#/chat'
  }
  if (route.kind === 'settings') return `#/settings/${route.panel}`
  return route.hash
}

export function navigate(route: Route, replace = false): void {
  const next = routeHash(route)
  if (window.location.hash === next) return
  if (replace) {
    const url = `${window.location.pathname}${window.location.search}${next}`
    window.history.replaceState(null, '', url)
    notify()
    return
  }
  window.location.hash = next
}

export function useRoute(): Route {
  const hash = useSyncExternalStore(
    subscribe,
    () => window.location.hash,
    () => '',
  )
  return parseHash(hash)
}

export function settingsRoute(panel: SettingsPanel): Route {
  return { kind: 'settings', panel }
}

export function chatRoute(sessionId?: string): Route {
  return { kind: 'chat', sessionId }
}
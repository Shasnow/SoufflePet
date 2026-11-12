// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useSyncExternalStore } from 'react'

export type SettingsPanel = 'persona' | 'llm' | 'models' | 'appearance'

export const SETTINGS_PANELS: { id: SettingsPanel; label: string }[] = [
  { id: 'persona', label: '人设' },
  { id: 'llm', label: '对话模型' },
  { id: 'models', label: 'Live2D 模型' },
  { id: 'appearance', label: '外观' },
]

/** 插件、技能、MCP 合并在同一个“插件与扩展”页面，用页内标签切换。 */
export type ExtensionTab = 'plugins' | 'skills' | 'mcp'

export const EXTENSION_TABS: { id: ExtensionTab; label: string }[] = [
  { id: 'plugins', label: '插件' },
  { id: 'skills', label: '技能' },
  { id: 'mcp', label: 'MCP 服务器' },
]

/** 自动化页的两个页内标签：任务配置与执行记录，同样写进路由以便刷新/后退保留。 */
export type TasksTab = 'tasks' | 'history'

export const TASKS_TABS: { id: TasksTab; label: string }[] = [
  { id: 'tasks', label: '已配置' },
  { id: 'history', label: '执行历史' },
]

export type Route =
  | { kind: 'chat'; sessionId?: string }
  | { kind: 'settings'; panel: SettingsPanel }
  | { kind: 'extensions'; tab: ExtensionTab }
  | { kind: 'tasks'; tab: TasksTab }
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

function isExtensionTab(value: string): value is ExtensionTab {
  return EXTENSION_TABS.some((tab) => tab.id === value)
}

function isTasksTab(value: string): value is TasksTab {
  return TASKS_TABS.some((tab) => tab.id === value)
}

export function parseHash(hash: string): Route {
  const clean = hash.replace(/^#/, '')
  const parts = clean.split('/').filter(Boolean)
  if (parts.length === 0) return { kind: 'chat' }
  if (parts[0] === 'chat') {
    return parts[1] ? { kind: 'chat', sessionId: decodeURIComponent(parts[1]) } : { kind: 'chat' }
  }
  if (parts[0] === 'extensions') {
    const tab = parts[1]
    if (tab && isExtensionTab(tab)) return { kind: 'extensions', tab }
    return { kind: 'unknown', hash }
  }
  if (parts[0] === 'tasks') {
    const tab = parts[1]
    if (!tab) return { kind: 'tasks', tab: 'tasks' }
    if (isTasksTab(tab)) return { kind: 'tasks', tab }
    return { kind: 'unknown', hash }
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
  if (route.kind === 'extensions') return `#/extensions/${route.tab}`
  if (route.kind === 'tasks') return route.tab === 'tasks' ? '#/tasks' : `#/tasks/${route.tab}`
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

export function extensionsRoute(tab: ExtensionTab): Route {
  return { kind: 'extensions', tab }
}

export function tasksRoute(tab: TasksTab = 'tasks'): Route {
  return { kind: 'tasks', tab }
}

export function chatRoute(sessionId?: string): Route {
  return { kind: 'chat', sessionId }
}

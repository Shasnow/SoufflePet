// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Copy, Menu, Minus, Sparkles, Square, X } from 'lucide-react'
import { useRef } from 'react'
import { invoke } from '@/bridge/client'
import { useAppStore } from '@/stores/appStore'
import { useSessionStore } from '@/stores/sessionStore'
import { useUiStore } from '@/stores/uiStore'
import { SETTINGS_PANELS, useRoute } from './router'

export function TopBar() {
  const pet = useAppStore((state) => state.pet)
  const sessions = useSessionStore((state) => state.sessions)
  const activeId = useSessionStore((state) => state.activeId)
  const maximized = useUiStore((state) => state.maximized)
  const toggleSidebar = useUiStore((state) => state.toggleSidebar)
  const route = useRoute()

  const timer = useRef<number | null>(null)
  const start = useRef<{ x: number; y: number } | null>(null)

  const activeTitle = sessions.find((session) => session.id === activeId)?.title ?? '新会话'
  const title =
    route.kind === 'chat'
      ? activeTitle
      : route.kind === 'settings'
        ? SETTINGS_PANELS.find((panel) => panel.id === route.panel)?.label ?? '设置'
        : 'SoufflePet'

  const clearTimer = () => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current)
      timer.current = null
    }
    start.current = null
  }

  const beginDrag = () => {
    timer.current = null
    start.current = null
    void invoke('window.drag').catch(() => undefined)
  }

  return (
    <header className="topbar">
      <button
        type="button"
        className="menu-btn"
        aria-label="切换侧栏"
        title="切换侧栏"
        data-no-drag
        onClick={toggleSidebar}
      >
        <Menu size={18} />
      </button>
      <div
        className="topbar-drag"
        onPointerDown={(event) => {
          if (event.button !== 0) return
          const target = event.target as HTMLElement
          if (target.closest('[data-no-drag]')) return
          start.current = { x: event.clientX, y: event.clientY }
        }}
        onPointerMove={(event) => {
          if (!start.current) return
          const dx = event.clientX - start.current.x
          const dy = event.clientY - start.current.y
          if (dx * dx + dy * dy < 16) return
          beginDrag()
        }}
        onPointerUp={clearTimer}
        onPointerCancel={clearTimer}
        onDoubleClick={() => {
          clearTimer()
          void invoke('window.maximize').catch(() => undefined)
        }}
      >
        <Sparkles size={17} style={{ color: 'var(--accent)', flex: 'none' }} />
        <span style={{ fontWeight: 650, flex: 'none' }}>SoufflePet</span>
        <span className="topbar-title">· {pet.name} · {title}</span>
      </div>
      <div className="topbar-actions">
        <button
          type="button"
          className="window-btn"
          aria-label="最小化"
          title="最小化"
          data-no-drag
          onClick={() => void invoke('window.minimize').catch(() => undefined)}
        >
          <Minus size={15} />
        </button>
        <button
          type="button"
          className="window-btn"
          aria-label={maximized ? '还原' : '最大化'}
          title={maximized ? '还原' : '最大化'}
          data-no-drag
          onClick={() => void invoke('window.maximize').catch(() => undefined)}
        >
          {maximized ? <Copy size={13} /> : <Square size={13} />}
        </button>
        <button
          type="button"
          className="window-btn is-close"
          aria-label="关闭"
          title="关闭（隐藏到托盘）"
          data-no-drag
          onClick={() => void invoke('window.close').catch(() => undefined)}
        >
          <X size={16} />
        </button>
      </div>
    </header>
  )
}
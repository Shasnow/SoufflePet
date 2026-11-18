// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { ChevronRight, Copy, Menu, Minus, Sparkles, Square, Users, X } from 'lucide-react'
import { useMemo, useRef, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { SessionSummaryDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { ancestorsOf, useSessionStore } from '@/stores/sessionStore'
import { useUiStore } from '@/stores/uiStore'
import { formatRelative } from '@/ui/format'
import { Popover } from '@/ui/Popover'
import { chatRoute, navigate, SETTINGS_PANELS, useRoute } from './router'

/** 当前会话的子代理数量；点开列出子会话入口，点击即可进入查看完整执行过程。 */
function SubagentMenu({ parentId, sessions }: { parentId: string; sessions: SessionSummaryDto[] }) {
  const [open, setOpen] = useState(false)
  const children = useMemo(
    () =>
      sessions
        .filter((session) => session.parentSessionId === parentId)
        .sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt)),
    [sessions, parentId],
  )

  if (children.length === 0) return null

  const waitingCount = children.filter((child) => child.waitingForUser).length

  return (
    <Popover
      open={open}
      onOpenChange={setOpen}
      className="subagent-popover"
      trigger={
        <button type="button" className="subagent-chip" data-no-drag title="查看子代理会话">
          <Users size={14} />
          <span>{waitingCount > 0 ? `${waitingCount} 个等待审批` : `${children.length} 个子代理`}</span>
          {children.some((child) => child.generating || child.waitingForUser) ? (
            <span className="subagent-chip-pulse" />
          ) : null}
        </button>
      }
    >
      <div className="subagent-panel">
        <div className="subagent-panel-head">
          <span>子代理</span>
          <span className="subagent-panel-count">{children.length}</span>
        </div>
        <div className="subagent-list">
          {children.map((child) => (
            <button
              key={child.id}
              type="button"
              className="subagent-item"
              onClick={() => {
                setOpen(false)
                navigate(chatRoute(child.id))
              }}
            >
              <span className={`subagent-dot${child.generating ? ' is-running' : ''}`} />
              <span className="subagent-item-text">
                <span className="subagent-item-title">{child.title}</span>
                <span className="subagent-item-sub">{child.lastUserText ?? '暂无消息'}</span>
              </span>
              <span className="subagent-item-meta">
                <span>
                  {child.waitingForUser ? '等待审批' : child.generating ? '运行中' : formatRelative(child.updatedAt)}
                </span>
                <span className="subagent-item-count">{child.messageCount} 条</span>
              </span>
              <ChevronRight size={14} className="subagent-item-arrow" />
            </button>
          ))}
        </div>
      </div>
    </Popover>
  )
}

export function TopBar() {
  const pet = useAppStore((state) => state.pet)
  const sessions = useSessionStore((state) => state.sessions)
  const activeId = useSessionStore((state) => state.activeId)
  const maximized = useUiStore((state) => state.maximized)
  const toggleSidebar = useUiStore((state) => state.toggleSidebar)
  const route = useRoute()

  const timer = useRef<number | null>(null)
  const start = useRef<{ x: number; y: number } | null>(null)

  const activeSession = sessions.find((session) => session.id === activeId)
  const ancestors = useMemo(() => ancestorsOf(sessions, activeId), [sessions, activeId])
  const title =
    route.kind === 'chat'
      ? activeSession?.title ?? '新会话'
      : route.kind === 'settings'
        ? SETTINGS_PANELS.find((panel) => panel.id === route.panel)?.label ?? '设置'
        : route.kind === 'extensions'
          ? '插件与扩展'
          : route.kind === 'tasks'
            ? '自动化'
            : 'SoufflePet'

  // 子会话的标题前缀显示完整祖先链：父会话可点击跳回，子会话本身是当前项。
  const crumbs =
    route.kind === 'chat' && ancestors.length > 0
      ? [
          ...ancestors.map((session) => ({ id: session.id, title: session.title, current: false })),
          { id: activeId ?? '', title, current: true },
        ]
      : null

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
        <span className="topbar-title">
          · {pet.name} ·{' '}
          {crumbs ? (
            <span className="topbar-crumbs">
              {crumbs.map((crumb) =>
                crumb.current ? (
                  <span key={crumb.id} className="topbar-crumb is-current">{crumb.title}</span>
                ) : (
                  <button
                    key={crumb.id}
                    type="button"
                    className="topbar-crumb"
                    data-no-drag
                    onClick={() => navigate(chatRoute(crumb.id))}
                  >
                    {crumb.title}
                  </button>
                ),
              )}
            </span>
          ) : (
            title
          )}
        </span>
        {route.kind === 'chat' && activeId ? (
          <SubagentMenu parentId={activeId} sessions={sessions} />
        ) : null}
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
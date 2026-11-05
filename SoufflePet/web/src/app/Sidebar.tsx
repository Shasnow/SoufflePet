// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import {
  Blocks,
  Clock,
  Ellipsis,
  FolderOpen,
  MessageSquarePlus,
  Plus,
  Search,
  Settings,
  Sparkles,
  X,
} from 'lucide-react'
import { useMemo, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { SessionSummaryDto, WorkspaceDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { filterSessions, groupSessions, useSessionStore } from '@/stores/sessionStore'
import { useUiStore } from '@/stores/uiStore'
import { Dialog } from '@/ui/Dialog'
import { GlobalUsageButton } from '@/ui/Usage'
import { Popover } from '@/ui/Popover'
import { chatRoute, navigate, settingsRoute, useRoute } from '@/app/router'
import type { SettingsPanel } from '@/app/router'
import { SessionItem } from '@/features/sessions/SessionItem'

type MorePanel = 'mcp' | 'skills' | 'llm' | 'models' | 'appearance'

const MORE_ITEMS: { panel: MorePanel; label: string }[] = [
  { panel: 'llm', label: '对话模型' },
  { panel: 'mcp', label: 'MCP 服务器' },
  { panel: 'skills', label: '技能' },
  { panel: 'models', label: 'Live2D 模型' },
  { panel: 'appearance', label: '外观' },
]

export function Sidebar() {
  const pet = useAppStore((state) => state.pet)
  const usage = useAppStore((state) => state.usage)
  const sessions = useSessionStore((state) => state.sessions)
  const activeId = useSessionStore((state) => state.activeId)
  const query = useSessionStore((state) => state.query)
  const setQuery = useSessionStore((state) => state.setQuery)
  const renameLocal = useSessionStore((state) => state.renameLocal)
  const removeLocal = useSessionStore((state) => state.removeLocal)
  const removeChat = useChatStore((state) => state.removeSession)
  const setActive = useSessionStore((state) => state.setActive)
  const setDrawerOpen = useUiStore((state) => state.setDrawerOpen)
  const route = useRoute()

  const [moreOpen, setMoreOpen] = useState(false)
  const [editing, setEditing] = useState<SessionSummaryDto | null>(null)
  const [editTitle, setEditTitle] = useState('')
  const [deleting, setDeleting] = useState<SessionSummaryDto | null>(null)
  const [busy, setBusy] = useState(false)

  const filtered = useMemo(() => filterSessions(sessions, query), [sessions, query])
  const groups = useMemo(() => groupSessions(filtered), [filtered])
  const activeWorkspace = useChatStore((state) =>
    state.activeId ? state.bySession[state.activeId]?.workspace ?? null : null,
  )

  const currentPanel = route.kind === 'settings' ? route.panel : null
  const moreActive = currentPanel !== null && MORE_ITEMS.some((item) => item.panel === currentPanel)

  const closeDrawer = () => {
    if (window.innerWidth < 720) setDrawerOpen(false)
  }

  const openChat = (sessionId?: string) => {
    navigate(chatRoute(sessionId))
    closeDrawer()
  }

  const openSettings = (panel: SettingsPanel) => {
    navigate(settingsRoute(panel))
    setMoreOpen(false)
    closeDrawer()
  }

  const createSession = async (workspacePath?: string) => {
    if (busy) return
    setBusy(true)
    try {
      const summary = await invoke<SessionSummaryDto>(
        'session.new',
        workspacePath ? { workspacePath } : {},
      )
      navigate(chatRoute(summary.id))
      closeDrawer()
    } catch (error) {
      console.warn('create session failed', error)
    } finally {
      setBusy(false)
    }
  }

  const pickWorkspace = async () => {
    try {
      const workspace = await invoke<WorkspaceDto | null>('chat.workspace.pick')
      if (workspace) await createSession(workspace.path)
    } catch (error) {
      console.warn('pick workspace failed', error)
    }
  }

  const saveTitle = async () => {
    if (!editing) return
    const title = editTitle.trim()
    if (!title) return
    setBusy(true)
    try {
      await invoke('session.rename', { sessionId: editing.id, title })
      renameLocal(editing.id, title)
      setEditing(null)
    } catch (error) {
      console.warn('rename session failed', error)
    } finally {
      setBusy(false)
    }
  }

  const confirmDelete = async () => {
    if (!deleting) return
    setBusy(true)
    try {
      const result = await invoke<{ sessions: SessionSummaryDto[]; activeId: string | null }>(
        'session.delete',
        { sessionId: deleting.id },
      )
      removeChat(deleting.id)
      removeLocal(deleting.id, result.activeId)
      setActive(result.activeId)
      if (deleting.id === activeId) {
        navigate(chatRoute(result.activeId ?? undefined), true)
      }
      setDeleting(null)
    } catch (error) {
      console.warn('delete session failed', error)
    } finally {
      setBusy(false)
    }
  }

  return (
    <aside className="sidebar" aria-label="侧边栏">
      <div className="sidebar-head">
        <button type="button" className="brand" onClick={() => openChat(activeId ?? undefined)}>
          <span className="brand-mark">
            <Sparkles size={16} />
          </span>
          <span className="brand-text">SoufflePet</span>
        </button>
      </div>

      <div className="sidebar-scroll">
        <div className="nav-list">
          <button
            type="button"
            className="nav-item nav-new"
            disabled={busy}
            onClick={() => void createSession(activeWorkspace?.path)}
          >
            <MessageSquarePlus size={17} className="nav-icon" />
            <span className="nav-label">新会话</span>
            <Plus size={14} style={{ marginLeft: 'auto' }} />
          </button>

          <button
            type="button"
            className="nav-item"
            data-active={currentPanel === 'plugins' ? 'true' : 'false'}
            onClick={() => openSettings('plugins')}
          >
            <Blocks size={17} className="nav-icon" />
            <span className="nav-label">插件</span>
          </button>
          <button
            type="button"
            className="nav-item"
            data-active={currentPanel === 'tasks' ? 'true' : 'false'}
            onClick={() => openSettings('tasks')}
          >
            <Clock size={17} className="nav-icon" />
            <span className="nav-label">自动化任务</span>
          </button>

          <Popover
            open={moreOpen}
            onOpenChange={setMoreOpen}
            side="right"
            align="start"
            trigger={
              <button
                type="button"
                className="nav-item"
                data-active={moreActive ? 'true' : 'false'}
              >
                <Ellipsis size={17} className="nav-icon" />
                <span className="nav-label">更多</span>
              </button>
            }
          >
            <div className="menu-label">设置与工具</div>
            {MORE_ITEMS.map((item) => (
              <button
                key={item.panel}
                type="button"
                className="menu-item"
                data-active={currentPanel === item.panel ? 'true' : 'false'}
                onClick={() => openSettings(item.panel)}
              >
                {item.label}
              </button>
            ))}
          </Popover>
        </div>

        <section className="sidebar-section" aria-label="工作区与会话">
          <div className="sidebar-section-head">
            <span>工作区</span>
            <span className="spacer" />
            <button type="button" className="icon-btn" aria-label="选择工作区" onClick={() => void pickWorkspace()}>
              <FolderOpen size={14} />
            </button>
            <button
              type="button"
              className="icon-btn"
              aria-label="新建会话"
              onClick={() => void createSession(activeWorkspace?.path)}
            >
              <Plus size={14} />
            </button>
          </div>

          <div style={{ padding: '4px 6px 6px' }}>
            <div style={{ position: 'relative' }}>
              <Search
                size={13}
                style={{
                  position: 'absolute',
                  left: 9,
                  top: '50%',
                  transform: 'translateY(-50%)',
                  color: 'var(--text-3)',
                  pointerEvents: 'none',
                }}
              />
              <input
                className="input"
                style={{ height: 30, paddingLeft: 28, fontSize: 12.5 }}
                value={query}
                placeholder="搜索会话…"
                onChange={(event) => setQuery(event.target.value)}
                aria-label="搜索会话"
              />
              {query ? (
                <button
                  type="button"
                  className="icon-btn"
                  aria-label="清除搜索"
                  style={{ position: 'absolute', right: 3, top: 3 }}
                  onClick={() => setQuery('')}
                >
                  <X size={13} />
                </button>
              ) : null}
            </div>
          </div>

          {groups.length === 0 ? (
            <div className="sidebar-empty">
              {query ? '没有匹配的会话。' : '还没有会话，点「新会话」开始。'}
            </div>
          ) : (
            groups.map((group) => (
              <div className="workspace-group" key={group.key || 'default'}>
                <div className="workspace-title" title={group.key}>
                  <FolderOpen size={13} />
                  <span className="name">{group.name}</span>
                  <button
                    type="button"
                    className="icon-btn"
                    aria-label={`在 ${group.name} 新建会话`}
                    style={{ marginLeft: 'auto' }}
                    onClick={() => void createSession(group.key || undefined)}
                  >
                    <Plus size={13} />
                  </button>
                </div>
                <div className="session-list">
                  {group.sessions.map((session) => (
                    <SessionItem
                      key={session.id}
                      session={session}
                      active={session.id === activeId}
                      onOpen={(item) => openChat(item.id)}
                      onRename={(item) => {
                        setEditing(item)
                        setEditTitle(item.title)
                      }}
                      onDelete={(item) => setDeleting(item)}
                    />
                  ))}
                </div>
              </div>
            ))
          )}
        </section>
      </div>

      <div className="sidebar-foot">
        <div className="account-chip">
          <span className="account-avatar">{pet.initial}</span>
          <div className="account-meta">
            <div className="account-name">{pet.name}</div>
            <GlobalUsageButton usage={usage} compact />
          </div>
          <button
            type="button"
            className="icon-btn"
            aria-label="外观设置"
            onClick={() => openSettings('appearance')}
          >
            <Settings size={15} />
          </button>
        </div>
      </div>

      <Dialog
        open={editing !== null}
        onOpenChange={(open) => {
          if (!open) setEditing(null)
        }}
        title="重命名会话"
        description="标题会显示在侧栏与窗口标题上。"
      >
        <div className="field" style={{ marginTop: 12 }}>
          <label className="field-label" htmlFor="session-title">
            会话标题
          </label>
          <input
            id="session-title"
            className="input"
            value={editTitle}
            maxLength={80}
            onChange={(event) => setEditTitle(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') void saveTitle()
            }}
            autoFocus
          />
        </div>
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setEditing(null)}>
            取消
          </button>
          <button type="button" className="btn btn-primary" disabled={busy || !editTitle.trim()} onClick={() => void saveTitle()}>
            保存
          </button>
        </div>
      </Dialog>

      <Dialog
        open={deleting !== null}
        onOpenChange={(open) => {
          if (!open) setDeleting(null)
        }}
        title="删除会话"
        description={`「${deleting?.title ?? ''}」会移入回收站，聊天记录可从 sessions/.deleted 恢复。`}
      >
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setDeleting(null)}>
            取消
          </button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={() => void confirmDelete()}>
            删除
          </button>
        </div>
      </Dialog>
    </aside>
  )
}
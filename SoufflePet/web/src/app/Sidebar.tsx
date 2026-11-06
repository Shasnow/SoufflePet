// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import * as DropdownMenu from '@radix-ui/react-dropdown-menu'
import {
  Blocks,
  ChevronRight,
  Clock,
  Ellipsis,
  FolderOpen,
  MessageSquarePlus,
  Pencil,
  Plus,
  Search,
  Settings,
  Sparkles,
  Trash2,
  X,
} from 'lucide-react'
import { useMemo, useRef, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { SessionSummaryDto, WorkspaceDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { filterSessions, groupSessions, useSessionStore } from '@/stores/sessionStore'
import { useUiStore } from '@/stores/uiStore'
import { Dialog } from '@/ui/Dialog'
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
  const workspaces = useAppStore((state) => state.workspaces)
  const sessions = useSessionStore((state) => state.sessions)
  const activeId = useSessionStore((state) => state.activeId)
  const query = useSessionStore((state) => state.query)
  const setQuery = useSessionStore((state) => state.setQuery)
  const renameLocal = useSessionStore((state) => state.renameLocal)
  const removeLocal = useSessionStore((state) => state.removeLocal)
  const setUpdated = useSessionStore((state) => state.setUpdated)
  const removeChat = useChatStore((state) => state.removeSession)
  const setActive = useSessionStore((state) => state.setActive)
  const setDrawerOpen = useUiStore((state) => state.setDrawerOpen)
  const route = useRoute()

  const [moreOpen, setMoreOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const [editing, setEditing] = useState<SessionSummaryDto | null>(null)
  const [editTitle, setEditTitle] = useState('')
  const [deleting, setDeleting] = useState<SessionSummaryDto | null>(null)
  const [busy, setBusy] = useState(false)
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const [renamingWorkspace, setRenamingWorkspace] = useState<{ path: string; name: string } | null>(null)
  const [workspaceTitle, setWorkspaceTitle] = useState('')
  const [deletingWorkspace, setDeletingWorkspace] = useState<{
    path: string
    name: string
    count: number
  } | null>(null)
  const searchButton = useRef<HTMLButtonElement | null>(null)
  // 记录工作区菜单是否由指针打开：关闭时据此决定是否把焦点还给触发按钮
  const menuByPointer = useRef(false)

  const toggleCollapsed = (key: string) =>
    setCollapsed((prev) => ({ ...prev, [key]: !prev[key] }))

  const openSearch = () => setSearchOpen(true)

  const closeSearch = () => {
    setSearchOpen(false)
    setQuery('')
    // 收起后搜索按钮才重新挂载，等一帧再还焦点，避免焦点掉到 body
    window.setTimeout(() => searchButton.current?.focus(), 0)
  }

  const filtered = useMemo(() => filterSessions(sessions, query), [sessions, query])
  // 搜索时不显示没有命中会话的空工作区分组
  const groups = useMemo(
    () => groupSessions(filtered, query ? [] : workspaces),
    [filtered, workspaces, query],
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
      if (workspace) {
        // 新选中的工作区立即进入全局列表，会话删空后分组也不会消失
        const app = useAppStore.getState()
        if (!app.workspaces.some((item) => item.path === workspace.path)) {
          app.setWorkspaces([...app.workspaces, workspace])
        }
        await createSession(workspace.path)
      }
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

  const startRenameWorkspace = (key: string, name: string) => {
    setWorkspaceTitle(name)
    setRenamingWorkspace({ path: key, name })
  }

  const saveWorkspaceName = async () => {
    if (!renamingWorkspace) return
    const name = workspaceTitle.trim()
    if (!name) return
    setBusy(true)
    try {
      await invoke('chat.workspace.rename', { path: renamingWorkspace.path, name })
      setRenamingWorkspace(null)
    } catch (error) {
      console.warn('rename workspace failed', error)
    } finally {
      setBusy(false)
    }
  }

  const confirmDeleteWorkspace = async () => {
    if (!deletingWorkspace) return
    setBusy(true)
    try {
      const ids = sessions
        .filter((session) => (session.workspacePath ?? '') === deletingWorkspace.path)
        .map((session) => session.id)
      const result = await invoke<{ sessions: SessionSummaryDto[]; activeId: string | null }>(
        'chat.workspace.delete',
        { path: deletingWorkspace.path, deleteSessions: true },
      )
      ids.forEach((id) => removeChat(id))
      setUpdated(result.sessions, result.activeId)
      // 宿主推送的 workspace op 会同步全量列表，这里先本地兜底移除（无活动会话时宿主不会推 op）
      const app = useAppStore.getState()
      app.setWorkspaces(app.workspaces.filter((item) => item.path !== deletingWorkspace.path))
      if (ids.some((id) => id === activeId)) {
        navigate(chatRoute(result.activeId ?? undefined), true)
      }
      setDeletingWorkspace(null)
    } catch (error) {
      console.warn('delete workspace failed', error)
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
            onClick={() => void createSession()}
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
          <div className="sidebar-section-head" data-searching={searchOpen ? 'true' : 'false'}>
            {searchOpen ? (
              <div className="sidebar-search">
                <Search size={13} className="sidebar-search-icon" />
                <input
                  className="sidebar-search-input"
                  value={query}
                  placeholder="搜索会话名称"
                  aria-label="搜索会话"
                  autoFocus
                  onChange={(event) => setQuery(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === 'Escape') {
                      event.stopPropagation()
                      closeSearch()
                    }
                  }}
                />
                <button
                  type="button"
                  className="icon-btn"
                  aria-label="关闭搜索"
                  onClick={() => closeSearch()}
                >
                  <X size={13} />
                </button>
              </div>
            ) : (
              <>
                <span>工作区</span>
                <span className="spacer" />
                <button
                  ref={searchButton}
                  type="button"
                  className="icon-btn"
                  aria-label="搜索会话"
                  onClick={openSearch}
                >
                  <Search size={14} />
                </button>
                <button type="button" className="icon-btn" aria-label="选择工作区" onClick={() => void pickWorkspace()}>
                  <FolderOpen size={14} />
                </button>
                <button
                  type="button"
                  className="icon-btn"
                  aria-label="新建会话"
                  onClick={() => void createSession()}
                >
                  <Plus size={14} />
                </button>
              </>
            )}
          </div>

          {groups.length === 0 ? (
            <div className="sidebar-empty">
              {query ? '没有匹配的会话。' : '还没有会话，发出第一条消息后会出现在这里。'}
            </div>
          ) : (
            groups.map((group) => {
              const groupKey = group.key || 'default'
              // 搜索时强制展开，避免命中的会话被折叠状态隐藏
              const isCollapsed = !query && (collapsed[groupKey] ?? false)
              return (
                <div className="workspace-group" key={groupKey}>
                  <div className="workspace-title" title={group.key}>
                    <button
                      type="button"
                      className="workspace-toggle"
                      aria-expanded={!isCollapsed}
                      onClick={() => toggleCollapsed(groupKey)}
                    >
                      <span className="workspace-icon">
                        <FolderOpen size={13} className="workspace-folder" />
                        <ChevronRight
                          size={13}
                          className="workspace-chevron"
                          data-expanded={isCollapsed ? 'false' : 'true'}
                        />
                      </span>
                      <span className="name">{group.name}</span>
                    </button>
                    <div className="workspace-actions">
                      {group.key ? (
                        <DropdownMenu.Root>
                          <DropdownMenu.Trigger asChild>
                            <button
                              type="button"
                              className="icon-btn"
                              aria-label={`${group.name} 工作区操作`}
                              onPointerDown={() => {
                                menuByPointer.current = true
                              }}
                              onKeyDown={() => {
                                menuByPointer.current = false
                              }}
                            >
                              <Ellipsis size={13} />
                            </button>
                          </DropdownMenu.Trigger>
                          <DropdownMenu.Portal>
                            <DropdownMenu.Content
                              className="popover menu-list"
                              sideOffset={4}
                              align="end"
                              onCloseAutoFocus={(event) => {
                                // 鼠标关闭时不要让焦点退回触发按钮，否则操作区会一直处于展开态
                                if (menuByPointer.current) event.preventDefault()
                              }}
                            >
                              <DropdownMenu.Item
                                className="menu-item"
                                onSelect={() => startRenameWorkspace(group.key, group.name)}
                              >
                                <Pencil size={14} />
                                重命名
                              </DropdownMenu.Item>
                              <DropdownMenu.Separator className="menu-sep" />
                              <DropdownMenu.Item
                                className="menu-item"
                                onSelect={() => {
                                  setDeletingWorkspace({
                                    path: group.key,
                                    name: group.name,
                                    count: sessions.filter(
                                      (session) => (session.workspacePath ?? '') === group.key,
                                    ).length,
                                  })
                                }}
                              >
                                <Trash2 size={14} style={{ color: 'var(--danger)' }} />
                                <span style={{ color: 'var(--danger)' }}>删除工作区</span>
                              </DropdownMenu.Item>
                            </DropdownMenu.Content>
                          </DropdownMenu.Portal>
                        </DropdownMenu.Root>
                      ) : null}
                      <button
                        type="button"
                        className="icon-btn"
                        aria-label={`在 ${group.name} 新建会话`}
                        onClick={() => void createSession(group.key || undefined)}
                      >
                        <Plus size={13} />
                      </button>
                    </div>
                  </div>
                  <div
                    className="workspace-sessions"
                    data-collapsed={isCollapsed ? 'true' : 'false'}
                    inert={isCollapsed ? true : undefined}
                  >
                    <div className="session-list">
                      {group.sessions.length === 0 ? (
                        // 空工作区至少保留一个新建入口，避免分组存在却无法开始会话
                        <button
                          type="button"
                          className="session-item session-new"
                          onClick={() => void createSession(group.key)}
                        >
                          <Plus size={12} />
                          <span className="title">新会话</span>
                        </button>
                      ) : (
                        group.sessions.map((session) => (
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
                        ))
                      )}
                    </div>
                  </div>
                </div>
              )
            })
          )}
        </section>
      </div>

      <div className="sidebar-foot">
        <div className="account-chip">
          <span className="account-avatar">{pet.initial}</span>
          <div className="account-meta">
            <div className="account-name">{pet.name}</div>
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

      <Dialog
        open={renamingWorkspace !== null}
        onOpenChange={(open) => {
          if (!open) setRenamingWorkspace(null)
        }}
        title="重命名工作区"
        description="只修改侧栏显示名称，磁盘目录与已保存的会话不受影响。"
      >
        <div className="field" style={{ marginTop: 12 }}>
          <label className="field-label" htmlFor="workspace-name">
            工作区名称
          </label>
          <input
            id="workspace-name"
            className="input"
            value={workspaceTitle}
            maxLength={40}
            onChange={(event) => setWorkspaceTitle(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') void saveWorkspaceName()
            }}
            autoFocus
          />
        </div>
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setRenamingWorkspace(null)}>
            取消
          </button>
          <button
            type="button"
            className="btn btn-primary"
            disabled={busy || !workspaceTitle.trim()}
            onClick={() => void saveWorkspaceName()}
          >
            保存
          </button>
        </div>
      </Dialog>

      <Dialog
        open={deletingWorkspace !== null}
        onOpenChange={(open) => {
          if (!open) setDeletingWorkspace(null)
        }}
        title="删除工作区"
        description={`「${deletingWorkspace?.name ?? ''}」下的 ${deletingWorkspace?.count ?? 0} 个会话会移入回收站，聊天记录可从 sessions/.deleted 恢复。`}
      >
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setDeletingWorkspace(null)}>
            取消
          </button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={() => void confirmDeleteWorkspace()}>
            删除
          </button>
        </div>
      </Dialog>
    </aside>
  )
}
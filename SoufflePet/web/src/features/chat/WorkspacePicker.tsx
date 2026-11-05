// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Check, ChevronDown, Folder, FolderPlus, Trash2, X } from 'lucide-react'
import { useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ChatSnapshotDto, WorkspaceDto } from '@/bridge/contracts'
import { Popover } from '@/ui/Popover'

export function WorkspacePicker({ chat }: { chat: ChatSnapshotDto }) {
  const [open, setOpen] = useState(false)
  const [pendingDelete, setPendingDelete] = useState<string | null>(null)

  const select = async (workspace: WorkspaceDto | null) => {
    try {
      await invoke('chat.workspace.switch', { path: workspace?.path ?? '' })
      setOpen(false)
    } catch (error) {
      console.warn('switch workspace failed', error)
    }
  }

  const pick = async () => {
    try {
      const workspace = await invoke<WorkspaceDto | null>('chat.workspace.pick')
      if (workspace) await select(workspace)
    } catch (error) {
      console.warn('pick workspace failed', error)
    }
  }

  const remove = async (path: string) => {
    try {
      await invoke('chat.workspace.delete', { path })
      setPendingDelete(null)
    } catch (error) {
      console.warn('delete workspace failed', error)
    }
  }

  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        setOpen(next)
        if (!next) setPendingDelete(null)
      }}
      side="top"
      align="start"
      className="popover-wide"
      trigger={
        <button type="button" className="chip" aria-label="工作区">
          <Folder size={13} />
          <span className="chip-label">{chat.workspace?.name ?? '选择工作区'}</span>
          <ChevronDown size={12} />
        </button>
      }
    >
      <div className="menu-label">工作区</div>
      {chat.workspaces.length === 0 ? (
        <div className="sidebar-empty">还没有工作区，选一个目录开始。</div>
      ) : (
        chat.workspaces.map((workspace) => {
          const active = workspace.path === chat.workspace?.path
          const confirming = pendingDelete === workspace.path
          return (
            <div key={workspace.path}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
                <button
                  type="button"
                  className="menu-item"
                  data-active={active ? 'true' : 'false'}
                  onClick={() => void select(workspace)}
                  title={workspace.path}
                  style={{ flex: 1, minWidth: 0 }}
                >
                  <Folder size={14} style={{ flex: 'none' }} />
                  <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', textAlign: 'left' }}>
                    {workspace.name}
                  </span>
                  {active ? <Check size={14} className="menu-check" /> : null}
                </button>
                <button
                  type="button"
                  className="icon-btn"
                  aria-label={`移除工作区 ${workspace.name}`}
                  onClick={() => setPendingDelete(confirming ? null : workspace.path)}
                  style={{ width: 22, height: 22, flex: 'none' }}
                >
                  {confirming ? <X size={12} /> : <Trash2 size={12} />}
                </button>
              </div>
              {confirming ? (
                <div
                  style={{
                    display: 'flex',
                    gap: 6,
                    alignItems: 'center',
                    padding: '4px 8px 6px 30px',
                    fontSize: 11.5,
                    color: 'var(--text-3)',
                  }}
                >
                  从历史里移除？
                  <button type="button" className="btn btn-sm btn-danger" onClick={() => void remove(workspace.path)}>
                    移除
                  </button>
                  <button type="button" className="btn btn-sm btn-ghost" onClick={() => setPendingDelete(null)}>
                    取消
                  </button>
                </div>
              ) : null}
            </div>
          )
        })
      )}
      <div className="menu-sep" />
      <button type="button" className="menu-item" onClick={() => void pick()}>
        <FolderPlus size={14} />
        选择目录…
      </button>
    </Popover>
  )
}
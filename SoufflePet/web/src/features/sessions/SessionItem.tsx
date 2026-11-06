// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import * as DropdownMenu from '@radix-ui/react-dropdown-menu'
import { Loader2, MoreHorizontal, Pencil, Trash2 } from 'lucide-react'
import { useRef } from 'react'
import type { SessionSummaryDto } from '@/bridge/contracts'
import { useChatStore } from '@/stores/chatStore'
import { formatRelative } from '@/ui/format'

interface SessionItemProps {
  session: SessionSummaryDto
  active: boolean
  onOpen: (session: SessionSummaryDto) => void
  onRename: (session: SessionSummaryDto) => void
  onDelete: (session: SessionSummaryDto) => void
}

export function SessionItem({ session, active, onOpen, onRename, onDelete }: SessionItemProps) {
  // 记录菜单是否由指针打开：关闭时据此决定是否把焦点还给触发按钮
  const menuByPointer = useRef(false)
  // 询问工具在等这个会话回答时，用阻塞圆点替代加载图标
  const blocked = useChatStore((state) => Boolean(state.bySession[session.id]?.ask))

  return (
    <div
      className="session-item"
      data-active={active ? 'true' : 'false'}
      role="button"
      tabIndex={0}
      title={session.title}
      onClick={() => onOpen(session)}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onOpen(session)
        }
      }}
    >
      {blocked ? (
        <span className="session-blocked" role="img" aria-label="等待你的回答" title="等待你的回答">
          <span className="dot" />
        </span>
      ) : session.generating ? (
        <span className="session-spin" aria-hidden>
          <Loader2 size={12} />
        </span>
      ) : null}
      <span className="title">{session.title}</span>
      <span className="time">{formatRelative(session.updatedAt)}</span>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <button
            type="button"
            className="icon-btn session-more"
            aria-label="会话操作"
            onClick={(event) => event.stopPropagation()}
            onPointerDown={() => {
              menuByPointer.current = true
            }}
            onKeyDown={() => {
              menuByPointer.current = false
            }}
          >
            <MoreHorizontal size={14} />
          </button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content
            className="popover menu-list"
            sideOffset={4}
            align="end"
            onCloseAutoFocus={(event) => {
              // 鼠标关闭时不要让焦点退回触发按钮，否则操作按钮会一直处于显示态
              if (menuByPointer.current) event.preventDefault()
            }}
          >
            <DropdownMenu.Item
              className="menu-item"
              onSelect={() => onRename(session)}
            >
              <Pencil size={14} />
              重命名
            </DropdownMenu.Item>
            <DropdownMenu.Separator className="menu-sep" />
            <DropdownMenu.Item className="menu-item" onSelect={() => onDelete(session)}>
              <Trash2 size={14} style={{ color: 'var(--danger)' }} />
              <span style={{ color: 'var(--danger)' }}>删除会话</span>
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
    </div>
  )
}
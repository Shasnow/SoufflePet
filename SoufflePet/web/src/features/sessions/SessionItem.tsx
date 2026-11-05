// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import * as DropdownMenu from '@radix-ui/react-dropdown-menu'
import { MoreHorizontal, Pencil, Trash2 } from 'lucide-react'
import type { SessionSummaryDto } from '@/bridge/contracts'
import { formatRelative } from '@/ui/format'

interface SessionItemProps {
  session: SessionSummaryDto
  active: boolean
  onOpen: (session: SessionSummaryDto) => void
  onRename: (session: SessionSummaryDto) => void
  onDelete: (session: SessionSummaryDto) => void
}

export function SessionItem({ session, active, onOpen, onRename, onDelete }: SessionItemProps) {
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
      <span className="title">{session.title}</span>
      <span className="time">{formatRelative(session.updatedAt)}</span>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <button
            type="button"
            className="icon-btn session-more"
            aria-label="会话操作"
            onClick={(event) => event.stopPropagation()}
          >
            <MoreHorizontal size={14} />
          </button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content className="popover menu-list" sideOffset={4} align="end">
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
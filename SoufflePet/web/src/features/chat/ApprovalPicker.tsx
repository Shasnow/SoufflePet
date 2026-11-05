// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Check, ChevronDown, ShieldAlert, ShieldCheck } from 'lucide-react'
import { useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ApprovalMode } from '@/bridge/contracts'
import { Popover } from '@/ui/Popover'

const MODES: { id: ApprovalMode; label: string; description: string; icon: typeof ShieldCheck }[] = [
  {
    id: 'manual',
    label: '手动审批',
    description: '执行命令或发送文件前询问',
    icon: ShieldCheck,
  },
  {
    id: 'full',
    label: '完全权限',
    description: '直接执行有副作用的工具',
    icon: ShieldAlert,
  },
]

export function ApprovalPicker({ mode }: { mode: ApprovalMode }) {
  const [open, setOpen] = useState(false)
  const current = MODES.find((item) => item.id === mode) ?? MODES[0]
  const Icon = current.icon

  const select = async (next: ApprovalMode) => {
    try {
      await invoke('chat.approval.mode', { mode: next })
      setOpen(false)
    } catch (error) {
      console.warn('set approval mode failed', error)
    }
  }

  return (
    <Popover
      open={open}
      onOpenChange={setOpen}
      side="top"
      align="start"
      trigger={
        <button type="button" className="chip" aria-label="审批模式">
          <Icon size={13} />
          <span className="chip-label">{current.label}</span>
          <ChevronDown size={12} />
        </button>
      }
    >
      <div className="menu-label">审批模式</div>
      {MODES.map((item) => {
        const ItemIcon = item.icon
        return (
          <button
            key={item.id}
            type="button"
            className="menu-item"
            data-active={item.id === mode ? 'true' : 'false'}
            onClick={() => void select(item.id)}
            style={{ alignItems: 'flex-start' }}
          >
            <ItemIcon size={15} style={{ marginTop: 2, flex: 'none' }} />
            <span style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
              <span>{item.label}</span>
              <span style={{ color: 'var(--text-3)', fontSize: 11.5 }}>{item.description}</span>
            </span>
            {item.id === mode ? <Check size={14} className="menu-check" /> : null}
          </button>
        )
      })}
    </Popover>
  )
}
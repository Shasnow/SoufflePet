// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Check, ChevronDown, Cpu, Settings2 } from 'lucide-react'
import { useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ChatSnapshotDto } from '@/bridge/contracts'
import { Popover } from '@/ui/Popover'
import { navigate } from '@/app/router'

export function ModelPicker({ chat }: { chat: ChatSnapshotDto }) {
  const [open, setOpen] = useState(false)
  const selected = chat.models.find((model) => model.key === chat.selectedModelKey)
  const groups = new Map<string, typeof chat.models>()
  for (const model of chat.models) {
    if (!groups.has(model.provider)) groups.set(model.provider, [])
    groups.get(model.provider)!.push(model)
  }

  const select = async (key: string) => {
    try {
      await invoke('chat.model.select', { key })
      setOpen(false)
    } catch (error) {
      console.warn('select model failed', error)
    }
  }

  return (
    <Popover
      open={open}
      onOpenChange={setOpen}
      side="top"
      align="start"
      className="popover-wide"
      trigger={
        <button type="button" className="composer-tool" aria-label="选择对话模型">
          <Cpu size={14} />
          <span className="tool-text">{selected ? `${selected.provider} · ${selected.name}` : '选择模型'}</span>
          <ChevronDown size={12} />
        </button>
      }
    >
      <div className="menu-label">对话模型</div>
      {groups.size === 0 ? (
        <div className="sidebar-empty">还没有可用模型，先到「对话模型」里配置提供商。</div>
      ) : (
        [...groups.entries()].map(([provider, models]) => (
          <div key={provider}>
            <div className="menu-label">{provider}</div>
            {models.map((model) => (
              <button
                key={model.key}
                type="button"
                className="menu-item"
                data-active={model.key === chat.selectedModelKey ? 'true' : 'false'}
                onClick={() => void select(model.key)}
              >
                <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                  {model.name}
                </span>
                {model.key === chat.selectedModelKey ? <Check size={14} className="menu-check" /> : null}
              </button>
            ))}
          </div>
        ))
      )}
      <div className="menu-sep" />
      <button
        type="button"
        className="menu-item"
        onClick={() => {
          setOpen(false)
          navigate({ kind: 'settings', panel: 'llm' })
        }}
      >
        <Settings2 size={14} />
        管理模型
      </button>
    </Popover>
  )
}
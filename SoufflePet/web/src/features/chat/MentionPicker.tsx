// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { FileText, Folder } from 'lucide-react'
import { useEffect, useRef } from 'react'

/** 工作区里的一个候选条目；path 相对工作区，目录不带结尾斜杠。 */
export interface MentionEntry {
  name: string
  path: string
  isDirectory: boolean
}

interface MentionPickerProps {
  entries: MentionEntry[]
  activeIndex: number
  loading: boolean
  onHover: (index: number) => void
  onPick: (entry: MentionEntry) => void
}

/** 输入框里 @ 的候选列表：浮在输入卡上方，方向键选择、Tab / 回车确认。 */
export function MentionPicker({ entries, activeIndex, loading, onHover, onPick }: MentionPickerProps) {
  const listRef = useRef<HTMLDivElement>(null)

  // 方向键把当前项移出可视范围时，让列表跟着滚动，保证高亮项始终可见
  useEffect(() => {
    const active = listRef.current?.querySelector<HTMLElement>(`[data-index="${activeIndex}"]`)
    active?.scrollIntoView?.({ block: 'nearest' })
  }, [activeIndex, entries])

  return (
    <div
      className="mention-popover"
      role="listbox"
      aria-label="引用工作区文件"
      ref={listRef}
      // 浮层内任意位置（含空白、内边距）按下都不抢焦点：否则 textarea 失焦会立刻把列表关掉
      onMouseDown={(event) => event.preventDefault()}
    >
      {entries.length === 0 ? (
        <div className="mention-empty">{loading ? '搜索中…' : '没有匹配的文件'}</div>
      ) : (
        entries.map((entry, index) => (
          <button
            key={entry.path}
            type="button"
            role="option"
            data-index={index}
            aria-selected={index === activeIndex}
            className={`mention-item${index === activeIndex ? ' is-active' : ''}`}
            onMouseEnter={() => onHover(index)}
            // 用 mousedown 并阻止默认行为：否则 textarea 先失焦，列表会被关掉
            onMouseDown={(event) => {
              event.preventDefault()
              onPick(entry)
            }}
          >
            {entry.isDirectory ? <Folder size={14} /> : <FileText size={14} />}
            <span className="mention-name">{entry.isDirectory ? `${entry.name}/` : entry.name}</span>
            {entry.isDirectory ? <span className="mention-hint">进入目录 Tab ›</span> : null}
          </button>
        ))
      )}
    </div>
  )
}

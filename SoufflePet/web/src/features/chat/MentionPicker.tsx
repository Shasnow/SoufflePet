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

/** 候选项 id 的统一约定：输入框的 aria-activedescendant 与选项自身共用同一个生成规则。 */
export function mentionOptionId(listboxId: string, index: number): string {
  return `${listboxId}-option-${index}`
}

interface MentionPickerProps {
  /** 列表容器 id：输入框用 aria-controls 指向它，选项 id 以它为前缀。 */
  listboxId: string
  entries: MentionEntry[]
  activeIndex: number
  loading: boolean
  onHover: (index: number) => void
  onPick: (entry: MentionEntry) => void
}

/**
 * 输入框里 @ 的候选列表：浮在输入卡上方，方向键选择、Tab / 回车确认。
 * 与命令面板一样是 listbox + option 结构：候选不可聚焦，当前项由输入框的
 * aria-activedescendant 标出；没有候选时容器退化成 role="status" 的空态提示。
 */
export function MentionPicker({ listboxId, entries, activeIndex, loading, onHover, onPick }: MentionPickerProps) {
  const listRef = useRef<HTMLDivElement>(null)
  const empty = entries.length === 0

  // 方向键把当前项移出可视范围时，让列表跟着滚动，保证高亮项始终可见
  useEffect(() => {
    const active = listRef.current?.querySelector<HTMLElement>(`[data-index="${activeIndex}"]`)
    active?.scrollIntoView?.({ block: 'nearest' })
  }, [activeIndex, entries])

  return (
    <div
      className="mention-popover"
      // 没有候选时不声明 listbox（listbox 只允许 option 子节点），退化成播报空态的区域
      id={empty ? undefined : listboxId}
      role={empty ? undefined : 'listbox'}
      aria-label={empty ? undefined : '引用工作区文件'}
      ref={listRef}
      // 浮层内任意位置（含空白、内边距）按下都不抢焦点：否则 textarea 失焦会立刻把列表关掉
      onMouseDown={(event) => event.preventDefault()}
    >
      {empty ? (
        <div className="mention-empty" role="status">
          {loading ? '搜索中…' : '没有匹配的文件'}
        </div>
      ) : (
        entries.map((entry, index) => (
          <div
            key={entry.path}
            id={mentionOptionId(listboxId, index)}
            role="option"
            data-index={index}
            aria-selected={index === activeIndex}
            aria-label={entry.isDirectory ? `${entry.name}/` : entry.name}
            className={`mention-item${index === activeIndex ? ' is-active' : ''}`}
            onMouseEnter={() => onHover(index)}
            // 用 mousedown 并阻止默认行为：否则 textarea 先失焦，列表会被关掉
            onMouseDown={(event) => {
              event.preventDefault()
              onPick(entry)
            }}
            // 键盘 / 脚本触发的合成点击（detail 为 0）没有 mousedown，在这里兜底
            onClick={(event) => {
              if (event.detail > 0) return
              onPick(entry)
            }}
          >
            {entry.isDirectory ? <Folder size={14} aria-hidden="true" /> : <FileText size={14} aria-hidden="true" />}
            <span className="mention-name">{entry.isDirectory ? `${entry.name}/` : entry.name}</span>
            {entry.isDirectory ? <span className="mention-hint">进入目录 Tab ›</span> : null}
          </div>
        ))
      )}
    </div>
  )
}

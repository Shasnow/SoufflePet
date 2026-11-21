// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Check, Terminal } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { handleSlashKey, type SlashSuggestion, type SlashView } from './slashCommands'

/** 候选 id 的统一约定：输入框的 aria-activedescendant 与选项自身共用同一个生成规则。 */
export function slashOptionId(listboxId: string, index: number): string {
  return `${listboxId}-option-${index}`
}

interface SlashCommandMenuProps {
  /** 列表容器 id：输入框用 aria-controls 指向它，选项 id 以它为前缀。 */
  listboxId: string
  view: SlashView
  activeIndex: number
  /** 由按钮 / 消息操作菜单唤起时为 true：面板自己接管键盘焦点，关闭时归还给唤起者。 */
  detached?: boolean
  onHover: (index: number) => void
  onPick: (suggestion: SlashSuggestion) => void
  onMove: (index: number) => void
  onClose: () => void
}

/**
 * 命令面板：浮在输入卡上方，顶部是当前命令的用法预览，下面是命令或参数候选。
 *
 * 可访问性：列表是 `role="listbox"`，候选是 `role="option"`（不可聚焦，用 aria-activedescendant 标记当前项）。
 * 输入框触发时焦点留在 textarea（combobox），面板只读 aria-activedescendant；
 * 从按钮 / 消息操作菜单唤起时（detached）面板自己接管焦点，关闭后归还给唤起它的元素。
 */
export function SlashCommandMenu({
  listboxId,
  view,
  activeIndex,
  detached = false,
  onHover,
  onPick,
  onMove,
  onClose,
}: SlashCommandMenuProps) {
  const listRef = useRef<HTMLDivElement>(null)
  const containerRef = useRef<HTMLDivElement>(null)
  // 唤起面板前的焦点，关闭时（Escape / 选中）还回去；点到别处时不抢焦点
  const returnFocusRef = useRef<HTMLElement | null>(null)
  const descriptionId = `${listboxId}-description`
  const empty = view.suggestions.length === 0
  const activeDescendant = detached && !empty
    ? slashOptionId(listboxId, Math.min(activeIndex, view.suggestions.length - 1))
    : undefined

  // 方向键把当前项移出可视范围时，让列表跟着滚动，保证高亮项始终可见
  useEffect(() => {
    const active = listRef.current?.querySelector<HTMLElement>(`[data-index="${activeIndex}"]`)
    active?.scrollIntoView?.({ block: 'nearest' })
  }, [activeIndex, view])

  // detached：记录来源焦点并把焦点交给列表；关闭时归还给唤起者（Esc / 选中 / 组件卸载）
  useEffect(() => {
    if (!detached) return

    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const listbox = listRef.current
    returnFocusRef.current = previous
    listbox?.focus()

    return () => {
      const active = document.activeElement
      // 焦点还在列表里（Esc / 选中 / 卸载），或压根没落到别处（body）时归还；
      // 用户点到面板之外并让别的元素拿到焦点时不要抢回来。
      const stillInPanel = listbox !== null && active instanceof Node && listbox.contains(active)
      const nowhere = !(active instanceof HTMLElement) || active === document.body
      if (stillInPanel || nowhere) previous?.focus?.()
      returnFocusRef.current = null
    }
  }, [detached])

  return (
    <div
      className="slash-popover"
      ref={containerRef}
      data-detached={detached ? 'true' : 'false'}
      // 面板内任意位置（含空白、内边距）按下都不抢焦点：否则输入框失焦会立刻把面板关掉
      onMouseDown={(event) => event.preventDefault()}
    >
      <div className="slash-preview">
        <div className="slash-preview-head">
          <Terminal size={12} aria-hidden="true" />
          <code className="slash-usage">{view.usage}</code>
          {view.command?.argumentLabel ? (
            <span className="slash-preview-arg">参数：{view.command.argumentLabel}</span>
          ) : null}
        </div>
        <p className="slash-description" id={descriptionId}>
          {view.description}
        </p>
      </div>

      <div
        className="slash-list"
        id={listboxId}
        role="listbox"
        aria-label="命令面板"
        aria-describedby={descriptionId}
        aria-activedescendant={activeDescendant}
        ref={listRef}
        // detached 时列表自己接键盘；输入框触发时键盘仍由 textarea 处理，这里不抢焦点
        tabIndex={detached ? -1 : undefined}
        onKeyDown={
          detached
            ? (event) => {
                const result = handleSlashKey(event, view, activeIndex)
                if (!result) {
                  if (event.key === 'Escape') onClose()
                  return
                }
                event.preventDefault()
                if (result.type === 'move') onMove(result.index)
                else if (result.type === 'pick') onPick(result.suggestion)
                else onClose()
              }
            : undefined
        }
      >
        {view.suggestions.map((suggestion, index) => (
          <div
            key={`${suggestion.kind}-${suggestion.value}-${index}`}
            id={slashOptionId(listboxId, index)}
            role="option"
            data-index={index}
            aria-selected={index === activeIndex}
            aria-label={suggestion.detail ? `${suggestion.label} · ${suggestion.detail}` : suggestion.label}
            className={`slash-item${index === activeIndex ? ' is-active' : ''}`}
            onMouseEnter={() => onHover(index)}
            // 用 mousedown 并阻止默认行为：否则输入框先失焦，面板会被关掉
            onMouseDown={(event) => {
              event.preventDefault()
              onPick(suggestion)
            }}
            // 键盘 / 脚本触发的合成点击（detail 为 0）没有 mousedown，在这里兜底
            onClick={(event) => {
              if (event.detail > 0) return
              onPick(suggestion)
            }}
          >
            <span className="slash-item-label">{suggestion.label}</span>
            {suggestion.detail ? <span className="slash-item-detail">{suggestion.detail}</span> : null}
            {suggestion.selected ? <Check size={13} className="menu-check" aria-hidden="true" /> : null}
          </div>
        ))}
      </div>

      {/* 空态放在 listbox 外面：listbox 只允许 option 子节点，这里用 status 让读屏能播报 */}
      {empty ? (
        <div className="slash-empty" role="status">
          {view.command ? `没有匹配「${view.query}」的参数` : '没有匹配的命令'}
          <span className="slash-empty-hint">按回车按当前内容执行或发送</span>
        </div>
      ) : null}

      <div className="slash-foot">
        <span>{view.command ? '选择参数后立即执行' : '选择命令后继续补全参数'}</span>
        <span className="spacer" />
        <span>↑↓ 选择 · Tab/Enter 确认 · Esc 关闭</span>
      </div>
    </div>
  )
}

// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ChatSnapshotDto, SlashCommandDto } from '@/bridge/contracts'

/** 命令的触发字符：输入以它开头即进入命令模式。 */
export const SLASH_TRIGGER = '/'

/** 一条补全候选：命令名，或命令的参数值。 */
export interface SlashSuggestion {
  kind: 'command' | 'argument'
  /** 选中后写进输入框（或直接交给宿主执行）的值。 */
  value: string
  label: string
  detail?: string
  /** 参数候选是否为当前值（已选中的模型、当前审批模式）。 */
  selected?: boolean
  /** 命令阶段的候选带上定义，供预览区显示用法与说明。 */
  command?: SlashCommandDto
}

/** 命令面板的完整视图：command 为空表示在选命令，否则表示已进入该命令的参数阶段。 */
export interface SlashView {
  command: SlashCommandDto | null
  query: string
  suggestions: SlashSuggestion[]
  /** 预览区标题（命令用法）。 */
  usage: string
  /** 预览区说明。 */
  description: string
}

/** 解析后的斜杠输入：命令名 + 参数，以及命令名与参数之间是否有分隔空格。 */
export interface SlashInput {
  name: string
  argument: string
  hasArgumentSeparator: boolean
}

/**
 * 命令名与参数的分界字符集，逐字对应 .NET 的 `char.IsWhiteSpace`：
 * 含全角空格 U+3000、NBSP U+00A0、NEL U+0085 等，但**不含** BOM U+FEFF（.NET 也不把它当空白）。
 * 这里用显式字符类而不是 `/\s/`：后者多认 BOM，会与宿主产生
 * 「前端认作『命令 + 参数』、宿主却拼成不存在的命令名」的偏差。
 */
const WHITESPACE_CLASS =
  '\\t\\n\\v\\f\\r \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000'
const WHITESPACE = new RegExp(`[${WHITESPACE_CLASS}]`)
const LEADING_WHITESPACE = new RegExp(`^[${WHITESPACE_CLASS}]+`)
const TRAILING_WHITESPACE = new RegExp(`[${WHITESPACE_CLASS}]+$`)
const BARE_TRIGGER = new RegExp(`^[${WHITESPACE_CLASS}]*\\/[${WHITESPACE_CLASS}]*$`)

/** 去掉首尾空白（与 .NET `string.Trim` 的字符集一致）。 */
function trimWhitespace(value: string): string {
  return value.replace(LEADING_WHITESPACE, '').replace(TRAILING_WHITESPACE, '')
}

/** 输入（可带前置空白）是否以斜杠触发字符开头；Composer 用它判断是否进入命令模式。 */
export function startsWithSlashTrigger(value: string): boolean {
  return value.replace(LEADING_WHITESPACE, '').startsWith(SLASH_TRIGGER)
}

/**
 * 解析输入为命令。只认单行内容：命令从行首开始，命令名到第一个空白为止，
 * 其余为参数。结尾的空格保留（`/model ` 表示已进入参数阶段，界面据此列出模型），
 * 不是斜杠输入（或只有斜杠）时返回 null，交给普通文本处理。
 */
export function parseSlashInput(input: string): SlashInput | null {
  if (/[\r\n]/.test(input)) return null
  const trimmed = input.replace(LEADING_WHITESPACE, '')
  if (!trimmed.startsWith(SLASH_TRIGGER)) return null

  const body = trimmed.slice(1)
  if (body.length === 0) return null

  const separator = body.search(WHITESPACE)
  const name = separator < 0 ? body : body.slice(0, separator)
  if (name.length === 0) return null

  const argument = separator < 0 ? '' : trimWhitespace(body.slice(separator + 1))
  return { name, argument, hasArgumentSeparator: separator >= 0 }
}

/** 按命令名或别名查找（忽略大小写）。 */
export function findCommand(commands: SlashCommandDto[], name: string): SlashCommandDto | null {
  const key = name.trim().replace(/^\//, '')
  if (!key) return null
  const lowered = key.toLowerCase()
  return (
    commands.find((command) => command.name.toLowerCase() === lowered) ??
    commands.find((command) => command.aliases.some((alias) => alias.toLowerCase() === lowered)) ??
    null
  )
}

/**
 * 模糊打分：精确 > 前缀 > 包含 > 子序列，越高越匹配，-1 表示不匹配。
 * 与宿主 SlashCommands 的评分规则同构，保证界面候选顺序与执行结果一致。
 */
export function fuzzyScore(candidate: string | undefined, query: string): number {
  const text = (candidate ?? '').trim().toLowerCase()
  const pattern = query.trim().toLowerCase()
  if (pattern.length === 0) return 1
  if (text.length === 0) return -1

  if (text === pattern) return 1000
  if (text.startsWith(pattern)) return 900 - Math.min(200, text.length - pattern.length)

  const at = text.indexOf(pattern)
  if (at >= 0) return 700 - Math.min(200, at)

  // 子序列匹配：跳过越多字符分数越低，「dschat」也能命中「deepseek-chat」
  let gaps = 0
  let cursor = 0
  for (const character of pattern) {
    const found = text.indexOf(character, cursor)
    if (found < 0) return -1
    gaps += found - cursor
    cursor = found + 1
  }

  return 500 - Math.min(400, gaps)
}

/** 按查询给候选打分排序；同分保持原有顺序（模型列表顺序即平台给出的顺序）。 */
export function rankSuggestions<T>(items: T[], query: string, fields: (item: T) => (string | undefined)[]): T[] {
  return items
    .map((item, index) => ({
      item,
      index,
      score: fields(item).reduce((best, field) => Math.max(best, fuzzyScore(field, query)), -1),
    }))
    .filter((entry) => entry.score >= 0)
    .sort((left, right) => right.score - left.score || left.index - right.index)
    .map((entry) => entry.item)
}

/** 某条命令在当前会话上下文里的参数候选。 */
export function argumentSuggestions(command: SlashCommandDto, chat: ChatSnapshotDto): SlashSuggestion[] {
  if (command.argumentKind === 'model') {
    return chat.models.map((model) => ({
      kind: 'argument' as const,
      value: model.name,
      label: model.name,
      detail: `${model.provider} · ${model.modelId}`,
      selected: model.key === chat.selectedModelKey,
    }))
  }

  if (command.argumentKind === 'approval') {
    return [
      {
        kind: 'argument' as const,
        value: 'manual',
        label: 'manual',
        detail: '每次调用工具都需要确认',
        selected: chat.approvalMode === 'manual',
      },
      {
        kind: 'argument' as const,
        value: 'full',
        label: 'full',
        detail: '直接放行工具调用',
        selected: chat.approvalMode === 'full',
      },
    ]
  }

  return []
}

/**
 * 候选列表的初始高亮位置。
 *
 * 参数阶段落在「当前值」上（模型列表里已选中的模型、审批模式当前值），
 * 这样面板一打开直接按 Enter 只会重新确认当前状态，不会静默切到列表第一项
 *（`/model` + Enter 曾会切到 models[0]，`/approval` + Enter 曾会直接改审批模式）。
 * 命令阶段（还在选命令名）没有「当前值」，仍从第一项开始。
 */
export function defaultSuggestionIndex(view: SlashView | null): number {
  if (!view || view.command === null) return 0

  const index = view.suggestions.findIndex((item) => item.selected)
  return index >= 0 ? index : 0
}

function commandSuggestions(commands: SlashCommandDto[], query: string): SlashSuggestion[] {
  return rankSuggestions(commands, query, (command) => [
    command.name,
    ...command.aliases,
    command.summary,
  ]).map((command) => ({
    kind: 'command' as const,
    value: command.name,
    label: command.usage,
    detail: command.summary,
    command,
  }))
}

/** 命令列表视图（按钮 / 消息操作菜单直接唤起时用，没有输入框也成立）。 */
export function commandListView(commands: SlashCommandDto[]): SlashView {
  return {
    command: null,
    query: '',
    suggestions: commandSuggestions(commands, ''),
    usage: '命令',
    description: '选择一条命令；带参数的命令会继续列出可用参数。',
  }
}

/** 参数列表视图：已选中命令，列出它的全部参数候选。 */
export function argumentView(commands: SlashCommandDto[], command: SlashCommandDto, chat: ChatSnapshotDto): SlashView {
  const args = argumentSuggestions(command, chat)
  const target = findCommand(commands, command.name) ?? command
  return {
    command: target,
    query: '',
    suggestions: args,
    usage: target.usage,
    description: target.description,
  }
}

/**
 * 根据输入框内容给出面板视图：`/mo` → 命令候选，`/model ` → 该会话的模型候选。
 * 输入不是命令时返回 null（界面据此关闭面板）。
 */
export function resolveSlashView(
  commands: SlashCommandDto[],
  chat: ChatSnapshotDto,
  input: string,
): SlashView | null {
  const parsed = parseSlashInput(input)
  if (!parsed) {
    // 只输入了触发字符：还没到命令名，先把全部命令列出来
    if (!/[\r\n]/.test(input) && BARE_TRIGGER.test(input)) return commandListView(commands)
    return null
  }

  // 还没输入空格：仍在选命令
  if (!parsed.hasArgumentSeparator) {
    const suggestions = commandSuggestions(commands, parsed.name)
    const highlighted = suggestions[0]?.command
    return {
      command: null,
      query: parsed.name,
      suggestions,
      usage: highlighted?.usage ?? '命令',
      description: highlighted?.description ?? '没有匹配的命令，按回车把当前内容发给模型。',
    }
  }

  const command = findCommand(commands, parsed.name)
  if (!command) {
    return {
      command: null,
      query: parsed.name,
      suggestions: [],
      usage: `/${parsed.name}`,
      description: '没有这条命令；按回车会把当前内容作为普通消息发送。',
    }
  }

  const args = argumentSuggestions(command, chat)
  return {
    command,
    query: parsed.argument,
    suggestions: rankSuggestions(args, parsed.argument, (arg) => [arg.label, arg.value, arg.detail]),
    usage: command.usage,
    description: command.description,
  }
}

/** 选中命令后写回输入框的文本：补一个空格，方便继续输入参数。 */
export function draftAfterPick(command: SlashCommandDto | null, suggestion: SlashSuggestion): string {
  if (suggestion.kind === 'argument') {
    return `${SLASH_TRIGGER}${command?.name ?? ''} ${suggestion.value}`.trimEnd()
  }

  return `${SLASH_TRIGGER}${suggestion.value} `
}

/** 把一次选择变成要交给宿主执行的一行命令。 */
export function commandText(command: SlashCommandDto | null, suggestion: SlashSuggestion): string {
  if (suggestion.kind === 'command') return `${SLASH_TRIGGER}${suggestion.value}`
  return `${SLASH_TRIGGER}${command?.name ?? ''} ${suggestion.value}`.trimEnd()
}

/** 面板里的键盘操作结果：移动高亮、确认候选、关闭面板，或交给输入框处理。 */
export type SlashKeyResult =
  | { type: 'move'; index: number }
  | { type: 'pick'; suggestion: SlashSuggestion }
  | { type: 'close' }
  | null

/** 键盘处理所需的按键信息：从 KeyboardEvent 取最小子集，便于纯函数单测。 */
export interface SlashKeyEvent {
  key: string
  shiftKey?: boolean
  ctrlKey?: boolean
  altKey?: boolean
  metaKey?: boolean
}

/**
 * 统一的键盘处理：↑↓ 移动、Tab / 回车确认、Esc 关闭。
 *
 * 带任何修饰键（Shift / Ctrl / Alt / Meta）的组合键**一律不接管**，返回 null 交还输入框默认行为：
 * Shift+Enter 换行、Shift+Tab 反向切换焦点、Ctrl/Alt/Meta+Enter 等都不该被 preventDefault 当成「确认候选」。
 * 没有候选时同样返回 null，调用方照常处理回车（发送消息 / 执行已输入的命令）。
 */
export function handleSlashKey(
  event: SlashKeyEvent,
  view: SlashView | null,
  activeIndex: number,
): SlashKeyResult {
  if (!view) return null

  if (event.key === 'Escape') return { type: 'close' }
  if (view.suggestions.length === 0) return null
  if (event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return null

  if (event.key === 'ArrowDown') return { type: 'move', index: (activeIndex + 1) % view.suggestions.length }
  if (event.key === 'ArrowUp') {
    return { type: 'move', index: (activeIndex - 1 + view.suggestions.length) % view.suggestions.length }
  }
  if (event.key === 'Enter' || event.key === 'Tab') {
    return { type: 'pick', suggestion: view.suggestions[Math.min(activeIndex, view.suggestions.length - 1)] }
  }

  return null
}

let catalogPromise: Promise<SlashCommandDto[]> | null = null

/** 读取宿主侧的命令目录（进程内缓存；空结果不缓存，下次挂载时重试）。 */
export function loadSlashCommands(): Promise<SlashCommandDto[]> {
  if (!catalogPromise) {
    catalogPromise = invoke<{ commands: SlashCommandDto[] }>('chat.command.list')
      .then((result) => {
        const commands = result?.commands ?? []
        // 宿主还没就绪（或确实没有命令）时不缓存，下次重新拉取
        if (commands.length === 0) catalogPromise = null
        return commands
      })
      .catch(() => {
        catalogPromise = null
        return []
      })
  }

  return catalogPromise
}

/** 组件用的命令目录：首帧为空，宿主返回后填充（期间输入仍是普通文本）。 */
export function useSlashCommands(): SlashCommandDto[] {
  const [commands, setCommands] = useState<SlashCommandDto[]>([])

  useEffect(() => {
    let alive = true
    void loadSlashCommands().then((list) => {
      if (alive) setCommands(list)
    })
    return () => {
      alive = false
    }
  }, [])

  return commands
}

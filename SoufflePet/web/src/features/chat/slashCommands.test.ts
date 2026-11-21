// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import type { ChatSnapshotDto, SlashCommandDto } from '@/bridge/contracts'
import {
  argumentSuggestions,
  commandListView,
  commandText,
  defaultSuggestionIndex,
  draftAfterPick,
  findCommand,
  fuzzyScore,
  handleSlashKey,
  parseSlashInput,
  rankSuggestions,
  resolveSlashView,
  startsWithSlashTrigger,
} from './slashCommands'

const CATALOG: SlashCommandDto[] = [
  {
    name: 'model',
    usage: '/model [模型名称/模型ID]',
    summary: '切换当前会话使用的对话模型',
    description: '切换当前会话使用的对话模型。',
    argumentKind: 'model',
    argumentLabel: '模型名称/模型ID',
    aliases: ['m', 'models'],
  },
  {
    name: 'approval',
    usage: '/approval [manual/full]',
    summary: '切换工具审批模式',
    description: '切换工具审批模式。',
    argumentKind: 'approval',
    argumentLabel: 'manual/full',
    aliases: ['approve'],
  },
  {
    name: 'help',
    usage: '/help',
    summary: '列出全部命令',
    description: '列出全部命令。',
    argumentKind: 'none',
    aliases: ['?'],
  },
]

const chat: ChatSnapshotDto = {
  sessionId: 's1',
  messages: [],
  draft: '',
  attachments: [],
  generating: false,
  models: [
    { key: 'deepseek/deepseek-chat', provider: 'deepseek', name: 'DeepSeek Chat', modelId: 'deepseek-chat' },
    {
      key: 'deepseek/deepseek-reasoner',
      provider: 'deepseek',
      name: 'DeepSeek Reasoner',
      modelId: 'deepseek-reasoner',
    },
  ],
  selectedModelKey: 'deepseek/deepseek-chat',
  approvalMode: 'manual',
  workspace: null,
  workspaces: [],
  usage: null,
  ask: null,
}

describe('命令解析', () => {
  it('拆出命令名与参数，并保留是否已进入参数阶段', () => {
    expect(parseSlashInput('/model')).toEqual({ name: 'model', argument: '', hasArgumentSeparator: false })
    expect(parseSlashInput('/model deepseek chat')).toEqual({
      name: 'model',
      argument: 'deepseek chat',
      hasArgumentSeparator: true,
    })
    // 结尾空格表示已进入参数阶段，界面据此列出模型候选
    expect(parseSlashInput('/model ')).toEqual({
      name: 'model',
      argument: '',
      hasArgumentSeparator: true,
    })
    expect(parseSlashInput('  /help')).toEqual({ name: 'help', argument: '', hasArgumentSeparator: false })
  })

  it('不是命令（含多行）时返回 null', () => {
    expect(parseSlashInput('')).toBeNull()
    expect(parseSlashInput('/')).toBeNull()
    expect(parseSlashInput('你好')).toBeNull()
    expect(parseSlashInput('请用 /model')).toBeNull()
    expect(parseSlashInput('/model\n第二行')).toBeNull()
  })

  it('按命令名与别名查找命令', () => {
    expect(findCommand(CATALOG, 'model')?.name).toBe('model')
    expect(findCommand(CATALOG, '/MODEL')?.name).toBe('model')
    expect(findCommand(CATALOG, 'm')?.name).toBe('model')
    expect(findCommand(CATALOG, '?')?.name).toBe('help')
    expect(findCommand(CATALOG, 'nope')).toBeNull()
  })

  it('空白分界与宿主 char.IsWhiteSpace 一致（全角空格 / NBSP / NEL 都算分隔符）', () => {
    // 这几条与 SoufflePet.Core.Tests/SlashCommandTests.cs 的 InlineData 逐条对应：
    // 两侧必须同规则，否则 /model　reason 在界面上是「命令 + 参数」，到宿主却拼成一个不存在的命令名
    for (const separator of ['\u3000', '\u00a0', '\u0085', '\u2003', ' ', '\t']) {
      expect(parseSlashInput(`/model${separator}reason`)).toEqual({
        name: 'model',
        argument: 'reason',
        hasArgumentSeparator: true,
      })
    }

    // 参数首尾的同类空白也会被去掉
    expect(parseSlashInput('/model\u3000\u00a0reason\u3000')).toEqual({
      name: 'model',
      argument: 'reason',
      hasArgumentSeparator: true,
    })

    // 前置空白同样按同一字符集剥离
    expect(parseSlashInput('\u3000/model')).toEqual({
      name: 'model',
      argument: '',
      hasArgumentSeparator: false,
    })

    // BOM 不是空白（.NET 也不认），会留在命令名里 → 不属于已知命令
    expect(parseSlashInput('/model\ufeffreason')).toEqual({
      name: 'model\ufeffreason',
      argument: '',
      hasArgumentSeparator: false,
    })
  })

  it('以斜杠开头（允许前置空白）判定与解析一致', () => {
    expect(startsWithSlashTrigger('/model')).toBe(true)
    expect(startsWithSlashTrigger('\u3000 /model')).toBe(true)
    expect(startsWithSlashTrigger('  /')).toBe(true)
    expect(startsWithSlashTrigger('hello /model')).toBe(false)
    expect(startsWithSlashTrigger('')).toBe(false)
  })
})

describe('模糊匹配', () => {
  it('精确 > 前缀 > 包含 > 子序列', () => {
    expect(fuzzyScore('model', 'model')).toBeGreaterThan(fuzzyScore('model', 'mod'))
    expect(fuzzyScore('model', 'mod')).toBeGreaterThan(fuzzyScore('model', 'del'))
    expect(fuzzyScore('deepseek-chat', 'chat')).toBeGreaterThan(fuzzyScore('deepseek-chat', 'dsh'))
    expect(fuzzyScore('model', 'zzz')).toBe(-1)
  })

  it('排序时剔除不匹配项并保持同分顺序', () => {
    const ranked = rankSuggestions(['b-model', 'a-model', 'nope'], 'model', (item) => [item])
    expect(ranked).toEqual(['b-model', 'a-model'])
  })
})

describe('补全视图', () => {
  it('输入命令名时给出命令候选与预览', () => {
    const view = resolveSlashView(CATALOG, chat, '/mo')!
    expect(view.command).toBeNull()
    expect(view.suggestions.map((item) => item.value)).toEqual(['model'])
    expect(view.usage).toBe('/model [模型名称/模型ID]')

    const all = resolveSlashView(CATALOG, chat, '/')!
    expect(all.suggestions).toHaveLength(3)
  })

  it('输入「命令 + 空格」后列出全部模型并标记当前模型', () => {
    const view = resolveSlashView(CATALOG, chat, '/model ')!
    expect(view.command?.name).toBe('model')
    expect(view.suggestions.map((item) => item.value)).toEqual(['DeepSeek Chat', 'DeepSeek Reasoner'])
    expect(view.suggestions[0].selected).toBe(true)
    expect(view.suggestions[0].detail).toContain('deepseek-chat')
  })

  it('模型参数支持模糊过滤', () => {
    const view = resolveSlashView(CATALOG, chat, '/model reason')!
    expect(view.suggestions).toHaveLength(1)
    expect(view.suggestions[0].value).toBe('DeepSeek Reasoner')
  })

  it('审批模式参数跟随当前值', () => {
    const args = argumentSuggestions(CATALOG[1], chat)
    expect(args.map((item) => item.value)).toEqual(['manual', 'full'])
    expect(args[0].selected).toBe(true)
  })

  it('未知命令与普通文本', () => {
    expect(resolveSlashView(CATALOG, chat, '你好')).toBeNull()
    const unknown = resolveSlashView(CATALOG, chat, '/nope ')!
    expect(unknown.suggestions).toHaveLength(0)
    expect(unknown.command).toBeNull()
  })

  it('按钮唤起时给出完整命令列表', () => {
    const view = commandListView(CATALOG)
    expect(view.command).toBeNull()
    expect(view.suggestions.map((item) => item.value)).toEqual(['model', 'approval', 'help'])
  })
})

describe('初始高亮', () => {
  const chatWith = (selectedModelKey: string, approvalMode: 'manual' | 'full'): ChatSnapshotDto => ({
    ...chat,
    selectedModelKey,
    approvalMode,
  })

  it('命令阶段从第一项开始', () => {
    expect(defaultSuggestionIndex(resolveSlashView(CATALOG, chat, '/mo'))).toBe(0)
    expect(defaultSuggestionIndex(commandListView(CATALOG))).toBe(0)
    expect(defaultSuggestionIndex(null)).toBe(0)
  })

  it('模型参数落在当前模型上，而不是列表第一项', () => {
    const second = chatWith('deepseek/deepseek-reasoner', 'manual')
    // 当前模型在第二位：直接回车应当重新确认它，而不是切到 models[0]
    expect(defaultSuggestionIndex(resolveSlashView(CATALOG, second, '/model '))).toBe(1)

    const first = chatWith('deepseek/deepseek-chat', 'manual')
    expect(defaultSuggestionIndex(resolveSlashView(CATALOG, first, '/model '))).toBe(0)

    // 过滤后只剩一个候选：自然落在它上面
    expect(defaultSuggestionIndex(resolveSlashView(CATALOG, second, '/model reason'))).toBe(0)
  })

  it('审批模式参数落在当前模式上', () => {
    expect(defaultSuggestionIndex(resolveSlashView(CATALOG, chatWith('deepseek/deepseek-chat', 'full'), '/approval '))).toBe(1)
    expect(defaultSuggestionIndex(resolveSlashView(CATALOG, chatWith('deepseek/deepseek-chat', 'manual'), '/approval '))).toBe(0)
  })
})

describe('选中候选', () => {
  it('命令补一个空格继续输入参数，参数则拼成完整命令', () => {
    expect(draftAfterPick(CATALOG[0], { kind: 'command', value: 'model', label: '/model' })).toBe('/model ')
    expect(
      draftAfterPick(CATALOG[0], { kind: 'argument', value: 'DeepSeek Reasoner', label: 'DeepSeek Reasoner' }),
    ).toBe('/model DeepSeek Reasoner')
    expect(commandText(CATALOG[2], { kind: 'command', value: 'help', label: '/help' })).toBe('/help')
    expect(commandText(CATALOG[0], { kind: 'argument', value: 'DeepSeek Chat', label: 'DeepSeek Chat' })).toBe(
      '/model DeepSeek Chat',
    )
  })
})

describe('键盘操作', () => {
  const view = resolveSlashView(CATALOG, chat, '/model ')!

  it('方向键循环移动高亮', () => {
    expect(handleSlashKey({ key: 'ArrowDown' }, view, 1)).toEqual({ type: 'move', index: 0 })
    expect(handleSlashKey({ key: 'ArrowUp' }, view, 0)).toEqual({ type: 'move', index: 1 })
  })

  it('回车/Tab 确认当前项，Esc 关闭', () => {
    const picked = handleSlashKey({ key: 'Enter' }, view, 1)
    expect(picked).toMatchObject({ type: 'pick', suggestion: { value: 'DeepSeek Reasoner' } })
    expect(handleSlashKey({ key: 'Tab' }, view, 0)).toMatchObject({ type: 'pick' })
    expect(handleSlashKey({ key: 'Escape' }, view, 0)).toEqual({ type: 'close' })
  })

  it('带修饰键的组合键一律交还输入框（Shift+Enter 换行不被吞）', () => {
    expect(handleSlashKey({ key: 'Enter', shiftKey: true }, view, 0)).toBeNull()
    expect(handleSlashKey({ key: 'Tab', shiftKey: true }, view, 0)).toBeNull()
    expect(handleSlashKey({ key: 'Enter', ctrlKey: true }, view, 0)).toBeNull()
    expect(handleSlashKey({ key: 'Tab', altKey: true }, view, 0)).toBeNull()
    expect(handleSlashKey({ key: 'Enter', metaKey: true }, view, 0)).toBeNull()
    // 方向键带修饰键同样不接管，避免 Ctrl+↑ 等被当作换高亮
    expect(handleSlashKey({ key: 'ArrowDown', shiftKey: true }, view, 0)).toBeNull()
    // Esc 仍要能关闭面板（即使带修饰键）
    expect(handleSlashKey({ key: 'Escape', shiftKey: true }, view, 0)).toEqual({ type: 'close' })
  })

  it('没有候选或没有面板时交给调用方处理', () => {
    expect(handleSlashKey({ key: 'Enter' }, null, 0)).toBeNull()
    expect(handleSlashKey({ key: 'Enter' }, { ...view, suggestions: [] }, 0)).toBeNull()
  })
})

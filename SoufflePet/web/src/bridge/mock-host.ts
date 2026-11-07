// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/**
 * 前端独立开发用的 mock 宿主：实现 docs/react-ui-contract.md 的 v1 方法与事件。
 * 只在页面里运行，不写入任何宿主持久化数据；生产包体积很小，只有无宿主时才启用。
 */

import type {
  AppSnapshotDto,
  ChatModelDto,
  ChatOp,
  ChatSnapshotDto,
  Live2DModelDto,
  McpServerDto,
  McpToolDto,
  PluginDto,
  ProviderDto,
  ScheduledTaskLogDto,
  SessionSummaryDto,
  SkillDto,
  TaskDto,
  TaskInputDto,
  ThemeDto,
  ToastDto,
  UsageDto,
  WindowStateDto,
  WorkspaceDto,
} from './contracts'
import type { IncomingEnvelope, RequestEnvelope } from './protocol'
import { DEFAULT_ACCENT } from '@/theme/presets'
import { oklchToHex } from '@/theme/oklch'

type Emit = (envelope: IncomingEnvelope) => void

function iso(minutesAgo = 0): string {
  return new Date(Date.now() - minutesAgo * 60_000).toISOString()
}

function timeLabel(minutesAgo = 0): string {
  return iso(minutesAgo)
}

const WORKSPACE: WorkspaceDto = { path: 'D:/20114/CSharp/SoufflePet', name: 'SoufflePet' }
const NOTES_WORKSPACE: WorkspaceDto = { path: 'D:/notes', name: 'notes' }

const MODELS: ChatModelDto[] = [
  {
    key: 'deepseek/deepseek-chat',
    provider: 'deepseek',
    name: 'DeepSeek Chat',
    modelId: 'deepseek-chat',
  },
  {
    key: 'deepseek/deepseek-reasoner',
    provider: 'deepseek',
    name: 'DeepSeek Reasoner',
    modelId: 'deepseek-reasoner',
  },
  {
    key: 'openai/gpt-4.1',
    provider: 'openai',
    name: 'GPT-4.1',
    modelId: 'gpt-4.1',
  },
]

const USAGE_SESSION: UsageDto = {
  inputTokens: 12_480,
  outputTokens: 620,
  cachedInputTokens: 12_300,
  model: 'deepseek/deepseek-chat',
  time: iso(8),
}

const SESSION_STATS = {
  rounds: 4,
  steps: 6,
  modelSeconds: 52.8,
  toolSeconds: 21.7,
  averageFirstTokenSeconds: 4.4,
  tokensPerSecond: 129,
}

const RICH_TEXT = [
  '### 整理结果',
  '',
  '| 日期 | 输入 | 缓存命中 | 输出 |',
  '| --- | ---: | ---: | ---: |',
  '| 11-03 | 12,480 | 12,300 | 640 |',
  '| 11-04 | 15,020 | 14,880 | 720 |',
  '',
  '缓存命中率 = 缓存读取 / 输入 token：',
  '',
  '$$\\text{hit} = \\frac{12{,}300}{12{,}480} \\approx 98.6\\%$$',
  '',
  '> 命中率只统计输入侧；输出 token 不计入分母。',
  '',
  '```ts',
  'const hit = cachedInput / inputTokens',
  'const percent = `${(hit * 100).toFixed(1)}%`',
  '```',
  '',
  '- 输入包含缓存读取与未缓存两部分',
  '- 表格列宽可以横向滚动',
  '- 代码块会按需加载高亮',
  '',
  '需要的话我也可以把它导出成 CSV。',
].join('\n')

const STREAM_REPLY = [
  '收到，我先给你一个可以直接用的版本：',
  '',
  '1. 把表格按日期排序；',
  '2. 用 `cached / input` 计算命中率；',
  '3. 输出前保留一位小数。',
  '',
  '```ts',
  'const rate = cached / Math.max(input, 1)',
  '```',
  '',
  '如果你希望我把结果也写进 `notes.md`，告诉我路径即可。',
].join('\n')

interface MockSessionData {
  summary: SessionSummaryDto
  chat: ChatSnapshotDto
}

export interface MockHost {
  send: (envelope: RequestEnvelope, emit: Emit) => void
  dispose: () => void
}

export interface MockHostOptions {
  scenario?: string | null
}

export function createMockHost(options: MockHostOptions = {}): MockHost {
  const scenario = options.scenario ?? ''
  let emit: Emit = () => undefined
  let rev = 1024
  let activeId: string | null = 's-welcome'
  let maximized = false
  let theme: ThemeDto = {
    mode: 'dark',
    accent: oklchToHex(DEFAULT_ACCENT),
    accentL: DEFAULT_ACCENT.l,
    accentC: DEFAULT_ACCENT.c,
    accentH: DEFAULT_ACCENT.h,
    followSystem: false,
  }

  const providers: ProviderDto[] = [
    {
      name: 'deepseek',
      baseUrl: 'https://api.deepseek.com',
      apiKey: 'sk-mock',
      models: [
        { modelId: 'deepseek-chat', name: 'DeepSeek Chat', isActive: true },
        { modelId: 'deepseek-reasoner', name: 'DeepSeek Reasoner', isActive: true },
      ],
    },
    {
      name: 'openai',
      baseUrl: 'https://api.openai.com/v1',
      apiKey: 'sk-mock',
      models: [{ modelId: 'gpt-4.1', name: 'GPT-4.1', isActive: true }],
    },
  ]

  let mcpServers: McpServerDto[] = [
    {
      name: 'filesystem',
      enabled: true,
      transport: 'stdio',
      command: 'npx',
      arguments: ['-y', '@modelcontextprotocol/server-filesystem', 'D:/20114'],
      workingDirectory: '',
      environment: {},
      url: '',
      headers: {},
      timeoutSeconds: 30,
    },
  ]

  const skills: SkillDto[] = [
    {
      name: 'code-review',
      description: '按清单审查改动，优先看边界与失败路径',
      source: '家目录',
      filePath: 'C:/Users/20114/.souffle/skills/code-review/SKILL.md',
      enabled: true,
    },
    {
      name: 'table-export',
      description: '把对话里的表格导出为 Markdown / CSV',
      source: '工作区',
      filePath: 'D:/20114/CSharp/SoufflePet/.agents/skills/table-export/SKILL.md',
      enabled: true,
    },
  ]

  const plugins: PluginDto[] = [
    {
      id: 'computer-use',
      name: '电脑操作工具',
      version: '2.0.0',
      author: 'Shasnow',
      description: '向模型提供窗口列表、截屏与键鼠输入工具',
      builtIn: true,
      directory: 'plugins/computer-use',
      state: 'enabled',
      toolNames: ['list_windows', 'take_screenshot', 'mouse_click', 'keyboard_type'],
      failureCount: 0,
      hasConfig: true,
    },
    {
      id: 'browser-use',
      name: '浏览器操作工具',
      version: '1.0.0',
      author: 'Shasnow',
      description: '向模型提供自带浏览器窗口的打开、读取与交互工具',
      builtIn: true,
      directory: 'plugins/browser-use',
      state: 'enabled',
      toolNames: ['browser_open', 'browser_snapshot', 'browser_click', 'browser_screenshot'],
      failureCount: 0,
      hasConfig: false,
    },
    {
      id: 'dsml-cleaner',
      name: 'DSML Cleaner',
      version: '1.0.4',
      author: 'Shasnow',
      description: '清理模型回复里的 DSML 标记',
      builtIn: true,
      directory: 'plugins/dsml-cleaner',
      state: 'disabled',
      toolNames: [],
      failureCount: 0,
      hasConfig: false,
    },
  ]

  let tasks: TaskDto[] = [
    {
      id: 'task-daily',
      name: '每日整理',
      content: '把昨天的表格整理成 Markdown，并总结缓存命中率。',
      enabled: true,
      kind: 'daily',
      timeOfDay: '09:00',
      weekdays: [],
      intervalMinutes: 60,
      completed: false,
      nextRunAt: iso(-60 * 22),
      lastRunAt: iso(60 * 2),
    },
    {
      id: 'task-weekly',
      name: '周末回顾',
      content: '回顾本周的对话主题，列出三件值得继续推进的事。',
      enabled: true,
      kind: 'weekly',
      timeOfDay: '20:30',
      weekdays: [5, 6],
      intervalMinutes: 60,
      completed: false,
      nextRunAt: iso(-60 * 48),
    },
    {
      id: 'task-interval',
      name: '喝水提醒',
      content: '提醒我起来喝口水，顺便活动一下肩颈。',
      enabled: false,
      kind: 'interval',
      weekdays: [],
      intervalMinutes: 45,
      completed: false,
    },
  ]

  let taskLogs: ScheduledTaskLogDto[] = [
    { text: `${new Date().toLocaleString()} 任务「每日整理」已触发，交给桌宠执行（下次 11-06 09:00）`, isError: false },
    { text: `${new Date().toLocaleString()} 任务「周末回顾」已停用`, isError: false },
  ]

  const live2dModels: Live2DModelDto[] = [
    {
      name: '舒芙蕾',
      directory: 'models/shufulei',
      active: true,
      gameExpression: 'game',
      musicExpression: 'music',
      valid: true,
    },
    {
      name: 'Haru',
      directory: 'models/haru',
      active: false,
      valid: true,
    },
  ]

  const sessions: MockSessionData[] = []
  // 草稿会话：真实宿主在发出第一条消息前不落盘、不进侧栏列表，mock 保持一致
  let draft: MockSessionData | null = null
  const streamTimers = new Map<string, number>()
  const titleOverride = new Map<string, string>()

  function summary(
    id: string,
    title: string,
    workspace: WorkspaceDto,
    messageCount: number,
    updatedAt: string,
    lastUserText?: string,
  ): SessionSummaryDto {
    return {
      id,
      title,
      workspacePath: workspace.path,
      workspaceName: workspace.name,
      createdAt: iso(60 * 24 * 3),
      updatedAt,
      messageCount,
      lastUserText,
      modelKey: MODELS[0].key,
      generating: false,
    }
  }

  function emptyChat(id: string, workspace: WorkspaceDto | null = WORKSPACE): ChatSnapshotDto {
    return {
      sessionId: id,
      messages: [],
      draft: '',
      attachments: [],
      generating: false,
      models: MODELS,
      selectedModelKey: MODELS[0].key,
      approvalMode: 'manual',
      workspace,
      workspaces: [WORKSPACE, NOTES_WORKSPACE],
      usage: null,
      ask: null,
    }
  }

  function pushRichSession(): void {
    const id = 's-rich'
    const chat = emptyChat(id)
    chat.messages = [
      {
        id: 'm-notice',
        kind: 'notice',
        text: '⏰ 定时任务「每日整理」：把昨天的表格整理成 Markdown，并总结缓存命中率。',
        time: timeLabel(24),
      },
      {
        id: 'm-user-1',
        kind: 'user',
        text: '帮我把这个表格整理成 Markdown，并解释一下缓存命中率怎么算。',
        time: timeLabel(23),
      },
      {
        id: 'm-reasoning',
        kind: 'reasoning',
        text: '先确认表格的列，再按 Markdown 语法重排；缓存命中率要区分输入 token 与缓存读取 token。',
        time: timeLabel(23),
        expanded: false,
        reasoningDone: true,
      },
      {
        id: 'm-tool',
        kind: 'tool',
        text: '调用工具 read_file',
        time: timeLabel(22),
        expanded: false,
        tool: {
          name: 'read_file',
          args: '{"path":"D:/notes/table.csv"}',
          result: '日期,输入,缓存命中,输出\n11-03,12480,12300,640\n11-04,15020,14880,720',
          label: '读取 · table.csv',
        },
      },
      {
        id: 'm-tool-edit',
        kind: 'tool',
        text: '调用工具 edit_file',
        time: timeLabel(22),
        expanded: false,
        tool: {
          name: 'edit_file',
          args: '{"path":"D:/notes/table.md"}',
          result: '已编辑 D:/notes/table.md（+2 −1）。',
          label: '编辑 · table.md',
          diffs: [
            {
              path: 'D:/notes/table.md',
              added: 2,
              removed: 1,
              hunks: [
                {
                  oldStart: 1,
                  oldLines: 3,
                  newStart: 1,
                  newLines: 4,
                  lines: [
                    { kind: 'ctx', text: '# 整理结果' },
                    { kind: 'del', text: '缓存命中率 = 缓存读取 / 输入 token' },
                    { kind: 'add', text: '缓存命中率 = 缓存读取 / 输入 token（含缓存写入）' },
                    { kind: 'add', text: '示例：12300 / 12480 = 98.6%' },
                  ],
                },
              ],
            },
          ],
        },
      },
      {
        id: 'm-assistant-1',
        kind: 'assistant',
        text: RICH_TEXT,
        time: timeLabel(22),
        usage: USAGE_SESSION,
      },
      {
        id: 'm-user-2',
        kind: 'user',
        text: '顺便提醒我今晚 9 点继续整理。',
        time: timeLabel(15),
      },
      {
        id: 'm-assistant-2',
        kind: 'assistant',
        text: '已经帮你加了一条「今晚整理表格」的提醒，计划在 21:00 触发；到点后我会在这个会话里继续。',
        time: timeLabel(14),
        usage: { inputTokens: 8_240, outputTokens: 310, cachedInputTokens: 8_100, model: 'deepseek/deepseek-chat', time: timeLabel(14) },
      },
      {
        id: 'm-attachment',
        kind: 'attachment',
        text: '',
        time: timeLabel(14),
        attachment: {
          id: 'att-1',
          name: 'table.csv',
          path: 'D:/notes/table.csv',
          isImage: false,
          size: 2384,
        },
      },
      {
        id: 'm-approval',
        kind: 'approval',
        text: '模型请求执行命令，等待你的确认：\nrun_shell_command {"command":"node scripts/export.mjs"}',
        time: timeLabel(13),
        approval: {
          toolName: 'run_shell_command',
          detail: '{"command":"node scripts/export.mjs"}',
          pending: false,
          outcome: 'approved',
        },
      },
    ]
    chat.usage = { inputTokens: 20_720, outputTokens: 930, cachedInputTokens: 20_400, model: 'deepseek/deepseek-chat' }
    sessions.push({
      summary: summary(id, '整理表格与缓存说明', WORKSPACE, chat.messages.length, iso(14), '顺便提醒我今晚 9 点继续整理。'),
      chat,
    })
  }

  function pushSecondSession(): void {
    const id = 's-live2d'
    const chat = emptyChat(id)
    chat.workspace = NOTES_WORKSPACE
    chat.messages = [
      {
        id: 'm-live-user',
        kind: 'user',
        text: '桌宠切到 Haru 之后动作不动了，帮我看看。',
        time: timeLabel(60 * 30),
      },
      {
        id: 'm-live-assistant',
        kind: 'assistant',
        text: '先确认模型目录里的 `.model3.json` 是否包含动作组；如果动作名与配置里的 `GameExpression` 对不上，切换后就不会触发联动。',
        time: timeLabel(60 * 29),
        usage: { inputTokens: 6_100, outputTokens: 260, cachedInputTokens: 5_900, model: 'deepseek/deepseek-chat' },
      },
      {
        id: 'm-live-user-2',
        kind: 'user',
        text: '我把配置发你看看。',
        time: timeLabel(60 * 28),
      },
      {
        id: 'm-live-assistant-2',
        kind: 'assistant',
        text: '看到了，`Motions` 里只有 `Idle`，没有 `TapBody`；补一个动作映射再重启模型即可。',
        time: timeLabel(60 * 28),
        usage: { inputTokens: 6_400, outputTokens: 220, cachedInputTokens: 6_100, model: 'deepseek/deepseek-chat' },
      },
    ]
    chat.usage = {
      inputTokens: 12_500,
      outputTokens: 480,
      cachedInputTokens: 12_000,
      model: 'deepseek/deepseek-chat',
      ...SESSION_STATS,
    }
    sessions.push({
      summary: summary(id, 'Live2D 模型动效排查', NOTES_WORKSPACE, chat.messages.length, iso(60 * 28), '我把配置发你看看。'),
      chat,
    })
  }

  function pushLongSession(): void {
    const id = 's-long'
    const chat = emptyChat(id)
    chat.messages = Array.from({ length: 500 }, (_, index) => {
      const isUser = index % 2 === 0
      return {
        id: `m-long-${index}`,
        kind: isUser ? ('user' as const) : ('assistant' as const),
        text: isUser
          ? `第 ${index / 2 + 1} 个问题：帮我把这一轮的要点列成三条，并给一个下一步建议。`
          : `第 ${index / 2 + 0.5} 轮回答：\n\n1. 先确认输入；\n2. 再确认缓存命中；\n3. 最后检查输出格式。\n\n> 这是第 ${index + 1} 条消息，用于验证长列表滚动。`,
        time: timeLabel(60 * 24 - index),
        usage: isUser
          ? undefined
          : { inputTokens: 4_000 + index, outputTokens: 120, cachedInputTokens: 3_800 + index, model: 'deepseek/deepseek-chat' },
      }
    })
    sessions.push({
      summary: summary(id, '500 条长会话压测', WORKSPACE, 500, iso(1), '第 250 个问题：帮我把这一轮的要点列成三条。'),
      chat,
    })
  }

  // 默认活动会话是空的「新会话」，让首屏显示欢迎页；历史会话可在侧栏切换。
  sessions.push({
    summary: summary('s-welcome', '新会话', WORKSPACE, 0, iso(1)),
    chat: emptyChat('s-welcome'),
  })
  pushRichSession()
  pushSecondSession()
  if (scenario === 'long') pushLongSession()

  if (scenario === 'empty') {
    sessions.splice(1)
    titleOverride.clear()
    activeId = 's-welcome'
  } else if (scenario === 'rich') {
    activeId = 's-rich'
  } else if (scenario === 'long') {
    activeId = 's-long'
  } else if (scenario === 'ask') {
    activeId = 's-rich'
    const chat = getSessionChat('s-rich')
    if (chat) {
      chat.ask = {
        requestId: 'ask-mock-1',
        questions: [
          {
            text: '今晚整理表格时，你希望先做哪一步？',
            options: [
              { text: '先补全缺失的日期', description: '把 11-01、11-02 的空行补上' },
              { text: '先检查缓存列', description: '确认命中率统计口径' },
            ],
          },
          {
            text: '结果保存到哪里？',
            options: [
              { text: 'D:/notes/table.md' },
              { text: 'D:/20114/CSharp/SoufflePet/docs/table.md' },
            ],
          },
        ],
      }
    }
  }

  function getSessionData(id: string): MockSessionData | undefined {
    return sessions.find((session) => session.summary.id === id)
      ?? (draft?.summary.id === id ? draft : undefined)
  }

  function getSessionChat(id: string): ChatSnapshotDto | undefined {
    return getSessionData(id)?.chat
  }

  function getSessionSummary(id: string): SessionSummaryDto | undefined {
    return getSessionData(id)?.summary
  }

  /// 草稿在第一条消息发出时转正：进列表、参与搜索与持久化
  function promoteDraft(sessionId: string): void {
    if (!draft || draft.summary.id !== sessionId) return
    sessions.push(draft)
    draft = null
  }

  // 与宿主一致：显式 sessionId 优先，缺省回退活动会话
  function targetSessionId(payload: Record<string, unknown>): string | undefined {
    const explicit = payload.sessionId
    return typeof explicit === 'string' && explicit.length > 0 ? explicit : activeId ?? undefined
  }

  function emitEvent(name: string, payload: Record<string, unknown>): void {
    rev += 1
    emit({ v: 1, type: 'event', name, payload: { ...payload, rev } })
  }

  function emitOps(sessionId: string, ops: ChatOp[]): void {
    rev += 1
    emit({
      v: 1,
      type: 'event',
      name: 'chat.ops',
      payload: { sessionId, rev, ops },
    })
  }

  function emitSessions(): void {
    emitEvent('session.updated', {
      sessions: sessions.map((session) => session.summary),
      activeId,
    })
  }

  // 与宿主一致：工作区列表是全宿主共享的，包含当前没有任何会话的工作区
  function collectWorkspaces(): WorkspaceDto[] {
    const map = new Map<string, WorkspaceDto>()
    for (const workspace of [WORKSPACE, NOTES_WORKSPACE]) map.set(workspace.path, workspace)
    for (const session of sessions) {
      for (const workspace of session.chat.workspaces) map.set(workspace.path, workspace)
    }
    if (draft) {
      for (const workspace of draft.chat.workspaces) map.set(workspace.path, workspace)
    }
    return [...map.values()]
  }

  function emitSnapshot(): void {
    rev += 1
    const chat = activeId ? getSessionChat(activeId) ?? null : null
    const snapshot: AppSnapshotDto = {
      protocolVersion: 1,
      window: 'app',
      route: activeId ? `#/chat/${activeId}` : '#/chat',
      pet: { name: '舒芙蕾', initial: '舒' },
      theme,
      usage: {
        inputTokens: 128_400,
        outputTokens: 23_800,
        cachedInputTokens: 126_000,
        conversationCount: 42,
        pokeCount: 7,
        headPatCount: 3,
        runSeconds: 93_784,
      },
      sessions: sessions.map((session) => session.summary),
      activeSessionId: activeId,
      chat,
      workspaces: collectWorkspaces(),
    }
    emit({ v: 1, type: 'snapshot', rev, data: snapshot })
  }

  function response(id: number, result: unknown): void {
    emit({ v: 1, id, ok: true, result })
  }

  function fail(id: number, code: string, message: string): void {
    emit({ v: 1, id, ok: false, error: { code, message } })
  }

  function newId(prefix: string): string {
    return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`
  }

  function titleFromText(text: string): string {
    const single = text.replace(/\s+/g, ' ').trim()
    return single.length > 30 ? `${single.slice(0, 30)}…` : single || '新会话'
  }

  function stream(sessionId: string, text: string, attachmentNames: string[]): void {
    const chat = getSessionChat(sessionId)
    if (!chat || chat.generating) return

    const userText = text.trim() || (attachmentNames.length ? attachmentNames.map((name) => `[附件] ${name}`).join('\n') : '')
    if (!userText) return

    const userId = newId('m-user')
    const assistantId = newId('m-assistant')
    const now = timeLabel()
    const userMessage = {
      id: userId,
      kind: 'user' as const,
      text: userText,
      time: now,
    }
    const assistantMessage = {
      id: assistantId,
      kind: 'assistant' as const,
      text: '',
      time: now,
    }
    chat.messages = [...chat.messages, userMessage, assistantMessage]
    chat.generating = true
    chat.draft = ''
    chat.attachments = []
    emitOps(sessionId, [
      { op: 'msg:add', msg: userMessage },
      { op: 'msg:add', msg: assistantMessage },
      { op: 'turn', generating: true },
      { op: 'attachments', attachments: [] },
    ])
    const streamingSummary = getSessionSummary(sessionId)
    // 与宿主一致：生成状态走 chat.ops 的 turn 增量，不额外推送整张会话列表
    if (streamingSummary) streamingSummary.generating = true
    if (!titleOverride.has(sessionId)) {
      titleOverride.set(sessionId, titleFromText(userText))
      const summary = getSessionSummary(sessionId)
      if (summary) {
        summary.title = titleOverride.get(sessionId)!
        summary.lastUserText = userText
        summary.updatedAt = iso()
        summary.messageCount = chat.messages.length
        emitSessions()
      }
    }

    let cursor = 0
    const timer = window.setInterval(() => {
      const current = getSessionChat(sessionId)
      if (!current?.generating) {
        window.clearInterval(timer)
        return
      }
      const chunk = STREAM_REPLY.slice(cursor, cursor + 12)
      cursor += 12
      if (chunk) {
        const message = current.messages.find((item) => item.id === assistantId)
        if (message) message.text += chunk
        emitOps(sessionId, [{ op: 'msg:text', id: assistantId, mode: 'append', text: chunk }])
      }
      if (cursor >= STREAM_REPLY.length) {
        window.clearInterval(timer)
        streamTimers.delete(sessionId)
        const usage: UsageDto = {
          inputTokens: 9_820,
          outputTokens: 420,
          cachedInputTokens: 9_500,
          model: chat.selectedModelKey ?? MODELS[0].key,
          time: timeLabel(),
        }
        current.generating = false
        const message = current.messages.find((item) => item.id === assistantId)
        if (message) {
          message.usage = usage
          message.time = timeLabel()
        }
        current.usage = usage
        const summary = getSessionSummary(sessionId)
        if (summary) {
          summary.updatedAt = iso()
          summary.messageCount = current.messages.length
          summary.generating = false
        }
        emitOps(sessionId, [
          { op: 'msg:patch', id: assistantId, patch: { usage, time: timeLabel() } },
          { op: 'turn', generating: false },
          { op: 'usage', usage },
        ])
        emitSessions()
      }
    }, 40)
    streamTimers.set(sessionId, timer)
  }

  const host: MockHost = {
    send(envelope, emitter) {
      emit = emitter
      const { id, method, params } = envelope
      const payload = (params ?? {}) as Record<string, unknown>

      window.setTimeout(() => {
        switch (method) {
          case 'hello': {
            emitSnapshot()
            return
          }
          case 'app.resync': {
            emitSnapshot()
            return
          }
          case 'session.list': {
            response(id, sessions.map((session) => session.summary))
            return
          }
          case 'session.search': {
            const query = String(payload.query ?? '').toLowerCase()
            response(
              id,
              sessions
                .map((session) => session.summary)
                .filter((session) =>
                  [session.title, session.lastUserText, session.workspaceName]
                    .filter(Boolean)
                    .some((value) => String(value).toLowerCase().includes(query)),
                ),
            )
            return
          }
          case 'session.new': {
            // 和真实宿主一致：新建只产生一个内存草稿，发出第一条消息后才进列表
            if (draft) {
              activeId = draft.summary.id
              emitSessions()
              response(id, draft.summary)
              return
            }
            const sessionId = newId('s')
            // 从侧栏某个工作区新建时沿用该工作区，未指定则落在默认分组
            const requestedPath = String(payload.workspacePath ?? '')
            const workspace =
              [WORKSPACE, NOTES_WORKSPACE].find((item) => item.path === requestedPath) ?? null
            const chat = emptyChat(sessionId, workspace)
            draft = { summary: summaryOf(sessionId, '新会话', workspace), chat }
            activeId = sessionId
            emitSessions()
            response(id, draft.summary)
            return
          }
          case 'session.open': {
            const sessionId = String(payload.sessionId ?? '')
            const chat = getSessionChat(sessionId)
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            activeId = sessionId
            emitSessions()
            response(id, chat)
            return
          }
          case 'session.close': {
            const sessionId = String(payload.sessionId ?? '')
            if (!getSessionSummary(sessionId)) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            if (sessionId === activeId) {
              fail(id, 'conflict', '不能关闭当前活动会话')
              return
            }
            // mock 宿主不做真正的卸载：会话数据本来就都在内存里
            response(id, null)
            return
          }
          case 'session.rename': {
            const sessionId = String(payload.sessionId ?? '')
            const title = String(payload.title ?? '').trim()
            const summary = getSessionSummary(sessionId)
            if (!summary) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            if (!title) {
              fail(id, 'invalid_argument', '标题不能为空')
              return
            }
            summary.title = title
            summary.updatedAt = iso()
            emitSessions()
            response(id, summary)
            return
          }
          case 'session.delete': {
            const sessionId = String(payload.sessionId ?? '')
            const index = sessions.findIndex((session) => session.summary.id === sessionId)
            if (index < 0) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            sessions.splice(index, 1)
            if (sessions.length === 0) {
              // 删掉最后一个会话后补草稿，而不是立刻造出一个已保存的空会话
              const draftId = newId('s')
              draft = { summary: summaryOf(draftId, '新会话', WORKSPACE), chat: emptyChat(draftId) }
              activeId = draftId
            } else if (activeId === sessionId) {
              activeId = sessions[0].summary.id
            }
            emitSessions()
            response(id, {
              sessions: sessions.map((session) => session.summary),
              activeId,
            })
            return
          }
          case 'chat.send': {
            const sessionId = targetSessionId(payload)
            if (!sessionId) {
              fail(id, 'conflict', '没有活动会话')
              return
            }
            promoteDraft(sessionId)
            stream(
              sessionId,
              String(payload.text ?? ''),
              (payload.attachmentNames as string[] | undefined) ?? [],
            )
            emitSessions()
            response(id, null)
            return
          }
          case 'chat.stop': {
            const sessionId = targetSessionId(payload)
            if (!sessionId) {
              response(id, null)
              return
            }
            const timer = streamTimers.get(sessionId)
            if (timer) window.clearInterval(timer)
            streamTimers.delete(sessionId)
            const chat = getSessionChat(sessionId)
            if (chat) {
              chat.generating = false
              const summary = getSessionSummary(sessionId)
              if (summary) summary.generating = false
              const last = [...chat.messages].reverse().find((message) => message.kind === 'assistant')
              if (last && !last.text.includes('（已停止）')) {
                last.text += '（已停止）'
                emitOps(sessionId, [
                  { op: 'msg:text', id: last.id, mode: 'set', text: last.text },
                  { op: 'turn', generating: false },
                ])
              }
              // 侧栏加载图标由 turn 增量清除，先刷新摘要以同步停止后的文本长度
              emitSessions()
            }
            response(id, null)
            return
          }
          case 'chat.draft': {
            const chat = activeId ? getSessionChat(activeId) : undefined
            if (chat) chat.draft = String(payload.text ?? '')
            response(id, null)
            return
          }
          case 'chat.attach.pick':
          case 'chat.attach.paste': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            const attachment = {
              id: newId('att'),
              name: 'notes.md',
              path: 'D:/notes/notes.md',
              isImage: false,
              size: 1820,
            }
            chat.attachments = [...chat.attachments, attachment]
            emitOps(sessionId!, [{ op: 'attachments', attachments: chat.attachments }])
            response(id, { attachments: chat.attachments })
            return
          }
          case 'chat.attach.remove': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            chat.attachments = chat.attachments.filter((attachment) => attachment.id !== payload.id)
            emitOps(sessionId!, [{ op: 'attachments', attachments: chat.attachments }])
            response(id, { attachments: chat.attachments })
            return
          }
          case 'chat.model.select': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            const key = String(payload.key ?? '')
            if (!MODELS.some((model) => model.key === key)) {
              fail(id, 'invalid_argument', '模型不存在')
              return
            }
            chat.selectedModelKey = key
            emitOps(sessionId!, [{ op: 'models', models: MODELS, selectedModelKey: key }])
            response(id, null)
            return
          }
          case 'chat.approval.mode': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            const mode = payload.mode === 'full' ? 'full' : 'manual'
            chat.approvalMode = mode
            emitOps(sessionId!, [{ op: 'approval', mode }])
            response(id, null)
            return
          }
          case 'chat.approval.decide': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            const message = chat?.messages.find((item) => item.id === payload.messageId)
            if (!chat || !message?.approval) {
              fail(id, 'not_found', '审批消息不存在')
              return
            }
            message.approval.pending = false
            message.approval.outcome = payload.approve === true ? 'approved' : 'rejected'
            emitOps(sessionId!, [
              { op: 'msg:patch', id: message.id, patch: { approval: message.approval } },
            ])
            response(id, null)
            return
          }
          case 'chat.workspace.switch': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            const path = String(payload.path ?? '')
            const workspace = chat.workspaces.find((item) => item.path === path) ?? null
            chat.workspace = workspace
            // 与宿主一致：切换后会话归入新的工作区分组
            for (const session of sessions) {
              if (session.summary.id !== sessionId) continue
              session.summary.workspacePath = workspace?.path
              session.summary.workspaceName = workspace?.name
            }
            emitSessions()
            emitOps(sessionId!, [{ op: 'workspace', workspace, workspaces: chat.workspaces }])
            response(id, { workspace, workspaces: chat.workspaces })
            return
          }
          case 'chat.workspace.pick': {
            response(id, NOTES_WORKSPACE)
            return
          }
          case 'chat.workspace.rename': {
            const path = String(payload.path ?? '')
            const name = String(payload.name ?? '').trim()
            const fallback = path.split(/[\\/]/).filter(Boolean).pop() ?? path
            const label = name || fallback
            // 只改展示名：路径、会话分组与磁盘目录都不动
            const rename = (list: WorkspaceDto[]) =>
              list.map((item) => (item.path === path ? { path: item.path, name: label } : item))
            for (const session of sessions) {
              session.chat.workspaces = rename(session.chat.workspaces)
              if (session.chat.workspace?.path === path) session.chat.workspace = { path, name: label }
              if (session.summary.workspacePath === path) session.summary.workspaceName = label
            }
            if (draft) {
              draft.chat.workspaces = rename(draft.chat.workspaces)
              if (draft.chat.workspace?.path === path) draft.chat.workspace = { path, name: label }
              if (draft.summary.workspacePath === path) draft.summary.workspaceName = label
            }
            emitSessions()
            const activeChat = activeId ? getSessionChat(activeId) : undefined
            if (activeChat) {
              emitOps(activeId!, [
                {
                  op: 'workspace',
                  workspace: activeChat.workspace,
                  workspaces: activeChat.workspaces,
                },
              ])
            }
            response(id, { workspaces: activeChat?.workspaces ?? [] })
            return
          }
          case 'chat.workspace.delete': {
            const path = String(payload.path ?? '')
            if (payload.deleteSessions === true) {
              // 侧栏删除工作区：组内会话一起移出列表
              const removed = sessions.filter((session) => session.summary.workspacePath === path)
              for (const item of removed) {
                const index = sessions.indexOf(item)
                if (index >= 0) sessions.splice(index, 1)
              }
              if (sessions.length === 0) {
                const draftId = newId('s')
                draft = { summary: summaryOf(draftId, '新会话', WORKSPACE), chat: emptyChat(draftId) }
                activeId = draftId
              } else if (removed.some((item) => item.summary.id === activeId)) {
                activeId = sessions[0].summary.id
              }
              for (const session of sessions) {
                session.chat.workspaces = session.chat.workspaces.filter((item) => item.path !== path)
                if (session.chat.workspace?.path === path) session.chat.workspace = null
              }
              emitSessions()
              response(id, { sessions: sessions.map((session) => session.summary), activeId })
              return
            }

            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            chat.workspaces = chat.workspaces.filter((item) => item.path !== path)
            if (chat.workspace?.path === path) chat.workspace = null
            emitOps(sessionId!, [
              { op: 'workspace', workspace: chat.workspace, workspaces: chat.workspaces },
            ])
            response(id, { workspaces: chat.workspaces })
            return
          }
          case 'chat.ask': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            if (!chat) {
              fail(id, 'not_found', '会话不存在')
              return
            }
            if (payload.action === 'cancel' || payload.action === 'submit') {
              chat.ask = null
              emitOps(sessionId!, [{ op: 'ask', ask: null }])
            }
            response(id, null)
            return
          }
          case 'chat.file.open':
          case 'chat.link.open': {
            response(id, null)
            return
          }
          case 'chat.msg.expand': {
            const sessionId = targetSessionId(payload)
            const chat = sessionId ? getSessionChat(sessionId) : undefined
            const message = chat?.messages.find((item) => item.id === payload.messageId)
            if (!chat || !message) {
              fail(id, 'not_found', '消息不存在')
              return
            }
            message.expanded = payload.expanded === true
            emitOps(sessionId!, [
              { op: 'msg:patch', id: message.id, patch: { expanded: message.expanded } },
            ])
            response(id, null)
            return
          }
          case 'window.drag':
          case 'window.resize':
          case 'window.minimize':
          case 'window.close': {
            response(id, null)
            return
          }
          case 'window.maximize': {
            maximized = !maximized
            emitEvent('window.state', { visible: true, maximized })
            response(id, null)
            return
          }
          case 'window.state': {
            response(id, { visible: true, maximized } satisfies WindowStateDto)
            return
          }
          case 'settings.appearance.get': {
            response(id, theme)
            return
          }
          case 'settings.appearance.setAccent': {
            theme = { ...theme, accent: String(payload.accent ?? theme.accent), followSystem: false }
            emitEvent('host.theme', { ...theme })
            response(id, theme)
            return
          }
          case 'settings.appearance.setFollowSystem': {
            theme = { ...theme, followSystem: payload.enabled === true }
            emitEvent('host.theme', { ...theme })
            response(id, theme)
            return
          }
          case 'settings.appearance.reset': {
            theme = { ...theme, accent: oklchToHex(DEFAULT_ACCENT), followSystem: false }
            emitEvent('host.theme', { ...theme })
            response(id, theme)
            return
          }
          case 'settings.appearance.preview': {
            response(id, { ...theme, accent: String(payload.accent ?? theme.accent) })
            return
          }
          case 'settings.appearance.setSidebarCollapsed': {
            response(id, null)
            return
          }
          case 'settings.llm.list': {
            response(id, {
              providers,
              enabledModels: MODELS,
              defaultKey: MODELS[0].key,
            })
            return
          }
          case 'settings.llm.saveProvider': {
            const next = payload as unknown as ProviderDto
            const index = providers.findIndex((provider) => provider.name === next.name)
            if (index >= 0) providers[index] = next
            else providers.push(next)
            response(id, next)
            return
          }
          case 'settings.llm.deleteProvider': {
            const name = String(payload.name ?? '')
            const index = providers.findIndex((provider) => provider.name === name)
            if (index >= 0) providers.splice(index, 1)
            response(id, { providers })
            return
          }
          case 'settings.llm.fetchModels': {
            response(id, { modelIds: ['deepseek-chat', 'deepseek-reasoner', 'deepseek-coder'] })
            return
          }
          case 'settings.llm.setEnabled': {
            const provider = providers.find((item) => item.name === payload.provider)
            const model = provider?.models.find((item) => item.modelId === payload.modelId)
            if (model) model.isActive = payload.enabled === true
            response(id, provider ?? null)
            return
          }
          case 'settings.llm.setDefault': {
            response(id, null)
            return
          }
          case 'settings.mcp.list': {
            response(id, { servers: mcpServers, path: 'C:/Users/20114/.souffle/mcp.json' })
            return
          }
          case 'settings.mcp.save': {
            const next = payload as unknown as McpServerDto
            const index = mcpServers.findIndex((server) => server.name === next.name)
            if (index >= 0) mcpServers[index] = next
            else mcpServers = [...mcpServers, next]
            response(id, { servers: mcpServers })
            return
          }
          case 'settings.mcp.delete': {
            mcpServers = mcpServers.filter((server) => server.name !== payload.name)
            response(id, { servers: mcpServers })
            return
          }
          case 'settings.mcp.test':
          case 'settings.mcp.refreshTools': {
            const tools: McpToolDto[] = [
              { name: 'read_file', description: '读取本地文本文件' },
              { name: 'list_directory', description: '列出目录内容' },
              { name: 'search_files', description: '按关键词搜索文件' },
            ]
            response(id, { tools })
            return
          }
          case 'settings.skills.list': {
            response(id, {
              skills,
              directories: [
                'D:/20114/CSharp/SoufflePet/.agents/skills',
                'C:/Users/20114/.souffle/skills',
              ],
            })
            return
          }
          case 'settings.skills.setEnabled': {
            const skill = skills.find((item) => item.name === payload.name)
            if (skill) skill.enabled = payload.enabled === true
            response(id, skill ?? null)
            return
          }
          case 'settings.skills.openFolder': {
            response(id, null)
            return
          }
          case 'settings.skills.detail': {
            const skill = skills.find((item) => item.name === payload.name)
            response(id, skill ? { ...skill, content: `# ${skill.name}\n\n${skill.description}` } : null)
            return
          }
          case 'settings.plugins.list': {
            response(id, {
              plugins,
              installDir: 'C:/Users/20114/.souffle/plugins',
            })
            return
          }
          case 'settings.plugins.setEnabled': {
            const plugin = plugins.find((item) => item.id === payload.id)
            if (plugin) plugin.state = payload.enabled === true ? 'enabled' : 'disabled'
            response(id, plugin ?? null)
            return
          }
          case 'settings.plugins.getConfigSchema': {
            response(id, {
              fields: [
                {
                  key: 'typingDelayMs',
                  label: '输入延迟（毫秒）',
                  description: '逐字输入时的间隔',
                  kind: 'number',
                  value: 18,
                },
                {
                  key: 'allowClipboard',
                  label: '允许读写剪贴板',
                  kind: 'boolean',
                  value: true,
                },
                {
                  key: 'mode',
                  label: '输入模式',
                  kind: 'select',
                  options: ['逐字', '整段粘贴'],
                  value: '逐字',
                },
              ],
              config: {},
            })
            return
          }
          case 'settings.plugins.saveConfig': {
            response(id, plugins[0] ?? null)
            return
          }
          case 'settings.plugins.import': {
            response(id, { success: true, message: '已导入示例插件（mock）', pluginId: 'mock-import' })
            return
          }
          case 'settings.tasks.list': {
            response(id, { tasks, logs: taskLogs })
            return
          }
          case 'settings.tasks.save': {
            const input = payload as unknown as TaskInputDto
            const index = tasks.findIndex((task) => task.id === input.id)
            const next: TaskDto = {
              ...input,
              id: input.id ?? newId('task'),
              weekdays: input.weekdays ?? [],
              intervalMinutes: input.intervalMinutes ?? 60,
              completed: false,
              nextRunAt: iso(-60 * 12),
            }
            if (index >= 0) tasks[index] = next
            else tasks = [...tasks, next]
            response(id, next)
            return
          }
          case 'settings.tasks.delete': {
            tasks = tasks.filter((task) => task.id !== payload.id)
            response(id, { tasks })
            return
          }
          case 'settings.tasks.setEnabled': {
            const task = tasks.find((item) => item.id === payload.id)
            if (task) task.enabled = payload.enabled === true
            response(id, task ?? null)
            return
          }
          case 'settings.tasks.runNow': {
            response(id, null)
            return
          }
          case 'settings.tasks.nextRun': {
            response(id, { nextRunAt: iso(-60 * 12) })
            return
          }
          case 'settings.tasks.clearLogs': {
            taskLogs = []
            response(id, null)
            return
          }
          case 'settings.models.list': {
            response(id, {
              models: live2dModels,
              activeIndex: live2dModels.findIndex((model) => model.active),
            })
            return
          }
          case 'settings.models.importFolder':
          case 'settings.models.importArchive': {
            response(id, { models: live2dModels, message: '已导入示例模型（mock）' })
            return
          }
          case 'settings.models.remove': {
            const index = Number(payload.index ?? -1)
            if (index >= 0 && index < live2dModels.length) live2dModels.splice(index, 1)
            response(id, {
              models: live2dModels,
              activeIndex: Math.max(0, live2dModels.findIndex((model) => model.active)),
            })
            return
          }
          case 'settings.models.setActive': {
            const index = Number(payload.index ?? -1)
            live2dModels.forEach((model, modelIndex) => {
              model.active = modelIndex === index
            })
            response(id, null)
            return
          }
          case 'settings.models.validate': {
            response(id, { ok: true, message: '模型文件完整' })
            return
          }
          case 'nav.goto': {
            response(id, null)
            return
          }
          default: {
            fail(id, 'unknown_method', `mock 未实现方法：${method}`)
          }
        }
      }, 45)
    },
    dispose() {
      for (const timer of streamTimers.values()) window.clearInterval(timer)
      streamTimers.clear()
    },
  }

  function summaryOf(id: string, title: string, workspace: WorkspaceDto | null): SessionSummaryDto {
    return {
      id,
      title,
      workspacePath: workspace?.path,
      workspaceName: workspace?.name,
      createdAt: iso(),
      updatedAt: iso(),
      messageCount: 0,
      modelKey: MODELS[0].key,
    }
  }

  return host
}

export function mockScenarioFromUrl(): string | null {
  try {
    return new URLSearchParams(window.location.search).get('scenario')
  } catch {
    return null
  }
}

export function mockToast(text: string): ToastDto {
  return { level: 'info', text }
}
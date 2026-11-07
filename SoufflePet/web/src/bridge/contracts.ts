// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/**
 * React SPA 与宿主之间的 v1 契约。字段定义以 docs/react-ui-contract.md 为准。
 * 任何新增都必须是可选字段/新方法；语义变更必须升 protocolVersion。
 */

export type ApprovalMode = 'manual' | 'full'

export type ChatMessageKind =
  | 'user'
  | 'assistant'
  | 'reasoning'
  | 'tool'
  | 'notice'
  | 'attachment'
  | 'approval'

export interface AttachmentDto {
  id: string
  name: string
  path: string
  isImage: boolean
  url?: string
  size?: number
}

export interface UsageDto {
  inputTokens: number
  outputTokens: number
  cachedInputTokens: number
  model?: string
  time?: string
}

export interface ChatModelDto {
  key: string
  provider: string
  name: string
  modelId: string
}

export interface WorkspaceDto {
  path: string
  name: string
}

export interface AskOptionDto {
  text: string
  description?: string
}

export interface AskQuestionDto {
  text: string
  options: AskOptionDto[]
}

export interface AskStateDto {
  requestId: string
  questions: AskQuestionDto[]
}

export type DiffLineKind = 'add' | 'del' | 'ctx'

export interface DiffLineDto {
  kind: DiffLineKind
  text: string
}

export interface DiffHunkDto {
  oldStart: number
  oldLines: number
  newStart: number
  newLines: number
  lines: DiffLineDto[]
}

export interface FileDiffDto {
  path: string
  added: number
  removed: number
  truncated?: boolean
  hunks: DiffHunkDto[]
}

export interface ToolDetailDto {
  name: string
  args: string
  result: string
  /** 气泡标题的展示文案，如「运行命令 · npm test」；无时界面回退到工具名。 */
  label?: string
  /** 编辑工具产生的文件差异；有值时显示 diff 卡片。 */
  diffs?: FileDiffDto[]
}

export interface ApprovalDetailDto {
  toolName: string
  detail: string
  pending: boolean
  outcome?: 'approved' | 'rejected' | 'cancelled'
}

export interface ChatMessageDto {
  id: string
  kind: ChatMessageKind
  text: string
  time: string
  expanded?: boolean
  pending?: boolean
  reasoningDone?: boolean
  tool?: ToolDetailDto
  usage?: UsageDto
  attachment?: AttachmentDto
  approval?: ApprovalDetailDto
}

export interface ChatSnapshotDto {
  sessionId: string
  messages: ChatMessageDto[]
  draft: string
  attachments: AttachmentDto[]
  generating: boolean
  models: ChatModelDto[]
  selectedModelKey: string | null
  approvalMode: ApprovalMode
  /** 未绑定工作区时为 null；宿主省略 null 字段，运行时可能收到 undefined，store 会归一为 null。 */
  workspace: WorkspaceDto | null
  workspaces: WorkspaceDto[]
  usage: UsageDto | null
  ask: AskStateDto | null
}

export interface SessionSummaryDto {
  id: string
  title: string
  workspacePath?: string
  workspaceName?: string
  createdAt: string
  updatedAt: string
  messageCount: number
  lastUserText?: string
  modelKey?: string
  /** 该会话是否正在生成回复；宿主在生成开始/结束时推送 session.updated 刷新 */
  generating?: boolean
}

export interface ThemeDto {
  mode: 'dark' | 'light'
  accent: string
  accentL: number
  accentC: number
  accentH: number
  followSystem: boolean
}

export interface GlobalUsageDto {
  inputTokens: number
  outputTokens: number
  cachedInputTokens: number
  conversationCount: number
  pokeCount: number
  headPatCount: number
  runSeconds: number
}

export interface AppSnapshotDto {
  protocolVersion: 1
  window: 'app'
  route?: string
  pet: { name: string; initial: string }
  theme: ThemeDto
  usage: GlobalUsageDto
  sessions: SessionSummaryDto[]
  activeSessionId: string | null
  chat: ChatSnapshotDto | null
  /** 全部已知工作区（含没有会话的）；同样会被 WhenWritingNull 省略，读取时按空数组兜底 */
  workspaces: WorkspaceDto[]
}

/* ============================== 设置 DTO ============================== */

export interface ChatProviderModelDto {
  modelId: string
  name?: string
  isActive: boolean
}

export interface ProviderDto {
  name: string
  baseUrl: string
  apiKey: string
  models: ChatProviderModelDto[]
  /** 可选：重命名提供商时的旧名称（仅 saveProvider 入参）。 */
  previousName?: string
}

export interface McpServerDto {
  name: string
  enabled: boolean
  transport: 'stdio' | 'http'
  command: string
  arguments: string[]
  workingDirectory: string
  environment: Record<string, string>
  url: string
  headers: Record<string, string>
  timeoutSeconds: number
  /** 可选：重命名服务器时的旧名称（仅 save 入参）。 */
  previousName?: string
}

export interface McpToolDto {
  name: string
  description: string
}

export interface SkillDto {
  name: string
  description: string
  source: string
  filePath: string
  enabled: boolean
  content?: string
}

export type ConfigFieldKind = 'text' | 'password' | 'multiline' | 'number' | 'boolean' | 'select' | 'list'

export interface ConfigFieldDto {
  key: string
  label: string
  description?: string
  kind: ConfigFieldKind
  options?: string[]
  value: unknown
}

export type PluginState = 'enabled' | 'disabled' | 'loading' | 'discovered' | 'failed'

export interface PluginDto {
  id: string
  name: string
  version: string
  author: string
  description: string
  builtIn: boolean
  directory: string
  state: PluginState
  error?: string
  toolNames: string[]
  failureCount: number
  hasConfig: boolean
}

export type TaskKind = 'once' | 'daily' | 'weekly' | 'interval'

export interface TaskDto {
  id: string
  name: string
  content: string
  enabled: boolean
  kind: TaskKind
  onceDate?: string
  onceTime?: string
  timeOfDay?: string
  weekdays: number[]
  intervalMinutes: number
  completed: boolean
  nextRunAt?: string
  lastRunAt?: string
}

export interface TaskInputDto {
  id?: string
  name: string
  content: string
  enabled: boolean
  kind: TaskKind
  onceDate?: string
  onceTime?: string
  timeOfDay?: string
  weekdays: number[]
  intervalMinutes: number
}

export interface ScheduledTaskLogDto {
  text: string
  isError: boolean
}

export interface Live2DModelDto {
  name: string
  directory: string
  active: boolean
  gameExpression?: string
  musicExpression?: string
  valid: boolean
  error?: string
}

export interface WindowStateDto {
  visible: boolean
  maximized: boolean
}

export interface HostLogDto {
  level: 'debug' | 'info' | 'warn' | 'error'
  msg: string
}

export interface ToastDto {
  level: 'info' | 'success' | 'error'
  text: string
}

/* ============================== 事件 / ops ============================== */

export type ChatOp =
  | { op: 'msg:add'; msg: ChatMessageDto }
  | { op: 'msg:insert'; index: number; msg: ChatMessageDto }
  | { op: 'msg:remove'; id: string }
  | { op: 'msg:text'; id: string; mode: 'append' | 'set'; text: string }
  | { op: 'msg:patch'; id: string; patch: Partial<ChatMessageDto> }
  | { op: 'turn'; generating: boolean }
  | { op: 'attachments'; attachments: AttachmentDto[] }
  | { op: 'approval'; mode: ApprovalMode }
  | { op: 'workspace'; workspace: WorkspaceDto | null; workspaces: WorkspaceDto[] }
  | { op: 'models'; models: ChatModelDto[]; selectedModelKey: string | null }
  | { op: 'ask'; ask: AskStateDto | null }
  | { op: 'usage'; usage: UsageDto | null }

export interface ChatOpsPayload {
  sessionId: string
  rev: number
  ops: ChatOp[]
}

export interface SessionUpdatedPayload {
  sessions: SessionSummaryDto[]
  activeId: string | null
  rev: number
}

export interface ThemeEventPayload extends ThemeDto {
  rev: number
}

export type HostEventName =
  | 'chat.ops'
  | 'session.updated'
  | 'host.theme'
  | 'host.log'
  | 'window.state'
  | 'nav.goto'
  | 'toast'

export interface HostEvent {
  name: HostEventName
  payload: unknown
}

export type WindowResizeEdge = 'l' | 'r' | 't' | 'b' | 'lt' | 'rt' | 'lb' | 'rb'
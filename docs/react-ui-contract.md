# React UI 桥接契约（v1，冻结）

> 状态：**已冻结**，实现以本文件为准。
> 上游方案：[react-ui-refactor-plan.md](react-ui-refactor-plan.md) v4；窗口/WebView 前置结论见历史存档 [webview-chat.md](webview-chat.md)。
> TypeScript 类型镜像：`SoufflePet/web/src/bridge/contracts.ts`；宿主实现：`SoufflePet/Services/Web/`。
> 变更规则：任何字段/方法/事件的新增都必须升 `protocolVersion`；v1 内只允许**新增可选字段**与**新增方法**，不允许改语义或删除。

## 1. 为什么有这一层

- 一个 WebView2 窗口承载全部业务界面（侧栏 + 对话 + 设置），宿主只保留一个原生桌宠窗口。
- 页面不直接读本地文件、不进 SQLite、不发网络请求；所有状态读写都经宿主白名单方法。
- 页面崩溃/刷新后可以只靠 `hello → snapshot` 恢复；增量事件带 `rev`，跳号即重同步。

## 2. 传输

### 2.1 页面 → 宿主

NativeWebView 注入全局函数 `invokeCSharpAction(string)`。页面发送：

```json
{ "v": 1, "id": 42, "type": "req", "method": "chat.send", "params": { "text": "你好" } }
```

- `id`：页面内单调递增整数；响应必须带回同 id。
- `method`：见 §5 白名单；未知方法返回 `unknown_method`。
- `params`：对象；无参数时省略或 `{}`。

### 2.2 宿主 → 页面

宿主调用：

```js
window.__bridge && window.__bridge.receive(<object>)
```

`<object>` 是以下三种之一：

```json
{ "v": 1, "id": 42, "ok": true, "result": null }
{ "v": 1, "id": 42, "ok": false, "error": { "code": "io_error", "message": "保存失败", "details": null } }
{ "v": 1, "type": "event", "name": "chat.ops", "rev": 1024, "payload": { "sessionId": "s1", "rev": 1024, "ops": [] } }
{ "v": 1, "type": "snapshot", "rev": 1024, "data": { "..." : "..." } }
```

- `event.rev` 是 v1 新增的顶层字段；`payload` 可以是对象/数组/标量/null，非对象 payload 也能排序。对象 payload 内仍保留 `rev` 以兼容 M0–M7 页面。
- `event-file` 的 `/state` 文件只装 `payload` 本体；页面用 envelope 上的 `rev` 分发。

### 2.3 握手与重同步

1. 页面加载完成 → `hello { protocolVersion: 1, route: "#/chat/s1" }`。
2. 宿主回 `snapshot`（含会话列表、活动会话、主题、全局用量）。`snapshot.rev` 是宿主当前全局 rev。
3. 之后宿主推 `event`；页面按 `name` 分发。
4. 页面发现 `event.rev`（兼容对象 payload 内的 `payload.rev`）不等于 `lastRev + 1`（允许同 rev 重发）→ 调 `app.resync`，宿主回新的 `snapshot`；期间事件继续排队。
5. 窗口隐藏时事件仍入队；显示时若积压超过 2 MB 或 rev 跨度 > 512，宿主直接发 `snapshot`。

### 2.4 合帧、分片与超大载荷

- 宿主对 `chat.ops` 做 40 ms 合帧（不是为省 IPC，而是少触发页面 Markdown 重解析）。
- 单条 `receive` 脚本超过 256 KB 时，宿主把 JSON 写入临时文件，经 `LocalFileServer /state?token=` 提供：
  - 先发 `{ "type": "snapshot-file", "rev": 1024, "url": "/state?token=..." }` 或 `{ "type": "event-file", "name": "chat.ops", "rev": 1024, "url": "..." }`；
  - 页面 `fetch(url)` 后按正常结构应用；
  - token 一次性、5 分钟过期、只允许本机同源读取。

## 3. 错误码

| code | 含义 | 页面建议 |
| --- | --- | --- |
| `invalid_argument` | 参数缺失/类型错误/校验失败 | 表单内联提示 |
| `not_found` | 会话/提供商/任务/文件不存在 | 刷新列表并 toast |
| `forbidden` | 路径不在允许范围，或拒绝打开可执行/脚本文件 | 说明性提示 |
| `conflict` | 同名、重复、正在生成等 | 内联提示 |
| `io_error` | 落盘/读文件失败 | toast + 保留表单 |
| `timeout` | 第三方请求超时（模型列表/MCP/导入） | 可重试 |
| `unsupported` | 当前环境不支持（如 WebView2 缺失） | 说明性提示 |
| `internal` | 未预期异常（宿主已记日志） | 通用错误 toast |

所有错误都不包含堆栈；`details` 只放可展示的附加信息。

## 4. 核心 DTO

字段名即 JSON 名；时间一律 ISO 8601（含偏移），页面负责本地化。

```ts
type ApprovalMode = 'manual' | 'full'
type ChatMessageKind =
  | 'user' | 'assistant' | 'reasoning' | 'tool'
  | 'notice' | 'attachment' | 'approval'

interface AttachmentDto {
  id: string            // 会话内稳定；拖放/粘贴/选择共用
  name: string
  path: string          // 绝对路径；空串表示尚未落盘的粘贴图片
  isImage: boolean
  url?: string          // /local-file?token=…；仅图片
  size?: number
}

interface UsageDto {
  inputTokens: number
  outputTokens: number
  cachedInputTokens: number
  model?: string        // provider/modelId
  time?: string         // ISO
}

interface ChatModelDto {
  key: string           // provider/modelId
  provider: string
  name: string
  modelId: string
}

interface WorkspaceDto { path: string; name: string }

interface AskOptionDto { text: string; description?: string }
interface AskQuestionDto { text: string; options: AskOptionDto[] }
interface AskStateDto { requestId: string; questions: AskQuestionDto[] }

interface DiffLineDto { kind: 'add' | 'del' | 'ctx'; text: string }
interface DiffHunkDto {
  oldStart: number
  oldLines: number
  newStart: number
  newLines: number
  lines: DiffLineDto[]
}
interface FileDiffDto {
  path: string
  added: number
  removed: number
  truncated?: boolean            // 文件过大或差异过长，只展示了一部分
  hunks: DiffHunkDto[]
}

interface ChatMessageDto {
  id: string
  kind: ChatMessageKind
  text: string
  time: string                  // ISO
  expanded?: boolean            // 思考/工具折叠态
  pending?: boolean             // 审批等待中
  reasoningDone?: boolean
  tool?: {
    name: string
    args: string
    result: string
    label?: string               // 气泡标题，如「运行命令 · npm test」；无时回退到「调用工具：name」
    diffs?: FileDiffDto[]        // 编辑工具产生的文件差异；有值时气泡内显示 diff 卡片
  }
  usage?: UsageDto
  attachment?: AttachmentDto
  approval?: {
    toolName: string
    detail: string
    pending: boolean
    outcome?: 'approved' | 'rejected' | 'cancelled'
  }
}

interface ChatSnapshotDto {
  sessionId: string
  messages: ChatMessageDto[]
  draft: string
  attachments: AttachmentDto[]
  generating: boolean
  models: ChatModelDto[]
  selectedModelKey: string | null
  approvalMode: ApprovalMode
  workspace: WorkspaceDto | null
  workspaces: WorkspaceDto[]
  usage: UsageDto | null        // 本会话累计
  ask: AskStateDto | null
}

interface SessionSummaryDto {
  id: string
  title: string
  workspacePath?: string
  workspaceName?: string
  createdAt: string
  updatedAt: string
  messageCount: number
  lastUserText?: string
  modelKey?: string
}

interface ThemeDto {
  mode: 'dark' | 'light'
  accent: string                // #RRGGBB
  accentL: number
  accentC: number
  accentH: number
  followSystem: boolean
}

interface GlobalUsageDto {
  inputTokens: number
  outputTokens: number
  cachedInputTokens: number
  conversationCount: number
  pokeCount: number
  headPatCount: number
  runSeconds: number
}

interface AppSnapshotDto {
  protocolVersion: 1
  window: 'app'
  route?: string
  pet: { name: string; initial: string }
  theme: ThemeDto
  usage: GlobalUsageDto
  sessions: SessionSummaryDto[]
  activeSessionId: string | null
  chat: ChatSnapshotDto | null
  workspaces: WorkspaceDto[]
}
```

## 5. 方法与事件

### 5.1 方法白名单

#### 应用 / 导航

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `hello` | `{protocolVersion,route?}` | `snapshot`（宿主主动推送，不按 req 回包） |
| `app.resync` | `{}` | `snapshot`（宿主主动推送） |
| `nav.goto` | `{route}` | `null`（宿主也可主动推 `nav.goto` 事件） |

#### 会话

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `session.list` | `{}` | `SessionSummaryDto[]` |
| `session.new` | `{workspacePath?}` | `SessionSummaryDto`（草稿：不落盘、不进列表，首条消息后才出现在 `session.list`） |
| `session.open` | `{sessionId}` | `ChatSnapshotDto`（切换会话时页面应调用它来同步宿主活动会话；已打开的会话不会重新读盘） |
| `session.close` | `{sessionId}` | `null`（释放非活动会话的内存；历史留在磁盘，下次打开时重新加载。活动/生成中/有草稿的会话会返回 `conflict`） |
| `session.rename` | `{sessionId,title}` | `SessionSummaryDto` |
| `session.delete` | `{sessionId}` | `{sessions:SessionSummaryDto[],activeId:string|null}` |
| `session.search` | `{query}` | `SessionSummaryDto[]` |

#### 对话

除 `chat.workspace.pick` / `chat.workspace.rename` / `chat.link.open` 外，对话方法都接受可选 `sessionId`：**显式 `sessionId` 决定操作对象，省略时回退宿主活动会话（v1 向后兼容）**。多个会话可以同时生成，`chat.ops` 按 `sessionId` 分发。

| 方法 | 参数 | 返回 / 副作用 |
| --- | --- | --- |
| `chat.send` | `{sessionId?,text,attachmentIds?:string[]}` | `null`；事件 `chat.ops` |
| `chat.stop` | `{sessionId?}` | `null` |
| `chat.draft` | `{sessionId?,text}` | `null`（100 ms 防抖） |
| `chat.attach.pick` | `{sessionId?}` | `{attachments:AttachmentDto[]}` |
| `chat.attach.paste` | `{sessionId?}` | `{attachments:AttachmentDto[]}` |
| `chat.attach.remove` | `{sessionId?,id}` | `{attachments:AttachmentDto[]}` |
| `chat.model.select` | `{sessionId?,key}` | `null` |
| `chat.approval.mode` | `{sessionId?,mode:'manual'|'full'}` | `null` |
| `chat.approval.decide` | `{sessionId?,messageId,approve:boolean}` | `null` |
| `chat.workspace.switch` | `{sessionId?,path}` | `{workspace:WorkspaceDto|null,workspaces:WorkspaceDto[]}` |
| `chat.workspace.pick` | `{}` | `WorkspaceDto|null` |
| `chat.workspace.delete` | `{sessionId?,path,deleteSessions?:boolean}` | `{workspaces:WorkspaceDto[]}` 或 `{sessions:SessionSummaryDto[],activeId:string\|null}`（`deleteSessions` 为 true 时组内会话一并移入回收站） |
| `chat.workspace.rename` | `{path,name}` | `{workspaces:WorkspaceDto[]}`（仅改展示名，不改磁盘目录；`name` 为空则恢复目录名） |
| `chat.ask` | `{sessionId?,requestId,action:'prev'|'next'|'select'|'custom'|'submit'|'cancel',payload?}` | `null` |
| `chat.file.open` | `{sessionId?,path}` | `null`（仅存在文件；系统默认程序） |
| `chat.link.open` | `{url}` | `null`（仅 http/https） |
| `chat.msg.expand` | `{sessionId?,messageId,expanded:boolean}` | `null` |

#### 窗口

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `window.drag` | `{}` | `null` |
| `window.resize` | `{edge:'l'|'r'|'t'|'b'|'lt'|'rt'|'lb'|'rb'}` | `null` |
| `window.minimize` | `{}` | `null` |
| `window.maximize` | `{}` | `null`（切换最大化/还原） |
| `window.close` | `{}` | `null`（隐藏，不退出） |
| `window.state` | `{}` | `{visible,maximized}`；事件同名 |

#### 设置

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `settings.llm.list` | `{}` | `{providers:ProviderDto[],enabledModels:ChatModelDto[],defaultKey:string|null}` |
| `settings.llm.saveProvider` | `{name,baseUrl,apiKey,models,previousName?}` | `ProviderDto` |
| `settings.llm.deleteProvider` | `{name}` | `{providers:ProviderDto[]}` |
| `settings.llm.fetchModels` | `{name,baseUrl,apiKey}` | `{modelIds:string[]}` |
| `settings.llm.setEnabled` | `{provider,modelId,enabled}` | `ProviderDto` |
| `settings.llm.setDefault` | `{key}` | `null` |
| `settings.mcp.list` | `{}` | `{servers:McpServerDto[],path:string,error?:string}` |
| `settings.mcp.save` | `McpServerDto`（可带 `previousName?`） | `{servers:McpServerDto[]}` |
| `settings.mcp.delete` | `{name}` | `{servers:McpServerDto[]}` |
| `settings.mcp.test` | `{server:McpServerDto}` | `{tools:McpToolDto[]}` |
| `settings.mcp.refreshTools` | `{name}` | `{tools:McpToolDto[]}` |
| `settings.skills.list` | `{}` | `{skills:SkillDto[],directories:string[]}` |
| `settings.skills.setEnabled` | `{name,enabled}` | `SkillDto` |
| `settings.skills.openFolder` | `{path}` | `null` |
| `settings.skills.detail` | `{name}` | `SkillDto`（含 `content`） |
| `settings.plugins.list` | `{}` | `{plugins:PluginDto[],installDir:string}` |
| `settings.plugins.setEnabled` | `{id,enabled}` | `PluginDto` |
| `settings.plugins.getConfigSchema` | `{id}` | `{fields:ConfigFieldDto[],config:object}` |
| `settings.plugins.saveConfig` | `{id,config:object}` | `PluginDto` |
| `settings.plugins.import` | `{}`（宿主开文件选择器） | `{success,message,pluginId?}` |
| `settings.tasks.list` | `{}` | `{tasks:TaskDto[],logs:ScheduledTaskLogDto[]}` |
| `settings.tasks.save` | `TaskInputDto` | `TaskDto` |
| `settings.tasks.delete` | `{id}` | `{tasks:TaskDto[]}` |
| `settings.tasks.setEnabled` | `{id,enabled}` | `TaskDto` |
| `settings.tasks.runNow` | `{id}` | `null` |
| `settings.tasks.nextRun` | `{id}` | `{nextRunAt:string|null}` |
| `settings.tasks.clearLogs` | `{}` | `null` |
| `settings.models.list` | `{}` | `{models:Live2DModelDto[],activeIndex:number}` |
| `settings.models.importFolder` | `{}`（宿主开目录选择器） | `{models:Live2DModelDto[],message:string}` |
| `settings.models.importArchive` | `{}`（宿主开文件选择器） | `{models:Live2DModelDto[],message:string}` |
| `settings.models.remove` | `{index}` | `{models:Live2DModelDto[],activeIndex:number}` |
| `settings.models.setActive` | `{index}` | `null`（触发桌宠重载） |
| `settings.models.validate` | `{index}` | `{ok:boolean,message:string}` |
| `settings.appearance.get` | `{}` | `ThemeDto` |
| `settings.appearance.setAccent` | `{accent:'#RRGGBB'}` | `ThemeDto` |
| `settings.appearance.setFollowSystem` | `{enabled:boolean}` | `ThemeDto` |
| `settings.appearance.reset` | `{}` | `ThemeDto` |
| `settings.appearance.preview` | `{accent}` | `ThemeDto`（不落盘，仅宿主广播） |
| `settings.appearance.setSidebarCollapsed` | `{collapsed:boolean}` | `null` |

设置 DTO（字段与现有模型一致）：

```ts
interface ChatProviderModelDto { modelId: string; name?: string; isActive: boolean }
interface ProviderDto { name: string; baseUrl: string; apiKey: string; models: ChatProviderModelDto[] }

interface McpServerDto {
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
}
interface McpToolDto { name: string; description: string }

interface SkillDto {
  name: string; description: string; source: string; filePath: string
  enabled: boolean; content?: string
}

interface ConfigFieldDto {
  key: string; label: string; description?: string
  kind: 'text' | 'multiline' | 'number' | 'boolean' | 'select' | 'list'
  options?: string[]; value: unknown
}
interface PluginDto {
  id: string; name: string; version: string; author: string
  description: string; builtIn: boolean; directory: string
  state: 'enabled' | 'disabled' | 'loading' | 'discovered' | 'failed'
  error?: string; toolNames: string[]; failureCount: number
  hasConfig: boolean
}

interface TaskDto {
  id: string; name: string; content: string; enabled: boolean
  kind: 'once' | 'daily' | 'weekly' | 'interval'
  onceDate?: string; timeOfDay?: string   // timeOfDay 由单次 / 每日 / 每周共用
  weekdays: number[]      // 0=Sunday … 6=Saturday
  intervalMinutes: number
  completed: boolean; nextRunAt?: string; lastRunAt?: string
  workspacePath?: string; modelKey?: string
}
interface TaskInputDto {
  id?: string; name: string; content: string; enabled: boolean
  kind: TaskDto['kind']; onceDate?: string
  timeOfDay?: string; weekdays: number[]; intervalMinutes: number
  workspacePath?: string; modelKey?: string
}
interface ScheduledTaskLogDto { text: string; isError: boolean }

interface Live2DModelDto {
  name: string; directory: string; active: boolean
  gameExpression?: string; musicExpression?: string; valid: boolean
  error?: string
}
```

### 5.2 事件

| 事件 | payload | 说明 |
| --- | --- | --- |
| `chat.ops` | `{sessionId,rev,ops:ChatOp[]}` | 当前会话增量；页面按 sessionId 存放 |
| `session.updated` | `{sessions:SessionSummaryDto[],activeId:string|null,rev}` | 新建/重命名/删除/排序/标题生成 |
| `host.theme` | `ThemeDto & {rev}` | 强调色/跟随系统变化 |
| `host.log` | `{level:'debug'|'info'|'warn'|'error',msg}` | `?debug=1` 浮层 |
| `window.state` | `{visible:boolean,maximized:boolean,rev}` | 最大化/还原/显示 |
| `nav.goto` | `{route}` | 宿主让页面跳转（如旧菜单打开设置） |
| `toast` | `{level,text}` | 宿主主动提示 |

### 5.3 `ChatOp`

```ts
type ChatOp =
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
```

约束：
- `msg:add` 永远追加到末尾；需要插到中间（审批/工具/文件卡片）用 `msg:insert`。
- `msg:text` 纯追加用 `append`，占位替换/中断/错误追加用 `set`。
- `msg:patch` 只允许 `expanded/pending/reasoningDone/tool/approval/usage/text?`；`text` 用 `msg:text` 更省。
- 同一 session 的 op 顺序即消息顺序；页面不得按索引定位消息，索引用 `id`。
- 事件按 sessionId 过滤；切会话时不丢其他会话已缓存的 op（页面按会话存）。

## 6. 颜色与主题

- 页面只从 `snapshot.theme` / `host.theme` 取权威状态；URL 的 `?accent=` 只用于首帧。
- 首帧由 `src/theme/boot.ts` 在 React 挂载前执行（main 的第一个 import），不使用内联脚本，满足 CSP。
- `accent` 必须是 `#RRGGBB`，宿主保证在 sRGB gamut 内；页面用 `src/theme/oklch.ts` 复核并夹回 gamut。
- 页面不得写死色值；`src/styles/tokens.css` 是唯一来源，`scripts/check-colors.mjs` 在构建时检查。
- 跟随系统开启时宿主在系统强调色变化后广播；关闭时保留用户颜色。

## 7. 存储与数据兼容

```
%USERPROFILE%\.souffle\
  sessions\
    index.json                       # 会话索引
    <workspace-group>\<id>\conversation.jsonl
    <workspace-group>\<id>\window.json
    .deleted\<id>-<timestamp>\
    <workspace-group>\workspace.txt  # 分组对应的工作区全路径（重建索引用）
  memory.json                        # 全局长期记忆（所有会话共享）
  statistics.json
  scheduled-tasks.log
  ui-window.json
  attachments\                       # 聊天附件副本
```

- 会话按工作区分组存放：目录名是「可读前缀 + 路径哈希」（无工作区为 `_default`），避免同名目录冲突；切换/移除工作区时会话目录随之搬到新分组。
- `index.json` 见方案 §5.3；字段只增不改。
- `conversation.jsonl` 行格式与旧文件相同（`Time/Role/Text/...`）。
- `SoufflePet.json` 新增 `UiAccentColor`、`UiAccentFollowSystem`、`UiSidebarCollapsed`、`UiLastSessionId`；迁移期的 `UiRenderer` 已在 M7 删除（旧配置里的该键会被忽略）。
- 不做旧数据迁移：不导入旧 `conversations.jsonl`，也不自动搬 `%AppData%\SoufflePet\sessions`；需要旧记录时手工拷贝到 `~/.souffle/sessions/<workspace-group>/<id>/`。
- 删除会话进回收站；索引损坏时按会话目录重建（工作区分组从 `workspace.txt` 还原）。
- 草稿会话只在内存里：`session.new` 不写索引、不建会话目录，首条用户消息落盘后才有目录与索引记录（见 §13-21）。
- `statistics.json`、`memory.json` 不迁移、不改字段。

## 8. 安全规则

1. 只监听 `localhost`；页面不提供任何 HTTP 读会话接口。
2. 方法白名单按域划分；未列出的方法直接 `unknown_method`。
3. 路径参数（附件/打开文件/工作区/模型/导入）在宿主 `Path.GetFullPath` 后校验：
   - 文件必须存在；
   - 目录必须存在；
   - `chat.file.open` 只允许当前会话已登记的附件或会话工作区内的文件，并拒绝 `.exe/.bat/.ps1/.dll/.lnk` 等可执行/脚本扩展名；
   - 模型导入只允许 `wwwroot/models` 与用户选择的源目录；
   - 不暴露任意文件读取。
4. 本地图片只走 `/local-file?token=`；token 随机、短期、只登记已校验路径/内存字节。同一路径复用 token（12 小时），过期 token 按分钟级批量清理，避免长会话/重同步累积。
5. `chat.link.open` 只允许 `http/https`；`window.open`/外站导航由宿主 `NavigationStarted` 取消。
6. 生产关闭 DevTools；页面默认右键菜单在非输入框/可编辑区且无文本选区时禁用（输入框与复制选区仍可用）。CSP 由 `LocalFileServer` 对 `/app/*` 下发：
   `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'none'`。
7. 页面不请求 CDN/网络字体/远程图片；所有依赖随包分发。
8. RPC 载荷上限 256 KB（超出走 `/state`）；单请求 30 s 超时（模型列表/MCP/导入 60 s）。
9. 写请求（`session.*` 变更、`chat.*`、`settings.*` 保存）由宿主 `SemaphoreSlim(1,1)` 串行执行；只读列表/详情查询与 `window.*` 即时执行。请求 token 绑定主窗口生命周期，窗口关闭时取消在途 Core 操作并停止处理排队请求。

## 9. 路由

- `#/chat/:sessionId`；`#/chat` 自动跳到活动会话。
- `#/settings/{llm,models,appearance}`；`#/extensions/{plugins,skills,mcp}`（插件、技能、MCP 合并页，页内标签切换）；`#/tasks/{tasks,history}`（自动化任务独立页，页内标签切换，缺省为 `tasks`）。
- 页面启动时若 URL 无路由，用 snapshot 的 `activeSessionId` 替换；非法路由回落到 `#/chat`。
- 宿主 `nav.goto` 只改 hash，不重建窗口；浏览器前进/后退可用。
- 侧栏开合与抽屉是纯页面状态；开合偏好同步到 `UiSidebarCollapsed`（防抖 300 ms）。桌面宽度下收起即完全隐藏（宽度 0，不保留图标轨道），开关固定在标题栏最左侧；<720 px 使用抽屉，抽屉开合不写入折叠偏好。
- 会话搜索是「工作区」分区右侧操作区里的一个图标：点击后整个操作区（标题 + 选择工作区 + 新建会话）展开为搜索输入框，`Esc` 或点 × 收起并清空关键字；收起态不常驻搜索框。

## 10. 窗口行为

- 关闭 = `Hide()`；退出由托盘/应用生命周期负责。
- 标题栏空白区 `pointerdown` → `window.drag`（宿主 `ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION`）。
- 8 向缩放：页面最外圈 6 px 判定并设 cursor，`pointerdown` → `window.resize`；宿主 `GetCursorPos` 填 lParam 后发 `WM_NCLBUTTONDOWN`。最大化时页面禁用边带。
- 双击标题栏 → `window.maximize`；最大化状态由 `window.state` 同步。
- 首次显示 1120×760 DIP，最小 400×520；几何存 `ui-window.json` 并夹到工作区。
- `Esc` 只关闭最上层浮层/抽屉，不隐藏主窗口；快捷键 `Ctrl+Alt+Space` 由宿主切换窗口显隐。

## 11. Mock 模式（前端独立开发/验收）

- 页面检测 `typeof window.invokeCSharpAction !== 'function'` 或 URL `?mock=1` 时启用 mock host。
- mock 实现同一套方法与事件，覆盖：
  - 空会话欢迎页；
  - 富消息（Markdown/表格/代码/KaTeX/思考/工具/审批/提醒/附件/用量）；
  - `?scenario=long` 500 条消息；
  - 模拟流式（40 ms 一个 chunk）；
  - 全部设置面板 fixture。
- `dev/widths.html` 用 iframe 同时渲染 1416/1024/800/652/420 五档，供截图验收。
- mock host 只存在于 `src/bridge/mock-host.ts`，生产构建也会打入（体积小），但只有无宿主时才启用。

## 12. 版本与兼容

| 项 | 值 |
| --- | --- |
| `protocolVersion` | 1 |
| 最低 WebView2 | Chromium 156（Avalonia.Controls.WebView 12.1.1 自带） |
| 页面 base | `/app/` |
| 事件 rev | 宿主全局单调递增；页面按 session 缓存 |
| 后端 Core | `SoufflePet.Core` net10.0，无 Avalonia 引用 |

## 13. 与 v4 方案的差异记录

1. 视觉基线 `docs/prototype/chat-mock.html` 不存在 → 用 SPA mock + `dev/widths.html` 代替。
2. 窗口几何从 `window.json` 改为 `ui-window.json`（`window.json` 继续存历史窗口轮数）。
3. 不新增 `LegacyChatViewModel`：旧 `ChatViewModel` 原样保留到 P4，避免双状态源；M7 已随旧窗口一起删除。
4. 系统强调色用 DWM/注册表读取，不引入 WinRT `UISettings`。
5. Mermaid 不纳入 v1；KaTeX 纳入。
6. 定时任务沿用 Once/Daily/Weekly/Interval，不新增 Cron 表达式。
7. 侧栏底部不虚构账号，改为宠物名 + 运行统计 + 设置。
8. 拖放附件 v1 仍不做（`AllowExternalDrop` 在当前 WebView 包没有公开 API）。
9. M2 启动期实现 `BootstrapRpc`：`chat.send` 只回 toast，真实对话在 M4 接入；M4 已用 `ChatRpc` 取代它，`session.*`/`chat.*` 全部走 Core 的 `ChatSession`/`ChatSessionManager`。
10. 启动开关：M6 起**默认 React**；M7 删除 `--react-ui`、`--legacy-ui`、`UiRenderer` 与旧 Avalonia UI，只保留 `--web-dev`（Vite dev server）与 `--open-ui`（启动即呼出，验收用）。
11. `/state` 分片只存 payload / snapshot 本体（不存 envelope）；M2 曾登记整个 envelope，超过 256 KB 的快照会丢字段，M4 已修正。
12. `/local-file` token 有效期 12 小时（`/state` 仍 5 分钟且一次性），避免聊天记录滚动重渲染后图片 404。
13. 应用数据根目录可用环境变量 `SOUFFLEPET_DATA_DIR` 覆盖（自动化验收隔离用户数据）；默认仍是 `%AppData%\SoufflePet`。
14. M5 设置页需要重命名提供商/MCP 服务器：`ProviderDto` 与 `McpServerDto` 增加可选 `previousName`（仅 save 入参，响应不返回），不改变既有字段语义。
15. 插件配置保存只提交 configSchema 表单字段并保留 `config` 中未展示的键；字段值由宿主按 `configSchema.type` 转换，`list` 接受数组或换行/逗号分隔文本。
16. CSP 的 `font-src` 必须包含 `data:`：KaTeX 的字体在打包后有 data URI 回退，否则 Chromium 会在控制台报 CSP 错误。
17. M7 清理：删除旧 Avalonia 对话/设置窗口、六个设置面板 VM、`MarkdownTextBlock`/`MarkdownParser`、`WebViewPetWindow`、旧样式与 `ViewLocator`；`ChatWindowService` 由 `AppWindowService` 取代，`ChatTriggerRouter` 去掉 `Enabled/Disable`（不再有旧窗口消费者）；桌宠与 WebView2 主窗口各一个。WebView2 初始化失败只记录错误并弹一次系统提示，不再回退旧 UI。
18. M8 安全/性能：`AppWebWindow` 只允许 SPA 同源顶层导航，跨源 http(s) 交给系统浏览器、其它 scheme 直接取消；`NewWindowRequested` 不在应用内开新窗口；页面默认右键菜单按输入/选区放行，其余禁用。Windows 11 应用 DWM 圆角；RPC 耗时 ≥16 ms 打 Debug 日志。
19. 桥并发/取消：写请求串行化，只读与窗口命令不占写锁；请求使用窗口生命周期 token。`event` envelope 增加顶层 `rev` 以支持非对象 payload；`payload` 不再被强制转成空对象。
20. 设置落盘改为锁内序列化 + 临时文件原子替换；`/local-file` 按路径缓存 token 并每分钟清理过期项；`chat.file.open` 收窄到会话附件/工作区并拒绝可执行扩展名。
21. 新建会话延迟落盘：`session.new` 先只在内存里开草稿（不写 `sessions.json`、不建会话目录、不进侧栏与搜索），首条用户消息发出后由宿主转正；连点「新会话」复用/收敛草稿。删除最后一个会话时同样只补草稿，不再立刻造出空的已保存会话。
22. 会话存储迁到 `%USERPROFILE%\.souffle\sessions`：按工作区分组为 `<workspace-group>/<id>/`，索引改为根目录 `index.json`；不做旧 `%AppData%\SoufflePet` 会话与旧 `conversations.jsonl` 的自动迁移。其余数据（`memory.json`、统计、调度日志、窗口几何、附件）也统一在 `~/.souffle`。
23. 输入卡底部工具条：附件 → 审批模式（都在输入框内左侧），模型选择靠右、紧邻发送按钮；工作区选择仍留在输入卡上方的上下文行。
24. 消息元信息（时间、复制按钮、本轮用量）放在气泡外、气泡下方，跟随气泡左右对齐；复制按钮在整行 hover/focus 时显示。
25. 工具气泡标题改为可定制的 `tool.label`：内置工具按「动词 · 参数」生成（如 `运行命令 · npm test`、`读取 · a.txt`），插件可用 `AIFunction.AdditionalProperties["display"]` 模板覆盖，MCP 工具使用服务端 `title`；`label` 随工具记录落盘，刷新/重启后仍保留，缺失时界面回退到「调用工具：名称」。
26. 新增 `write_file` / `edit_file` 工具与 diff 显示：写入前按 `ToolApproval` 审批（审批详情带 diff 摘要），写入后用 Myers 行级 diff 把结构化 `FileDiffDto[]` 挂到 `tool.diffs`；完整 hunk 存在会话目录 `diffs/<id>.json`，`conversation.jsonl` 只存 `DiffFile` 引用，恢复会话时按需加载，避免会话文件膨胀。文件超过 200 KB / 3000 行或差异超过展示上限时只给截断提示；`run_shell_command` 改文件无法可靠还原 diff，不做推断。
27. 编辑工具保留原文件编码：读取时记录 BOM 与实际编码（UTF-8 / UTF-16 / UTF-32），写回沿用；无 BOM 只接受 UTF-8。Diff 卡片默认收起，只显示文件头与 `+N −M`，点击后才渲染 hunk 行，避免长会话里堆出大量 DOM。
28. 写文件改为「同目录临时文件 + `File.Replace`」原子替换，`write_file` / `edit_file` 覆盖已有文件前把旧版本备份到会话目录 `backups/`（同一路径只保留最近一份），删除会话时随目录进入 `.deleted`；工具结果会带回备份路径。
---
name: soufflepet-review
description: Code review checklist for the SoufflePet repository. Use when reviewing SoufflePet workspace diffs, commits, or PRs. Runs repo-specific validation (dotnet build, Core tests, pnpm typecheck, pnpm test) and applies SoufflePet coding invariants from AGENTS.md.
---

# SoufflePet Code Review Checklist

审查 SoufflePet 仓库改动时的标准验证流程和项目特定 Checklist。与 `TRAE-code-review` 技能配合使用 —— 后者负责通用代码审查流程，本 skill 补充仓库特定验证命令和约束。

## 审查流程

1. **确认范围**：`git diff --stat` + `git diff` 拿到完整改动
2. **推断意图**：理解改动的目的和设计方向
3. **验证构建**（按改动涉及的层选择）
4. **运行测试**（同上）
5. **类型检查**（React 前端改动必须跑）
6. **项目特定 Checklist**（见下）
7. **交叉验证**：如有候选问题，用 2 个独立 sub-agent 平行验证

## 验证命令

### .NET / Core 层改动

```powershell
# 编译整个解决方案
dotnet build SoufflePet.sln

# 只验证 .NET，跳过前端（需已有 wwwroot/app 产物）
dotnet build SoufflePet.sln -p:SkipWebBuild=true

# 输出被占用（MSB3026）时的备用路径
dotnet build SoufflePet.sln -p:BaseOutputPath=.verifybuild\bin\

# Core 领域层测试（会话状态机、会话管理、记忆、调度、token 格式）
dotnet test SoufflePet.Core.Tests/SoufflePet.Core.Tests.csproj --no-restore
```

### React 前端改动

```powershell
# 类型检查（必须）
pnpm --dir SoufflePet/web typecheck

# 全部前端单测
pnpm --dir SoufflePet/web test

# 生产构建验证
pnpm --dir SoufflePet/web build
```

### PowerShell 注意

- 命令串联统一使用 `;`，不要用 `&&`（PowerShell 7+ 虽支持但保持兼容）
- 避免 `find` / `grep` / `cat` / `head` / `tail` / `sed` / `awk`，用 Glob、Grep、Read 工具替代
- 前端命令在 `pnpm --dir SoufflePet/web` 下执行

## 项目特定 Checklist

### Live2D SDK 改动

- [ ] 改动前先读 README 的「对 Live2DCSharpSDK 的本地修改」一节
- [ ] 不要覆盖已有本地改动
- [ ] 上游 diff 需 `git diff --strip-trailing-cr`（CRLF 行尾）
- [ ] SDK 子项目用 MIT 许可头，主程序用 GPL-3.0-or-later

### Core 领域层改动

- [ ] `SoufflePet.Core` 不得引用 Avalonia；平台能力通过 `IPetController` / `IUiDispatcher` 注入
- [ ] `ChatSession` / `ChatSessionManager` 的公开方法需保证线程安全（正确使用 `_gate` 锁）
- [ ] Timer / 定时任务需在 `DisposeAsync` 中正确清理，避免进程不退出
- [ ] `IDisposable` 实现需遵循双模式（同步 + 异步），`Dispose` 调 `DisposeAsync().AsTask().GetAwaiter().GetResult()` 需注意 UI 线程死锁风险
- [ ] `Nullable enable` 项目，不引入新的可空性警告

### 插件系统改动

- [ ] 插件实现 `IMessagePlatform` 后会被 `PluginService` 自动注册到 `PlatformService`
- [ ] 插件生命周期与平台服务订阅严格同步：注册时绑定事件，注销前必须退订
- [ ] 插件退出清理逻辑若需切回 UI 线程，先判断当前线程：`if (Dispatcher.UIThread.CheckAccess()) { ... } else { Dispatcher.UIThread.InvokeAsync(...) }`

### WebView2 / Web 层改动

- [ ] WebView2 用户数据目录统一在 `%LocalAppData%`，不要写到安装目录
- [ ] React UI 构建依赖 Node.js 22.12+ 与 pnpm 10+
- [ ] Bridge RPC 方法遵循 `session.open` / `session.close` / `session.rename` 等命名

### Avalonia UI 改动

- [ ] 全局 TextBox 样式：单行文本垂直居中，多行顶部对齐
- [ ] `BringIntoViewOnFocusChange` 关闭，避免窗口切回时意外滚动
- [ ] XAML 附加事件处理器需显式 `AddHandler` 注册，消除 Rider 未使用警告

### 消息平台改动

- [ ] 消息推送使用 `PlatformIncomingMessage`，宿主侧按 `MessageId` 去重
- [ ] 平台提醒用独立 `IsPlatformNotice` 字段，不通过 `Display` 隐式判断
- [ ] 对话历史召回时过滤工具调用，仅保留纯文本对话片段

### 测试改动

- [ ] Core 测试用 xUnit + 内存 Fake（FakeLlm 等）
- [ ] 前端测试用 Vitest + Testing Library，jsdom 无布局需手动 stubGeometry
- [ ] 涉及 `setTimeout` / `requestAnimationFrame` 的前端测试需 fake timers

## 审查原则

- **Skip non-code files**（`.md`、`.json`、`.svg`、`cargo.lock`）除非改动涉及配置生效逻辑
- **UI 样式改动**假设设计者已确认视觉，只关注功能正确性和文档约束
- **编译成功假设**：静态类型项目假设改动可编译，除非 diff 直接证明相反
- **Evidence-based**：问题必须有具体文件+行号+事实依据，不输出"可能/也许"类猜测
- **Cross-validation**：候选问题用 2 个独立 sub-agent 平行验证，取 2/2 或 1/2 确认的问题

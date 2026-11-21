// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { FileText, Image as ImageIcon, Paperclip, Send, Square, Terminal, X } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { AttachmentDto, ChatSnapshotDto, SlashCommandDto, SlashCommandResultDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { useSlashPaletteStore } from '@/stores/slashPaletteStore'
import { useToastStore } from '@/stores/toastStore'
import { ApprovalPicker } from './ApprovalPicker'
import { AskPanel } from './AskPanel'
import { MentionPicker, mentionOptionId, type MentionEntry } from './MentionPicker'
import { ModelPicker } from './ModelPicker'
import { slashOptionId, SlashCommandMenu } from './SlashCommandMenu'
import {
  argumentView,
  commandListView,
  commandText,
  defaultSuggestionIndex,
  draftAfterPick,
  findCommand,
  handleSlashKey,
  parseSlashInput,
  resolveSlashView,
  startsWithSlashTrigger,
  useSlashCommands,
  type SlashSuggestion,
  type SlashView,
} from './slashCommands'
import { WorkspacePicker } from './WorkspacePicker'

interface ComposerProps {
  chat: ChatSnapshotDto
  hero?: boolean
}

/** 正在输入的 @ 引用片段：query 是 @ 之后、光标之前的文本。 */
interface MentionToken {
  query: string
  start: number
  end: number
}

/** 找出光标前正在输入的 @ 引用；@ 必须在行首或空白之后，且之后不能有空白。 */
function findMentionToken(value: string, caret: number): MentionToken | null {
  const before = value.slice(0, caret)
  const at = before.lastIndexOf('@')
  if (at < 0) return null
  if (at > 0 && !/\s/.test(before[at - 1])) return null
  const query = before.slice(at + 1)
  if (/\s/.test(query)) return null
  return { query, start: at, end: caret }
}

/** 把字节数格式化成附件副标题里的可读大小。 */
function formatSize(bytes?: number): string {
  if (bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return ''
  if (bytes < 1024) return `${Math.round(bytes)} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let index = 0
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024
    index += 1
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[index]}`
}

/** 文件胶囊第二行：扩展名 · 大小，例如「LOG · 33.9 KB」。 */
function attachmentMeta(name: string, size?: number): string {
  const dot = name.lastIndexOf('.')
  const extension = dot > 0 && dot < name.length - 1 ? name.slice(dot + 1).toUpperCase() : 'FILE'
  const readable = formatSize(size)
  return readable ? `${extension} · ${readable}` : extension
}

/** 从按钮 / 消息操作菜单唤起的命令面板状态：null 表示没打开，command 为空表示在选命令。 */
interface DetachedPalette {
  command: SlashCommandDto | null
}

export function Composer({ chat, hero = false }: ComposerProps) {
  const petName = useAppStore((state) => state.pet.name)
  const setDraft = useChatStore((state) => state.setDraft)
  const applyOps = useChatStore((state) => state.applyOps)
  const pushToast = useToastStore((state) => state.push)
  const paletteRequest = useSlashPaletteStore((state) => state.request)
  const clearPaletteRequest = useSlashPaletteStore((state) => state.clear)
  const commands = useSlashCommands()
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const composerRef = useRef<HTMLDivElement>(null)
  const composing = useRef(false)
  const draftTimer = useRef<number | null>(null)
  // 等待同步给宿主的草稿：卸载（切换会话）时补发一次，避免最后 100ms 的输入丢失
  const pendingDraft = useRef<string | null>(null)
  const sessionId = chat.sessionId
  const [mention, setMention] = useState<MentionToken | null>(null)
  const [mentionEntries, setMentionEntries] = useState<MentionEntry[]>([])
  const [mentionIndex, setMentionIndex] = useState(0)
  const [mentionLoading, setMentionLoading] = useState(false)
  const mentionRequest = useRef(0)
  // 命令面板：输入框里的「/」自动唤起；按钮 / 消息操作菜单用 detached 直接唤起
  const [detached, setDetached] = useState<DetachedPalette | null>(null)
  const [slashDismissed, setSlashDismissed] = useState(false)
  // 用户主动移动的高亮位置，连同「移动时看到的那份候选」一起记；
  // 候选一变（换命令、改过滤词、命令目录异步到达）就对不上，自动回落到默认位置。
  const [slashNav, setSlashNav] = useState<{ key: string; index: number } | null>(null)
  // 两个浮层的 id：输入框用 aria-controls 指向它们，aria-activedescendant 指到当前候选
  const slashListboxId = useId()
  const mentionListboxId = useId()

  useEffect(() => {
    const textarea = textareaRef.current
    if (!textarea) return
    textarea.style.height = 'auto'
    textarea.style.height = `${Math.min(160, Math.max(46, textarea.scrollHeight))}px`
  }, [chat.draft])

  useEffect(() => {
    return () => {
      if (draftTimer.current !== null) window.clearTimeout(draftTimer.current)
      if (pendingDraft.current !== null) {
        void invoke('chat.draft', { sessionId, text: pendingDraft.current }).catch(() => undefined)
      }
    }
  }, [sessionId])

  useEffect(() => {
    if (!mention) {
      setMentionEntries([])
      return
    }

    const seq = ++mentionRequest.current
    setMentionLoading(true)
    // 输入时逐字查询：防抖后再问宿主，避免每个字符一次 RPC
    const timer = window.setTimeout(() => {
      void invoke<{ entries: MentionEntry[] }>('chat.mention.search', { sessionId, query: mention.query })
        .then((result) => {
          if (mentionRequest.current !== seq) return
          setMentionEntries(result.entries)
          setMentionIndex(0)
        })
        .catch(() => {
          if (mentionRequest.current === seq) setMentionEntries([])
        })
        .finally(() => {
          if (mentionRequest.current === seq) setMentionLoading(false)
        })
    }, 120)

    return () => window.clearTimeout(timer)
  }, [mention, sessionId])

  // 消息操作菜单等入口请求打开命令面板：只响应本会话的请求
  useEffect(() => {
    if (!paletteRequest) return
    if (paletteRequest.sessionId !== sessionId) return
    clearPaletteRequest()
    setSlashDismissed(false)
    setDetached({ command: null })
  }, [paletteRequest, sessionId, clearPaletteRequest])

  const slashView = useMemo<SlashView | null>(() => {
    if (detached) {
      return detached.command
        ? argumentView(commands, detached.command, chat)
        : commandListView(commands)
    }
    if (slashDismissed) return null
    return resolveSlashView(commands, chat, chat.draft)
  }, [detached, slashDismissed, commands, chat])

  /**
   * @ 面板与命令面板浮在同一位置（.composer 的 left/right/bottom 完全一致），
   * 同时渲染会整块重叠，键盘又会作用在「被挡住」的那个。这里让它们互斥：
   * 命令优先——草稿以 / 开头时（如 /model @src）只显示命令面板，
   * @ 引用必须等命令面板关闭后才生效。渲染与键盘分支共用这个派生值，保证一致。
   */
  const showMention = mention !== null && slashView === null

  /**
   * 斜杠面板当前高亮项：**派生值**，不是 state。
   *
   * 参数阶段的默认高亮落在「当前值」上（已选中的模型 / 当前审批模式），这样面板一打开直接按
   * Enter 只会重新确认当前状态，不会静默切到列表第一项。默认位置随候选实时算出来，
   * 用户 ↑↓ / 悬停后的位置才记进 slashNav。
   *
   * 早先这里是 `useState(0)` + `useEffect(() => setSlashIndex(defaultIndex))`：候选一变，
   * 下标要等 effect flush 才更新。命令目录是异步拉取的，首帧 commands 为空、/model 落进
   * 「没有这条命令」分支（默认 0），目录到达后的那一帧里 DOM 已经有 m2 候选，但 effect 还没跑，
   * 此时回车就会读到旧下标 0 → 执行成 models[0]，静默换掉模型。测试里表现为偶发失败
   *（连跑 5 次挂 1 次，约 1s 的 waitFor 超时），真实使用中也是同一处隐患。
   * 改成派生后不存在「DOM 已更新、下标还没同步」的窗口。
   */
  const suggestionKey = slashView?.suggestions.map((item) => `${item.kind}:${item.value}`).join('|') ?? ''
  const slashIndex = slashNav !== null && slashNav.key === suggestionKey
    ? slashNav.index
    : defaultSuggestionIndex(slashView)
  const setSlashIndex = (index: number) => setSlashNav({ key: suggestionKey, index })

  // 当前高亮候选的 id：输入框（combobox）用 aria-activedescendant 指过去，
  // 焦点始终留在输入框里，读屏据此播报「当前选中了哪一项」。
  const slashActiveOptionId = slashView && slashView.suggestions.length > 0
    ? slashOptionId(slashListboxId, Math.min(slashIndex, slashView.suggestions.length - 1))
    : undefined
  const mentionActiveOptionId = showMention && mentionEntries.length > 0
    ? mentionOptionId(mentionListboxId, Math.min(mentionIndex, mentionEntries.length - 1))
    : undefined
  // detached 时焦点在面板自身，activedescendant 由面板自己维护，输入框不再声明展开
  const popupOpen = detached === null && (slashView !== null || showMention)
  // 命令面板即使没有候选也会渲染 listbox；@ 面板在「搜索中 / 无匹配」时只有空态提示，
  // 此时不声明 aria-controls，避免指向不存在的 id。
  const popupId = slashView
    ? slashListboxId
    : showMention && mentionEntries.length > 0
      ? mentionListboxId
      : undefined
  const popupActiveId = slashView ? slashActiveOptionId : showMention ? mentionActiveOptionId : undefined

  // 点面板与输入卡以外的地方收起 detached 面板（输入框触发的面板由失焦收起）
  useEffect(() => {
    if (!detached) return
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target
      if (target instanceof Node && composerRef.current?.contains(target)) return
      setDetached(null)
    }
    document.addEventListener('pointerdown', onPointerDown, true)
    return () => document.removeEventListener('pointerdown', onPointerDown, true)
  }, [detached])

  const syncDraft = (value: string) => {
    setDraft(sessionId, value)
    pendingDraft.current = value
    if (draftTimer.current !== null) window.clearTimeout(draftTimer.current)
    draftTimer.current = window.setTimeout(() => {
      draftTimer.current = null
      pendingDraft.current = null
      void invoke('chat.draft', { sessionId, text: value }).catch(() => undefined)
    }, 100)
  }

  /** 清空输入框（含待同步草稿），命令执行成功后调用，避免残留「/model」。 */
  const clearDraft = () => {
    if (draftTimer.current !== null) {
      window.clearTimeout(draftTimer.current)
      draftTimer.current = null
    }
    pendingDraft.current = null
    setDraft(sessionId, '')
  }

  /**
   * 执行一条命令：结果用 toast 呈现。
   * 命令来自输入框时成功后清空输入框；来自消息操作菜单 / 工具栏按钮时不动当前草稿。
   * 失败保留内容，方便修改重试。
   *
   * 清空必须两端一起做：宿主的 `_draft` 由 `clearDraft` 参数在成功后**无条件**清掉
   * （面板选中的候选和草稿并不相等：草稿 /hel → 执行 /help；草稿 /model → 执行 /model DeepSeek Chat），
   * 否则重新打开会话时快照会带回旧命令、面板还会自动弹回。
   */
  const executeCommand = async (text: string, options?: { clearDraftOnSuccess?: boolean }) => {
    const input = text.trim()
    if (!input) return

    const clearDraftOnSuccess = options?.clearDraftOnSuccess !== false
    setDetached(null)
    setSlashDismissed(true)
    // 执行命令就等于离开输入联想：清掉 @ 状态，否则草稿被清空后
    // mentionEntries 为空的浮层会残留（send() 里已有同样的清理）
    setMention(null)
    setMentionEntries([])
    // 命令来自输入框时，草稿往往就是刚敲进去的命令文本：先掐掉还没发出的草稿同步，
    // 否则它会在命令执行之后才把旧命令写回宿主，把宿主的清空覆盖掉。
    // 消息菜单 / 工具栏按钮触发的命令（clearDraftOnSuccess=false）草稿是普通文本，
    // 不能掐 timer，否则这段草稿永远同步不到宿主。
    // pendingDraft 先保留：执行失败时切换会话仍会把它补发给宿主，方便修改重试。
    if (clearDraftOnSuccess && draftTimer.current !== null) {
      window.clearTimeout(draftTimer.current)
      draftTimer.current = null
    }
    try {
      const result = await invoke<SlashCommandResultDto>('chat.command.execute', {
        sessionId,
        text: input,
        clearDraft: clearDraftOnSuccess,
      })
      if (!result) return
      pushToast({ level: result.ok ? 'success' : 'error', text: result.message })
      if (result.ok && clearDraftOnSuccess) clearDraft()
    } catch (error) {
      pushToast({ level: 'error', text: `命令执行失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  /**
   * 选中一条候选：命令需要参数时先写回输入框（detached 面板则留在面板里选参数），
   * 不需要参数或选中的是参数值时立即执行。
   */
  const pickSlash = (suggestion: SlashSuggestion) => {
    const command = slashView?.command ?? suggestion.command ?? null
    // 面板由按钮 / 消息菜单唤起时不碰输入框里的草稿
    const fromInput = detached === null

    if (suggestion.kind === 'command') {
      const target = suggestion.command ?? null
      if (!target || target.argumentKind === 'none') {
        void executeCommand(commandText(target, suggestion), { clearDraftOnSuccess: fromInput })
        return
      }

      if (detached) {
        setDetached({ command: target })
        return
      }

      syncDraft(draftAfterPick(target, suggestion))
      return
    }

    void executeCommand(commandText(command, suggestion), { clearDraftOnSuccess: fromInput })
  }

  const closeSlash = () => {
    setDetached(null)
    setSlashDismissed(true)
  }

  const send = async () => {
    if (chat.generating) {
      try {
        await invoke('chat.stop', { sessionId })
      } catch (error) {
        pushToast({ level: 'error', text: `停止失败：${error instanceof Error ? error.message : String(error)}` })
      }
      return
    }

    const text = chat.draft.trim()
    if (!text && chat.attachments.length === 0) return

    // 输入框里是一条已知命令时执行命令，而不是把命令文本发给模型
    const parsed = parseSlashInput(text)
    if (parsed && findCommand(commands, parsed.name)) {
      await executeCommand(text)
      return
    }

    setMention(null)
    try {
      await invoke('chat.send', {
        sessionId,
        text,
        attachmentIds: chat.attachments.map((attachment) => attachment.id),
      })
      // 宿主没有 draft 增量 op：发送成功后在页面侧立即清空，避免旧草稿留在输入框
      if (draftTimer.current !== null) {
        window.clearTimeout(draftTimer.current)
        draftTimer.current = null
      }
      pendingDraft.current = null
      setDraft(sessionId, '')
    } catch (error) {
      pushToast({ level: 'error', text: `发送失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const attach = async (mode: 'pick' | 'paste') => {
    try {
      const result = await invoke<{ attachments: AttachmentDto[] }>(
        mode === 'pick' ? 'chat.attach.pick' : 'chat.attach.paste',
        { sessionId },
      )
      applyOps(sessionId, [{ op: 'attachments', attachments: result.attachments }])
    } catch (error) {
      pushToast({ level: 'error', text: `添加附件失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const removeAttachment = async (id: string) => {
    try {
      const result = await invoke<{ attachments: AttachmentDto[] }>('chat.attach.remove', { sessionId, id })
      applyOps(sessionId, [{ op: 'attachments', attachments: result.attachments }])
    } catch (error) {
      pushToast({ level: 'error', text: `移除附件失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const removeLast = () => {
    const last = chat.attachments[chat.attachments.length - 1]
    if (last) void removeAttachment(last.id)
  }

  /** 把选中的候选替换掉 @ 片段：文件补一个空格收尾，目录保留列表继续往下钻。 */
  const pickMention = (entry: MentionEntry) => {
    if (!mention) return
    const inserted = `@${entry.path}${entry.isDirectory ? '/' : ' '}`
    const next = `${chat.draft.slice(0, mention.start)}${inserted}${chat.draft.slice(mention.end)}`
    syncDraft(next)

    const caret = mention.start + inserted.length
    setMention(entry.isDirectory ? { query: `${entry.path}/`, start: mention.start, end: caret } : null)

    const textarea = textareaRef.current
    if (!textarea) return
    window.requestAnimationFrame(() => {
      textarea.focus()
      textarea.setSelectionRange(caret, caret)
    })
  }

  const canSend = chat.draft.trim().length > 0 || chat.attachments.length > 0 || chat.generating
  // 还没发过消息的新会话（含从工作区新建的）可以随意切换工作区；
  // 一旦有对话内容，绑定工作区的会话就不再提供入口
  const canPickWorkspace = chat.messages.length === 0 || chat.workspace == null

  return (
    <div className="composer-wrap" ref={composerRef}>
      {/* 询问工具属于当前会话：内联在输入卡上方，不遮罩页面 */}
      {chat.ask ? <AskPanel sessionId={sessionId} ask={chat.ask} /> : null}

      {canPickWorkspace ? (
        <div className="composer-context">
          <WorkspacePicker chat={chat} />
        </div>
      ) : null}

      <div className={`composer ${chat.generating ? 'is-disabled' : ''}`}>
        {showMention ? (
          <MentionPicker
            listboxId={mentionListboxId}
            entries={mentionEntries}
            activeIndex={mentionIndex}
            loading={mentionLoading}
            onHover={setMentionIndex}
            onPick={pickMention}
          />
        ) : null}
        {slashView ? (
          <SlashCommandMenu
            listboxId={slashListboxId}
            view={slashView}
            activeIndex={slashIndex}
            detached={detached !== null}
            onHover={setSlashIndex}
            onMove={setSlashIndex}
            onPick={pickSlash}
            onClose={closeSlash}
          />
        ) : null}
        {chat.attachments.length > 0 ? (
          <div className="composer-attachments">
            {chat.attachments.map((attachment) =>
              attachment.isImage ? (
                <span key={attachment.id} className="composer-thumb" title={attachment.name}>
                  {attachment.url ? (
                    <img src={attachment.url} alt={attachment.name} />
                  ) : (
                    <span className="composer-thumb-placeholder">
                      <ImageIcon size={18} />
                    </span>
                  )}
                  <button
                    type="button"
                    className="remove"
                    aria-label={`移除附件 ${attachment.name}`}
                    onClick={() => void removeAttachment(attachment.id)}
                  >
                    <X size={11} />
                  </button>
                </span>
              ) : (
                <span key={attachment.id} className="composer-file" title={attachment.path}>
                  <span className="composer-file-icon">
                    <FileText size={16} />
                  </span>
                  <span className="composer-file-text">
                    <span className="composer-file-name">{attachment.name}</span>
                    <span className="composer-file-meta">{attachmentMeta(attachment.name, attachment.size)}</span>
                  </span>
                  <button
                    type="button"
                    className="remove"
                    aria-label={`移除附件 ${attachment.name}`}
                    onClick={() => void removeAttachment(attachment.id)}
                  >
                    <X size={11} />
                  </button>
                </span>
              ),
            )}
          </div>
        ) : null}

        <textarea
          ref={textareaRef}
          className="composer-input"
          value={chat.draft}
          autoFocus={hero}
          placeholder={`和${petName}说点什么…（输入 / 唤起命令）`}
          aria-label="输入消息"
          // combobox 模式：候选面板是 listbox，焦点始终留在输入框，用 aria-activedescendant 标记当前项
          role="combobox"
          aria-autocomplete="list"
          aria-haspopup="listbox"
          aria-expanded={popupOpen}
          aria-controls={popupId}
          aria-activedescendant={popupActiveId}
          onChange={(event) => {
            const value = event.target.value
            setSlashDismissed(false)
            syncDraft(value)
            // 命令输入（含 detached 面板打开）期间不点亮 @ 引用：否则草稿
            // /model @src 会留下 mention 状态，命令面板一关就突然弹出 @ 浮层，
            // 还会白跑一次 chat.mention.search。@ 与命令面板互斥，命令优先。
            if (startsWithSlashTrigger(value) || detached !== null) {
              setMention(null)
            } else {
              setMention(findMentionToken(value, event.target.selectionStart ?? value.length))
            }
          }}
          onCompositionStart={() => {
            composing.current = true
          }}
          onCompositionEnd={() => {
            composing.current = false
          }}
          onBlur={() => {
            setMention(null)
            // 只有输入框触发的面板跟着失焦关闭；按钮 / 消息菜单唤起的面板自己管理生命周期
            if (detached === null) setSlashDismissed(true)
          }}
          onKeyDown={(event) => {
            if (showMention && mentionEntries.length > 0 && !composing.current) {
              if (event.key === 'ArrowDown') {
                event.preventDefault()
                setMentionIndex((index) => (index + 1) % mentionEntries.length)
                return
              }
              if (event.key === 'ArrowUp') {
                event.preventDefault()
                setMentionIndex((index) => (index - 1 + mentionEntries.length) % mentionEntries.length)
                return
              }
              // 带修饰键的组合键不接管：Shift+Enter 换行、Shift+Tab 反向切焦点等保留原生行为
              if (
                (event.key === 'Enter' || event.key === 'Tab') &&
                !event.shiftKey &&
                !event.ctrlKey &&
                !event.altKey &&
                !event.metaKey
              ) {
                event.preventDefault()
                pickMention(mentionEntries[Math.min(mentionIndex, mentionEntries.length - 1)])
                return
              }
            }
            if (event.key === 'Escape' && showMention) {
              event.preventDefault()
              setMention(null)
              return
            }
            // 命令面板可见时（showMention=false）跳过上面的 @ 分支，键盘统一交给斜杠面板处理
            if (slashView && !composing.current) {
              const action = handleSlashKey(event, slashView, slashIndex)
              if (action) {
                event.preventDefault()
                if (action.type === 'move') setSlashIndex(action.index)
                else if (action.type === 'pick') pickSlash(action.suggestion)
                else closeSlash()
                return
              }
            }
            if (event.key === 'Enter' && !event.shiftKey && !composing.current) {
              event.preventDefault()
              void send()
              return
            }
            if (event.key === 'Backspace' && chat.draft.length === 0 && chat.attachments.length > 0) {
              event.preventDefault()
              removeLast()
            }
          }}
          onPaste={(event) => {
            const hasFile =
              Array.from(event.clipboardData.files).length > 0 ||
              Array.from(event.clipboardData.items).some((item) => item.kind === 'file')
            if (!hasFile) return
            event.preventDefault()
            void attach('paste')
          }}
        />

        <div className="composer-bar">
          <button type="button" className="composer-tool" aria-label="添加附件" onClick={() => void attach('pick')}>
            <Paperclip size={15} />
          </button>
          <ApprovalPicker sessionId={sessionId} mode={chat.approvalMode} />
          <button
            type="button"
            className="composer-tool"
            aria-label="命令"
            title="命令"
            aria-haspopup="listbox"
            aria-expanded={detached !== null}
            aria-controls={detached ? slashListboxId : undefined}
            onClick={() => {
              setSlashDismissed(false)
              setDetached((current) => (current ? null : { command: null }))
            }}
          >
            <Terminal size={14} />
            <span className="tool-text">命令</span>
          </button>
          <span className="spacer" />
          <ModelPicker chat={chat} />
          <button
            type="button"
            className={`send-btn ${chat.generating ? 'is-stop' : ''}`}
            aria-label={chat.generating ? '停止生成' : '发送'}
            title={chat.generating ? '停止生成' : '发送'}
            disabled={!canSend}
            onClick={() => void send()}
          >
            {chat.generating ? <Square size={14} fill="currentColor" /> : <Send size={15} />}
          </button>
        </div>
      </div>
    </div>
  )
}

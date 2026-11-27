// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it, vi, beforeEach } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ChatSnapshotDto, SlashCommandDto } from '@/bridge/contracts'
import { useChatStore } from '@/stores/chatStore'
import { useSlashPaletteStore } from '@/stores/slashPaletteStore'
import { Composer } from './Composer'
import { loadSlashCommands } from './slashCommands'

vi.mock('@/bridge/client', () => ({
  invoke: vi.fn(async () => null),
}))

import { invoke } from '@/bridge/client'

const SLASH_CATALOG: SlashCommandDto[] = [
  {
    name: 'model',
    usage: '/model [模型名称/模型ID]',
    summary: '切换当前会话使用的对话模型',
    description: '切换当前会话使用的对话模型，立即生效。',
    argumentKind: 'model',
    argumentLabel: '模型名称/模型ID',
    aliases: ['m'],
  },
  {
    name: 'help',
    usage: '/help',
    summary: '列出全部命令',
    description: '列出全部命令。',
    argumentKind: 'none',
    aliases: ['?'],
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
]

const snapshot: ChatSnapshotDto = {
  sessionId: 's1',
  messages: [],
  draft: '',
  attachments: [],
  generating: false,
  models: [
    { key: 'p/m', provider: 'p', name: 'm', modelId: 'm' },
  ],
  selectedModelKey: 'p/m',
  approvalMode: 'manual',
  workspace: { path: 'D:/w', name: 'w' },
  workspaces: [{ path: 'D:/w', name: 'w' }],
  usage: null,
  ask: null,
}

function Harness() {
  const chat = useChatStore((state) => state.bySession[state.activeId ?? '']) ?? snapshot
  return <Composer chat={chat} hero />
}

// 指定会话渲染，用来验证「后台会话」的操作是否只落在自己身上
function SessionHarness({ id }: { id: string }) {
  const chat = useChatStore((state) => state.bySession[id])
  return chat ? <Composer chat={chat} /> : null
}

describe('Composer', () => {
  beforeEach(() => {
    vi.mocked(invoke).mockClear()
    useChatStore.setState({ bySession: { s1: { ...snapshot, draft: '' } }, activeId: 's1' })
    useSlashPaletteStore.setState({ request: null })
  })

  it('sends on Enter and not on Shift+Enter', async () => {
    render(<Harness />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

    fireEvent.change(input, { target: { value: 'hello' } })
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter', shiftKey: true })
    expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.anything())

    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter', shiftKey: false })
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.send', {
        sessionId: 's1',
        text: 'hello',
        attachmentIds: [],
      })
    })
  })

  it('后台会话的发送只作用于自己的 sessionId', async () => {
    const background: ChatSnapshotDto = { ...snapshot, sessionId: 's2' }
    useChatStore.setState({
      bySession: { s1: { ...snapshot, draft: '' }, s2: background },
      activeId: 's2',
    })
    render(<SessionHarness id="s1" />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

    fireEvent.change(input, { target: { value: '只给会话1' } })
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.send', {
        sessionId: 's1',
        text: '只给会话1',
        attachmentIds: [],
      })
    })
    expect(useChatStore.getState().bySession.s2.draft).toBe('')
  })

  it('切换会话卸载时把防抖中的草稿补发给宿主', async () => {
    useChatStore.setState({
      bySession: { s1: { ...snapshot, draft: '' } },
      activeId: 's1',
    })
    const { unmount } = render(<SessionHarness id="s1" />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

    fireEvent.change(input, { target: { value: '草稿A' } })
    unmount()

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.draft', { sessionId: 's1', text: '草稿A' })
    })
  })

  it('does not send while IME composition is active', async () => {
    render(<Harness />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

    fireEvent.change(input, { target: { value: 'ni hao' } })
    fireEvent.compositionStart(input)
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
    expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.anything())

    fireEvent.compositionEnd(input)
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.send', expect.objectContaining({ text: 'ni hao' }))
    })
  })

  it('switches the send button to stop while generating', () => {
    useChatStore.setState({
      bySession: { s1: { ...snapshot, generating: true, draft: 'draft' } },
      activeId: 's1',
    })
    render(<Harness />)
    expect(screen.getByLabelText('停止生成')).toBeInTheDocument()
  })

  it('已绑定工作区的会话隐藏工作区切换入口', () => {
    useChatStore.setState({
      bySession: {
        s1: {
          ...snapshot,
          messages: [{ id: 'm1', kind: 'user', text: 'hi', time: '' }],
        },
      },
      activeId: 's1',
    })
    render(<Harness />)
    expect(screen.queryByLabelText('工作区')).toBeNull()
    expect(document.querySelector('.composer-context')).toBeNull()
  })

  it('未发送消息的新会话即使已选工作区仍可切换', () => {
    render(<Harness />)
    const chip = screen.getByLabelText('工作区')
    expect(chip.textContent).toContain(snapshot.workspace?.name ?? '')
  })

  it('未绑定工作区的会话仍可手动切换工作区', () => {
    useChatStore.setState({
      bySession: { s1: { ...snapshot, workspace: null, workspaces: [] } },
      activeId: 's1',
    })
    render(<Harness />)
    const chip = screen.getByLabelText('工作区')
    expect(chip.textContent).toContain('选择工作区')
  })

  it('询问工具内联在当前会话的输入卡上方，并带上 sessionId', async () => {
    useChatStore.setState({
      bySession: {
        s1: {
          ...snapshot,
          ask: {
            requestId: 'r1',
            questions: [{ text: '选哪个？', options: [{ text: '选项A' }] }],
          },
        },
      },
      activeId: 's1',
    })
    render(<Harness />)

    expect(document.querySelector('.composer-wrap .ask-card')).not.toBeNull()
    expect(document.querySelector('.dialog-overlay')).toBeNull()

    fireEvent.click(screen.getByText('选项A'))
    // 点选项只做选中，不能把选项文字灌进自由输入框
    expect((screen.getByLabelText('自由输入答案') as HTMLTextAreaElement).value).toBe('')
    fireEvent.click(screen.getByRole('button', { name: '提交' }))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith(
        'chat.ask',
        expect.objectContaining({
          sessionId: 's1',
          requestId: 'r1',
          action: 'submit',
          payload: { answers: [{ question: '选哪个？', answer: '选项A' }] },
        }),
      )
    })
  })

  it('把审批放在附件右侧、模型放在发送左侧，且都在输入框内', () => {
    render(<Harness />)
    const bar = document.querySelector('.composer-bar')
    expect(bar).not.toBeNull()
    const labels = [...bar!.children].map(
      (node) => node.getAttribute('aria-label') ?? node.className,
    )
    expect(labels.slice(0, 2)).toEqual(['添加附件', '审批模式'])
    expect(labels.slice(-2)).toEqual(['选择对话模型', '发送'])
    expect(document.querySelector('.composer-context [aria-label="审批模式"]')).toBeNull()
  })

  describe('命令', () => {
    beforeEach(async () => {
      vi.mocked(invoke).mockImplementation(async (method: string) => {
        if (method === 'chat.command.list') return { commands: SLASH_CATALOG }
        if (method === 'chat.command.execute') {
          return { ok: true, command: 'model', message: '已切换模型：m' }
        }
        return null
      })
      // 命令目录在进程内缓存：先预热，避免每条用例都等一次 RPC
      await loadSlashCommands()
    })

    it('输入斜杠立即唤起命令面板，并给出用法预览', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/' } })
      expect(await screen.findByRole('listbox', { name: '命令面板' })).toBeInTheDocument()
      expect(screen.getByRole('option', { name: /\/help/ })).toBeInTheDocument()

      fireEvent.change(input, { target: { value: '/h' } })
      await waitFor(() => {
        expect(document.querySelector('.slash-usage')?.textContent).toContain('/help')
      })
      expect(document.querySelector('.slash-description')?.textContent).toContain('列出全部命令')
    })

    it('输入「/model 」后列出模型，回车选中即执行切换', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model ' } })
      const option = await screen.findByRole('option', { name: /^m\b/ })
      expect(option).toBeInTheDocument()
      // 参数占位在预览区（listbox 之前），不在候选列表里
      expect(document.querySelector('.slash-preview-arg')?.textContent).toContain('模型名称/模型ID')

      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('chat.command.execute', {
          sessionId: 's1',
          text: '/model m',
          clearDraft: true,
        }, 0)
      })
      // 命令不进模型上下文：不会走 chat.send
      expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.anything())
    })

    it('无参数命令选中后直接执行，并要求宿主清空草稿', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      // 草稿 /hel 与执行文本 /help 不相等：仍必须要求宿主清空草稿，
      // 否则重新打开会话时快照会带回旧命令、面板自动弹回。
      fireEvent.change(input, { target: { value: '/hel' } })
      await screen.findByRole('option', { name: /\/help/ })
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })

      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('chat.command.execute', {
          sessionId: 's1',
          text: '/help',
          clearDraft: true,
        }, 0)
      })
      // 页面侧也立即清空，避免旧命令残留在输入框里把面板又弹回来
      expect(useChatStore.getState().bySession.s1.draft).toBe('')
    })

    it('命令一提交就清空输入框，不等执行结果；失败再把命令填回来', async () => {
      let finish: (value: unknown) => void = () => undefined
      vi.mocked(invoke).mockImplementation(async (method: string) => {
        if (method === 'chat.command.list') return { commands: SLASH_CATALOG }
        if (method === 'chat.command.execute') {
          return new Promise((resolve) => {
            finish = resolve
          })
        }
        return null
      })

      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement
      fireEvent.change(input, { target: { value: '/help' } })
      await screen.findByRole('option', { name: /\/help/ })
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })

      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('chat.command.execute', expect.anything(), 0)
      })
      // 命令还没返回，输入框已经空了：长命令（/compact）不该把输入框占住
      expect(useChatStore.getState().bySession.s1.draft).toBe('')
      expect(input.value).toBe('')

      // 失败：宿主没清草稿，页面把命令文本填回来，方便改一下重试
      finish({ ok: false, command: 'help', message: '命令出错了' })
      await waitFor(() => {
        expect(useChatStore.getState().bySession.s1.draft).toBe('/help')
      })
    })

    it('Esc 收起面板，未知斜杠内容仍按普通消息发送', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model' } })
      await screen.findByRole('listbox', { name: '命令面板' })
      fireEvent.keyDown(input, { key: 'Escape', code: 'Escape' })
      expect(screen.queryByRole('listbox', { name: '命令面板' })).toBeNull()

      fireEvent.change(input, { target: { value: '/api/v1 是什么' } })
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith(
          'chat.send',
          expect.objectContaining({ sessionId: 's1', text: '/api/v1 是什么' }),
        )
      })
    })

    it('普通文本消息不弹面板、照常发送', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '你好' } })
      expect(screen.queryByRole('listbox', { name: '命令面板' })).toBeNull()

      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith(
          'chat.send',
          expect.objectContaining({ sessionId: 's1', text: '你好' }),
        )
      })
    })

    it('面板打开时 Shift+Enter 不确认候选、不 preventDefault（保留换行）', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model ' } })
      await screen.findByRole('listbox', { name: '命令面板' })

      // fireEvent 返回 false 表示有处理器调用了 preventDefault；Shift+Enter 必须放行
      const notPrevented = fireEvent.keyDown(input, {
        key: 'Enter',
        code: 'Enter',
        shiftKey: true,
      })
      expect(notPrevented).toBe(true)
      expect(invoke).not.toHaveBeenCalledWith('chat.command.execute', expect.anything(), expect.anything())
      expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.anything())
    })

    it('面板打开时 Shift+Tab 同样不确认候选（保留反向切焦点）', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model ' } })
      await screen.findByRole('listbox', { name: '命令面板' })

      const notPrevented = fireEvent.keyDown(input, {
        key: 'Tab',
        code: 'Tab',
        shiftKey: true,
      })
      expect(notPrevented).toBe(true)
      expect(invoke).not.toHaveBeenCalledWith('chat.command.execute', expect.anything(), expect.anything())
    })

    it('面板打开时无修饰键的 Enter 仍然确认候选', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model ' } })
      await screen.findByRole('option', { name: /^m\b/ })
      const prevented = fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
      expect(prevented).toBe(false)
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('chat.command.execute', expect.objectContaining({ text: '/model m' }), 0)
      })
    })

    it('消息操作菜单可唤起面板，不改动输入框草稿', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement
      fireEvent.change(input, { target: { value: '写了一半的草稿' } })

      act(() => useSlashPaletteStore.getState().requestPalette('s1'))
      await screen.findByRole('listbox', { name: '命令面板' })
      expect(document.querySelector('.slash-popover')?.getAttribute('data-detached')).toBe('true')

      fireEvent.mouseDown(await screen.findByRole('option', { name: /\/model/ }))
      fireEvent.mouseDown(await screen.findByRole('option', { name: /^m\b/ }))

      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('chat.command.execute', {
          sessionId: 's1',
          text: '/model m',
          clearDraft: false,
        }, 0)
      })
      expect(useChatStore.getState().bySession.s1.draft).toBe('写了一半的草稿')
    })

    it('参数阶段回车只重新确认当前模型，不会静默切到列表第一项', async () => {
      useChatStore.setState({
        bySession: {
          s1: {
            ...snapshot,
            draft: '',
            models: [
              { key: 'p/m1', provider: 'p', name: 'm1', modelId: 'm1' },
              { key: 'p/m2', provider: 'p', name: 'm2', modelId: 'm2' },
            ],
            selectedModelKey: 'p/m2',
          },
        },
        activeId: 's1',
      })

      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model ' } })
      await screen.findByRole('option', { name: /^m2/ })
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })

      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith(
          'chat.command.execute',
          expect.objectContaining({ text: '/model m2' }),
          0,
        )
      })
      // 关键：不能落到列表第一项 m1（那会把当前模型悄悄换掉）
      expect(invoke).not.toHaveBeenCalledWith(
        'chat.command.execute',
        expect.objectContaining({ text: '/model m1' }),
        0,
      )
    })

    it('审批面板回车不会把 full 改回 manual', async () => {
      useChatStore.setState({
        bySession: { s1: { ...snapshot, draft: '', approvalMode: 'full' } },
        activeId: 's1',
      })

      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/approval ' } })
      await screen.findByRole('option', { name: /^full/ })
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })

      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith(
          'chat.command.execute',
          expect.objectContaining({ text: '/approval full' }),
          0,
        )
      })
      expect(invoke).not.toHaveBeenCalledWith(
        'chat.command.execute',
        expect.objectContaining({ text: '/approval manual' }),
        0,
      )
    })

    it('工具栏按钮无需输入即可唤起面板', async () => {
      render(<Harness />)

      fireEvent.click(screen.getByLabelText('命令'))
      expect(await screen.findByRole('listbox', { name: '命令面板' })).toBeInTheDocument()
      fireEvent.click(screen.getByLabelText('命令'))
      expect(screen.queryByRole('listbox', { name: '命令面板' })).toBeNull()
    })

    it('输入框是 combobox，并用 aria-activedescendant 标记当前候选', async () => {
      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      expect(input.getAttribute('role')).toBe('combobox')
      expect(input.getAttribute('aria-expanded')).toBe('false')
      expect(input.getAttribute('aria-activedescendant')).toBeNull()

      fireEvent.change(input, { target: { value: '/' } })
      const listbox = await screen.findByRole('listbox', { name: '命令面板' })

      // 展开状态、控制的列表、当前候选三者都挂在输入框上
      await waitFor(() => expect(input.getAttribute('aria-expanded')).toBe('true'))
      expect(input.getAttribute('aria-controls')).toBe(listbox.id)

      const firstActive = input.getAttribute('aria-activedescendant')
      expect(firstActive).toBeTruthy()
      const firstOption = document.getElementById(firstActive!)
      expect(firstOption?.getAttribute('role')).toBe('option')
      expect(firstOption?.getAttribute('aria-selected')).toBe('true')

      // 候选不可聚焦（不是 button）：焦点始终留在 combobox 里
      expect(listbox.querySelectorAll('button')).toHaveLength(0)

      // 方向键移动高亮时 aria-activedescendant 跟着变（目录里 /model、/help 两项）
      fireEvent.keyDown(input, { key: 'ArrowDown', code: 'ArrowDown' })
      await waitFor(() => {
        const moved = input.getAttribute('aria-activedescendant')
        expect(moved).toBeTruthy()
        expect(moved).not.toBe(firstActive)
        expect(document.getElementById(moved!)?.getAttribute('aria-selected')).toBe('true')
      })
    })

    it('detached 面板接管焦点，关闭后归还给唤起它的按钮', async () => {
      render(<Harness />)
      const trigger = screen.getByLabelText('命令') as HTMLButtonElement
      trigger.focus()
      expect(document.activeElement).toBe(trigger)

      fireEvent.click(trigger)
      const listbox = await screen.findByRole('listbox', { name: '命令面板' })
      // 打开后焦点进面板，方向键/回车才可用
      await waitFor(() => expect(document.activeElement).toBe(listbox))

      fireEvent.keyDown(listbox, { key: 'Escape', code: 'Escape' })
      expect(screen.queryByRole('listbox', { name: '命令面板' })).toBeNull()
      // 关闭后把焦点还给唤起者，而不是丢到 body
      await waitFor(() => expect(document.activeElement).toBe(trigger))
    })

    it('斜杠草稿带 @ 时只显示命令面板，@ 面板不重叠、键盘作用于命令面板', async () => {
      vi.mocked(invoke).mockImplementation(async (method: string) => {
        if (method === 'chat.command.list') return { commands: SLASH_CATALOG }
        if (method === 'chat.mention.search') {
          return { entries: [{ name: 'src', path: 'src', isDirectory: true }] }
        }
        if (method === 'chat.command.execute') {
          return { ok: true, command: 'model', message: '已切换模型：m' }
        }
        return null
      })

      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      // /model @src 同时满足两种触发条件
      fireEvent.change(input, { target: { value: '/model @src' } })
      await screen.findByRole('listbox', { name: '命令面板' })

      // 命令面板可见，@ 面板绝不渲染（两个面板几何完全重叠）
      expect(document.querySelector('.mention-popover')).toBeNull()
      // 也不该为被挡住的 @ 面板白跑一次搜索
      expect(invoke).not.toHaveBeenCalledWith('chat.mention.search', expect.anything())

      // 键盘作用于当前可见的命令面板，而不是被挡住的 @ 面板：
      // 若 @ 面板抢了键盘，Enter 会走 pickMention 把 @src 补成 @src/ 而不执行命令；
      // 现在候选为空，Enter 落到 send() 的斜杠分支 → 执行 /model @src 这条命令文本。
      // 出现 chat.command.execute 恰好证明命令面板拿到了键盘所有权。
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith(
          'chat.command.execute',
          expect.objectContaining({ text: '/model @src' }),
          0,
        )
      })
      // @ 候选没有被 pickMention 插入：草稿没有被改成 @src/
      expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.objectContaining({ text: expect.stringContaining('@src/') }))
    })

    it('命令面板关闭后不会残留或弹出 @ 面板', async () => {
      vi.mocked(invoke).mockImplementation(async (method: string) => {
        if (method === 'chat.command.list') return { commands: SLASH_CATALOG }
        if (method === 'chat.mention.search') {
          return { entries: [{ name: 'src', path: 'src', isDirectory: true }] }
        }
        return null
      })

      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      fireEvent.change(input, { target: { value: '/model @src' } })
      await screen.findByRole('listbox', { name: '命令面板' })

      // Esc 关掉命令面板（草稿仍是 /model @src，@ 状态当初没被点亮）
      fireEvent.keyDown(input, { key: 'Escape', code: 'Escape' })
      expect(screen.queryByRole('listbox', { name: '命令面板' })).toBeNull()
      expect(document.querySelector('.mention-popover')).toBeNull()
    })

    it('命令执行成功后清空 @ 引用浮层状态', async () => {
      vi.mocked(invoke).mockImplementation(async (method: string) => {
        if (method === 'chat.command.list') return { commands: SLASH_CATALOG }
        if (method === 'chat.mention.search') {
          return { entries: [{ name: 'src', path: 'src', isDirectory: true }] }
        }
        if (method === 'chat.command.execute') {
          return { ok: true, command: 'help', message: '可用命令：' }
        }
        return null
      })

      render(<Harness />)
      const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

      // 先让 @ 面板处于「有候选」状态
      fireEvent.change(input, { target: { value: '@src' } })
      expect(await screen.findByRole('listbox', { name: '引用工作区文件' })).toBeInTheDocument()

      // 再切到命令并执行
      fireEvent.change(input, { target: { value: '/help' } })
      await screen.findByRole('option', { name: /\/help/ })
      fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })

      await waitFor(() => {
        expect(invoke).toHaveBeenCalledWith('chat.command.execute', expect.objectContaining({ text: '/help' }), 0)
      })
      // 命令执行后 @ 浮层已清掉，不会残留
      expect(document.querySelector('.mention-popover')).toBeNull()
    })
  })
})
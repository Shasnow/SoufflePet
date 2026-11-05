// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import type { ChatMessageDto, ChatSnapshotDto } from './contracts'
import { parseIncoming, makeRequest } from './protocol'
import { reduceChatOps } from '@/stores/chatStore'

function message(id: string, text = id): ChatMessageDto {
  return { id, kind: 'assistant', text, time: new Date(0).toISOString() }
}

function snapshot(): ChatSnapshotDto {
  return {
    sessionId: 's1',
    messages: [message('a')],
    draft: '',
    attachments: [],
    generating: false,
    models: [],
    selectedModelKey: null,
    approvalMode: 'manual',
    workspace: null,
    workspaces: [],
    usage: null,
    ask: null,
  }
}

describe('chat op reducer', () => {
  it('adds, inserts, removes and patches messages by id', () => {
    let state = reduceChatOps(snapshot(), [{ op: 'msg:add', msg: message('b') }])
    expect(state.messages.map((m) => m.id)).toEqual(['a', 'b'])

    state = reduceChatOps(state, [{ op: 'msg:insert', index: 1, msg: message('x') }])
    expect(state.messages.map((m) => m.id)).toEqual(['a', 'x', 'b'])

    state = reduceChatOps(state, [{ op: 'msg:remove', id: 'x' }])
    expect(state.messages.map((m) => m.id)).toEqual(['a', 'b'])

    state = reduceChatOps(state, [
      { op: 'msg:patch', id: 'b', patch: { expanded: true, text: 'patched' } },
    ])
    expect(state.messages[1]).toMatchObject({ id: 'b', expanded: true, text: 'patched' })
  })

  it('appends and replaces text', () => {
    let state = reduceChatOps(snapshot(), [
      { op: 'msg:text', id: 'a', mode: 'append', text: ' + more' },
    ])
    expect(state.messages[0].text).toBe('a + more')

    state = reduceChatOps(state, [{ op: 'msg:text', id: 'a', mode: 'set', text: 'reset' }])
    expect(state.messages[0].text).toBe('reset')
  })

  it('updates round state, attachments, approval, workspace, models, ask and usage', () => {
    const state = reduceChatOps(snapshot(), [
      { op: 'turn', generating: true },
      { op: 'approval', mode: 'full' },
      { op: 'attachments', attachments: [{ id: 'f', name: 'a.txt', path: 'C:/a.txt', isImage: false }] },
      { op: 'workspace', workspace: { path: 'C:/w', name: 'w' }, workspaces: [] },
      {
        op: 'models',
        models: [{ key: 'p/m', provider: 'p', name: 'm', modelId: 'm' }],
        selectedModelKey: 'p/m',
      },
      { op: 'ask', ask: { requestId: 'r', questions: [] } },
      { op: 'usage', usage: { inputTokens: 1, outputTokens: 2, cachedInputTokens: 0 } },
    ])

    expect(state.generating).toBe(true)
    expect(state.approvalMode).toBe('full')
    expect(state.attachments).toHaveLength(1)
    expect(state.workspace?.name).toBe('w')
    expect(state.selectedModelKey).toBe('p/m')
    expect(state.ask?.requestId).toBe('r')
    expect(state.usage?.outputTokens).toBe(2)
  })

  it('ignores duplicate message ids', () => {
    const state = reduceChatOps(snapshot(), [
      { op: 'msg:add', msg: message('a', 'duplicate') },
    ])
    expect(state.messages).toHaveLength(1)
    expect(state.messages[0].text).toBe('a')
  })
})

describe('protocol', () => {
  it('builds request envelopes', () => {
    expect(makeRequest(7, 'chat.send', { text: 'x' })).toEqual({
      v: 1,
      id: 7,
      type: 'req',
      method: 'chat.send',
      params: { text: 'x' },
    })
    expect(makeRequest(8, 'hello')).toEqual({
      v: 1,
      id: 8,
      type: 'req',
      method: 'hello',
    })
  })

  it('parses host envelopes', () => {
    expect(parseIncoming({ v: 1, id: 1, ok: true, result: null })).toMatchObject({ id: 1, ok: true })
    expect(
      parseIncoming({ v: 1, id: 2, ok: false, error: { code: 'io_error', message: 'x' } }),
    ).toMatchObject({ id: 2, ok: false })
    expect(
      parseIncoming({ v: 1, type: 'event', name: 'chat.ops', payload: {} }),
    ).toMatchObject({ type: 'event', name: 'chat.ops' })
    expect(
      parseIncoming({ v: 1, type: 'event', name: 'toast', rev: 3, payload: 'saved' }),
    ).toMatchObject({ type: 'event', name: 'toast', rev: 3, payload: 'saved' })
    expect(
      parseIncoming({ v: 1, type: 'event', name: 'list', rev: 4, payload: ['a', 'b'] }),
    ).toMatchObject({ type: 'event', name: 'list', rev: 4, payload: ['a', 'b'] })
    expect(
      parseIncoming({ v: 1, type: 'snapshot', rev: 1, data: { protocolVersion: 1 } }),
    ).toMatchObject({ type: 'snapshot', rev: 1 })
    expect(parseIncoming({ v: 2, id: 1, ok: true })).toBeNull()
    expect(parseIncoming(null)).toBeNull()
  })
})
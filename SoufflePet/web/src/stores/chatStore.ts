// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'
import type {
  ChatMessageDto,
  ChatOp,
  ChatSnapshotDto,
} from '@/bridge/contracts'

/** 纯函数：把一批 op 应用到单个会话快照（可单测，不依赖 React/store）。 */
export function reduceChatOps(session: ChatSnapshotDto, ops: ChatOp[]): ChatSnapshotDto {
  let next = session

  for (const op of ops) {
    switch (op.op) {
      case 'msg:add': {
        if (next.messages.some((message) => message.id === op.msg.id)) break
        next = { ...next, messages: [...next.messages, op.msg] }
        break
      }
      case 'msg:insert': {
        if (next.messages.some((message) => message.id === op.msg.id)) break
        const index = Math.max(0, Math.min(next.messages.length, op.index))
        const messages = [...next.messages]
        messages.splice(index, 0, op.msg)
        next = { ...next, messages }
        break
      }
      case 'msg:remove': {
        next = { ...next, messages: next.messages.filter((message) => message.id !== op.id) }
        break
      }
      case 'msg:text': {
        next = {
          ...next,
          messages: next.messages.map((message) =>
            message.id === op.id
              ? {
                  ...message,
                  text: op.mode === 'append' ? message.text + op.text : op.text,
                }
              : message,
          ),
        }
        break
      }
      case 'msg:patch': {
        next = {
          ...next,
          messages: next.messages.map((message) =>
            message.id === op.id ? { ...message, ...op.patch, id: message.id } : message,
          ),
        }
        break
      }
      case 'turn': {
        next = { ...next, generating: op.generating }
        break
      }
      case 'attachments': {
        next = { ...next, attachments: op.attachments }
        break
      }
      case 'approval': {
        next = { ...next, approvalMode: op.mode }
        break
      }
      case 'workspace': {
        next = { ...next, workspace: op.workspace, workspaces: op.workspaces }
        break
      }
      case 'models': {
        next = { ...next, models: op.models, selectedModelKey: op.selectedModelKey }
        break
      }
      case 'ask': {
        next = { ...next, ask: op.ask }
        break
      }
      case 'usage': {
        next = { ...next, usage: op.usage }
        break
      }
      default: {
        const exhaustive: never = op
        void exhaustive
        break
      }
    }
  }

  return next
}

interface ChatState {
  bySession: Record<string, ChatSnapshotDto>
  activeId: string | null
  setActive: (id: string | null) => void
  setSnapshot: (session: ChatSnapshotDto | null, activeId: string | null) => void
  upsertSnapshot: (session: ChatSnapshotDto) => void
  applyOps: (sessionId: string, ops: ChatOp[]) => void
  setDraft: (text: string) => void
  setAsk: (ask: ChatSnapshotDto['ask']) => void
  removeSession: (id: string) => void
}

export const useChatStore = create<ChatState>()((set) => ({
  bySession: {},
  activeId: null,
  setActive: (activeId) => set({ activeId }),
  setSnapshot: (session, activeId) =>
    set((state) => ({
      activeId,
      bySession: session
        ? { ...state.bySession, [session.sessionId]: session }
        : state.bySession,
    })),
  upsertSnapshot: (session) =>
    set((state) => ({ bySession: { ...state.bySession, [session.sessionId]: session } })),
  applyOps: (sessionId, ops) =>
    set((state) => {
      const current = state.bySession[sessionId]
      if (!current) return state
      return { bySession: { ...state.bySession, [sessionId]: reduceChatOps(current, ops) } }
    }),
  setDraft: (text) =>
    set((state) => {
      if (!state.activeId) return state
      const current = state.bySession[state.activeId]
      if (!current) return state
      return {
        bySession: {
          ...state.bySession,
          [state.activeId]: { ...current, draft: text },
        },
      }
    }),
  setAsk: (ask) =>
    set((state) => {
      if (!state.activeId) return state
      const current = state.bySession[state.activeId]
      if (!current) return state
      return {
        bySession: {
          ...state.bySession,
          [state.activeId]: { ...current, ask },
        },
      }
    }),
  removeSession: (id) =>
    set((state) => {
      const bySession = { ...state.bySession }
      delete bySession[id]
      return { bySession }
    }),
}))

export function useActiveChat(): ChatSnapshotDto | null {
  return useChatStore((state) => (state.activeId ? state.bySession[state.activeId] ?? null : null))
}

export function messageById(messages: ChatMessageDto[], id: string): ChatMessageDto | undefined {
  return messages.find((message) => message.id === id)
}
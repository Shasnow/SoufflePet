// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'
import type { SessionSummaryDto } from '@/bridge/contracts'

interface SessionState {
  sessions: SessionSummaryDto[]
  activeId: string | null
  query: string
  setSnapshot: (sessions: SessionSummaryDto[], activeId: string | null) => void
  setUpdated: (sessions: SessionSummaryDto[], activeId: string | null) => void
  setActive: (id: string | null) => void
  setQuery: (query: string) => void
  renameLocal: (id: string, title: string) => void
  removeLocal: (id: string, activeId: string | null) => void
}

export const useSessionStore = create<SessionState>()((set) => ({
  sessions: [],
  activeId: null,
  query: '',
  setSnapshot: (sessions, activeId) => set({ sessions, activeId }),
  setUpdated: (sessions, activeId) => set({ sessions, activeId }),
  setActive: (activeId) => set({ activeId }),
  setQuery: (query) => set({ query }),
  renameLocal: (id, title) =>
    set((state) => ({
      sessions: state.sessions.map((session) => (session.id === id ? { ...session, title } : session)),
    })),
  removeLocal: (id, activeId) =>
    set((state) => ({
      sessions: state.sessions.filter((session) => session.id !== id),
      activeId,
    })),
}))

export function filterSessions(sessions: SessionSummaryDto[], query: string): SessionSummaryDto[] {
  const needle = query.trim().toLowerCase()
  if (!needle) return sessions
  return sessions.filter((session) =>
    [session.title, session.lastUserText, session.workspacePath, session.workspaceName]
      .filter(Boolean)
      .some((value) => String(value).toLowerCase().includes(needle)),
  )
}

export function sortSessions(sessions: SessionSummaryDto[]): SessionSummaryDto[] {
  return [...sessions].sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt))
}

export function groupSessions(sessions: SessionSummaryDto[]): {
  key: string
  name: string
  sessions: SessionSummaryDto[]
}[] {
  const groups = new Map<string, { key: string; name: string; sessions: SessionSummaryDto[] }>()
  for (const session of sortSessions(sessions)) {
    const key = session.workspacePath ?? ''
    const name = session.workspaceName ?? (key ? key.split(/[\\/]/).filter(Boolean).pop() ?? key : '默认工作区')
    if (!groups.has(key)) groups.set(key, { key, name, sessions: [] })
    groups.get(key)!.sessions.push(session)
  }
  return [...groups.values()]
}
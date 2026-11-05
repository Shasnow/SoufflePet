// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { filterSessions, groupSessions, sortSessions } from './sessionStore'
import type { SessionSummaryDto } from '@/bridge/contracts'

function session(id: string, title: string, updatedAt: string, workspacePath?: string): SessionSummaryDto {
  return {
    id,
    title,
    updatedAt,
    createdAt: updatedAt,
    messageCount: 1,
    workspacePath,
    workspaceName: workspacePath?.split('/').pop(),
  }
}

describe('session store helpers', () => {
  it('filters by title, last message and workspace', () => {
    const sessions = [
      { ...session('a', '整理表格', '2026-11-05T10:00:00Z'), lastUserText: '缓存命中率' },
      { ...session('b', 'Live2D 排查', '2026-11-04T10:00:00Z', 'D:/notes') },
    ]
    expect(filterSessions(sessions, '').map((s) => s.id)).toEqual(['a', 'b'])
    expect(filterSessions(sessions, '缓存').map((s) => s.id)).toEqual(['a'])
    expect(filterSessions(sessions, 'notes').map((s) => s.id)).toEqual(['b'])
    expect(filterSessions(sessions, '不存在')).toEqual([])
  })

  it('sorts newest first', () => {
    const sessions = [
      session('old', 'old', '2026-11-01T10:00:00Z'),
      session('new', 'new', '2026-11-05T10:00:00Z'),
      session('mid', 'mid', '2026-11-03T10:00:00Z'),
    ]
    expect(sortSessions(sessions).map((s) => s.id)).toEqual(['new', 'mid', 'old'])
  })

  it('groups by workspace', () => {
    const groups = groupSessions([
      session('a', 'a', '2026-11-05T10:00:00Z', 'D:/work'),
      session('b', 'b', '2026-11-04T10:00:00Z', 'D:/work'),
      session('c', 'c', '2026-11-03T10:00:00Z', 'D:/notes'),
      session('d', 'd', '2026-11-02T10:00:00Z'),
    ])
    expect(groups.map((group) => group.key)).toEqual(['D:/work', 'D:/notes', ''])
    expect(groups[0].sessions.map((s) => s.id)).toEqual(['a', 'b'])
    expect(groups[0].name).toBe('work')
  })
})
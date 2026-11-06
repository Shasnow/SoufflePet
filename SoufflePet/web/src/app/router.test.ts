// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { parseHash, routeHash } from './router'

describe('hash router', () => {
  it('parses chat and settings routes', () => {
    expect(parseHash('#/chat/s1')).toEqual({ kind: 'chat', sessionId: 's1' })
    expect(parseHash('#/chat')).toEqual({ kind: 'chat' })
    expect(parseHash('')).toEqual({ kind: 'chat' })
    expect(parseHash('#/settings/llm')).toEqual({ kind: 'settings', panel: 'llm' })
    expect(parseHash('#/settings/appearance')).toEqual({ kind: 'settings', panel: 'appearance' })
    expect(parseHash('#/settings/nope')).toEqual({ kind: 'unknown', hash: '#/settings/nope' })
  })

  it('parses the extension and automation pages', () => {
    expect(parseHash('#/extensions/plugins')).toEqual({ kind: 'extensions', tab: 'plugins' })
    expect(parseHash('#/extensions/skills')).toEqual({ kind: 'extensions', tab: 'skills' })
    expect(parseHash('#/extensions/mcp')).toEqual({ kind: 'extensions', tab: 'mcp' })
    expect(parseHash('#/extensions/nope')).toEqual({ kind: 'unknown', hash: '#/extensions/nope' })
    expect(parseHash('#/tasks')).toEqual({ kind: 'tasks', tab: 'tasks' })
    expect(parseHash('#/tasks/history')).toEqual({ kind: 'tasks', tab: 'history' })
    expect(parseHash('#/tasks/nope')).toEqual({ kind: 'unknown', hash: '#/tasks/nope' })
  })

  it('round-trips route hashes', () => {
    expect(routeHash({ kind: 'chat', sessionId: 'a b' })).toBe('#/chat/a%20b')
    expect(routeHash({ kind: 'chat' })).toBe('#/chat')
    expect(routeHash({ kind: 'settings', panel: 'appearance' })).toBe('#/settings/appearance')
    expect(routeHash({ kind: 'extensions', tab: 'mcp' })).toBe('#/extensions/mcp')
    expect(routeHash({ kind: 'tasks', tab: 'tasks' })).toBe('#/tasks')
    expect(routeHash({ kind: 'tasks', tab: 'history' })).toBe('#/tasks/history')
  })
})

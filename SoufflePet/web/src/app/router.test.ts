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
    expect(parseHash('#/settings/nope')).toEqual({ kind: 'unknown', hash: '#/settings/nope' })
  })

  it('round-trips route hashes', () => {
    expect(routeHash({ kind: 'chat', sessionId: 'a b' })).toBe('#/chat/a%20b')
    expect(routeHash({ kind: 'chat' })).toBe('#/chat')
    expect(routeHash({ kind: 'settings', panel: 'appearance' })).toBe('#/settings/appearance')
  })
})
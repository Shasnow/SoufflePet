// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { formatClock, formatDuration, formatPercent, formatTokens, previewText } from './format'

describe('format helpers', () => {
  it('formats token counts', () => {
    expect(formatTokens(999)).toBe('999 tok')
    expect(formatTokens(12_500)).toBe('12.5K tok')
    expect(formatTokens(2_500_000)).toBe('2.50M tok')
    expect(formatPercent(0.986)).toBe('98.6%')
  })

  it('formats durations', () => {
    expect(formatDuration(42)).toBe('42 秒')
    expect(formatDuration(90)).toBe('2 分钟')
    expect(formatDuration(3720)).toBe('1 小时 2 分钟')
  })

  it('formats clock and previews without newlines', () => {
    const iso = new Date(2026, 10, 5, 9, 7, 0).toISOString()
    expect(formatClock(iso)).toMatch(/^09:07$/)
    expect(previewText('  hello\nworld  ')).toBe('hello world')
    expect(previewText('x'.repeat(40))).toHaveLength(31)
  })
})
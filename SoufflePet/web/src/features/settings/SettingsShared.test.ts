// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { formatKeyValueLines, parseKeyValueLines, splitLines } from './SettingsShared'

describe('settings helpers', () => {
  it('parses KEY=VALUE lines', () => {
    expect(parseKeyValueLines('A=1\nB=two=three\n\n')).toEqual({ A: '1', B: 'two=three' })
  })

  it('rejects invalid lines', () => {
    expect(parseKeyValueLines('A=1\ninvalid')).toBeNull()
    expect(parseKeyValueLines('=1')).toBeNull()
  })

  it('formats maps for the textarea', () => {
    expect(formatKeyValueLines({ A: '1', B: '2' })).toBe('A=1\nB=2')
    expect(formatKeyValueLines(undefined)).toBe('')
  })

  it('splits MCP arguments without empty lines', () => {
    expect(splitLines('-y\r\n package \n\n')).toEqual(['-y', 'package'])
  })
})
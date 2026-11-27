// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { applyTheme, previewAccent } from './apply'

const base = {
  accent: '#E0873C',
  accentL: 0.72,
  accentC: 0.17,
  accentH: 35,
  followSystem: false,
}

describe('applyTheme', () => {
  it('把实际外观写到 html[data-mode]', () => {
    applyTheme({ ...base, mode: 'light', modePreference: 'light' })
    expect(document.documentElement.dataset.mode).toBe('light')

    applyTheme({ ...base, mode: 'dark', modePreference: 'dark' })
    expect(document.documentElement.dataset.mode).toBe('dark')
  })

  it('预览强调色时保留当前外观模式', () => {
    const applied = previewAccent('#3366CC', 'light', 'system')
    expect(applied?.mode).toBe('light')
    expect(applied?.modePreference).toBe('system')
    expect(document.documentElement.dataset.mode).toBe('light')
  })
})

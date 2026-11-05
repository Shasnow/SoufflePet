// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'
import type { ThemeDto } from '@/bridge/contracts'
import { applyTheme } from '@/theme/apply'
import { hexToOklch, oklchToHex } from '@/theme/oklch'
import { DEFAULT_ACCENT } from '@/theme/presets'

const urlAccent = (() => {
  try {
    return new URLSearchParams(window.location.search).get('accent')
  } catch {
    return null
  }
})()

const initialAccent = urlAccent ?? oklchToHex(DEFAULT_ACCENT)
const initialOklch = hexToOklch(initialAccent) ?? DEFAULT_ACCENT
const initialTheme: ThemeDto = {
  mode: 'dark',
  accent: oklchToHex(initialOklch),
  accentL: initialOklch.l,
  accentC: initialOklch.c,
  accentH: initialOklch.h,
  followSystem: false,
}

applyTheme(initialTheme)

interface ThemeState {
  theme: ThemeDto
  setTheme: (theme: ThemeDto) => void
}

export const useThemeStore = create<ThemeState>()((set) => ({
  theme: initialTheme,
  setTheme: (theme) => {
    const applied = applyTheme(theme)
    set({ theme: applied })
  },
}))
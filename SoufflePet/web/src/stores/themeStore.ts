// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'
import type { ThemeDto, ThemeMode, ThemeModePreference } from '@/bridge/contracts'
import { applyTheme } from '@/theme/apply'
import { hexToOklch, oklchToHex } from '@/theme/oklch'
import { DEFAULT_ACCENT } from '@/theme/presets'

const params = (() => {
  try {
    return new URLSearchParams(window.location.search)
  } catch {
    return new URLSearchParams('')
  }
})()

const initialAccent = params.get('accent') ?? oklchToHex(DEFAULT_ACCENT)
const initialMode: ThemeMode = params.get('mode') === 'light' ? 'light' : 'dark'
const preferenceParam = params.get('modePreference')
const initialPreference: ThemeModePreference =
  preferenceParam === 'light' || preferenceParam === 'system' ? preferenceParam : initialMode
const initialOklch = hexToOklch(initialAccent) ?? DEFAULT_ACCENT
const initialTheme: ThemeDto = {
  mode: initialMode,
  modePreference: initialPreference,
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
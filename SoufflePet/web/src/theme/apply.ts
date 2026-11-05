// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import type { ThemeDto } from '@/bridge/contracts'
import { hexToOklch, harmonizeAccent, onAccentCss } from './oklch'

/** 把宿主/首帧的主题写进 :root。返回实际采用的（可能被夹过的）主题。 */
export function applyTheme(theme: ThemeDto): ThemeDto {
  const root = document.documentElement
  const parsed = hexToOklch(theme.accent) ?? {
    l: theme.accentL,
    c: theme.accentC,
    h: theme.accentH,
  }
  const harmonized = harmonizeAccent(parsed)

  root.style.setProperty('--accent-l', harmonized.accent.l.toFixed(4))
  root.style.setProperty('--accent-c', harmonized.accent.c.toFixed(4))
  root.style.setProperty('--accent-h', harmonized.accent.h.toFixed(2))
  root.style.setProperty('--on-accent', onAccentCss(harmonized.onAccent, harmonized.accent.h))
  root.setAttribute('data-mode', theme.mode)
  root.setAttribute('data-accent-contrast', harmonized.contrast >= 4.5 ? 'ok' : 'low')

  return {
    ...theme,
    accentL: harmonized.accent.l,
    accentC: harmonized.accent.c,
    accentH: harmonized.accent.h,
  }
}

/** 只用于外观面板的实时预览：不落盘，不改 followSystem。 */
export function previewAccent(accent: string): ThemeDto | null {
  const parsed = hexToOklch(accent)
  if (!parsed) return null
  return applyTheme({
    mode: 'dark',
    accent,
    accentL: parsed.l,
    accentC: parsed.c,
    accentH: parsed.h,
    followSystem: false,
  })
}
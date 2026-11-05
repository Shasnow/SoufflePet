// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import type { Oklch } from './oklch'

export interface AccentPreset {
  id: string
  name: string
  oklch: Oklch
}

/** 8 个预设：固定 L/C，取不同色相；不写 hex 字面量。 */
export const ACCENT_PRESETS: AccentPreset[] = [
  { id: 'amber', name: '琥珀', oklch: { l: 0.72, c: 0.17, h: 35 } },
  { id: 'lime', name: '青柠', oklch: { l: 0.74, c: 0.16, h: 95 } },
  { id: 'green', name: '薄荷', oklch: { l: 0.71, c: 0.15, h: 150 } },
  { id: 'cyan', name: '湖蓝', oklch: { l: 0.73, c: 0.14, h: 195 } },
  { id: 'blue', name: '星蓝', oklch: { l: 0.7, c: 0.16, h: 240 } },
  { id: 'violet', name: '鸢尾', oklch: { l: 0.69, c: 0.17, h: 285 } },
  { id: 'pink', name: '樱粉', oklch: { l: 0.73, c: 0.16, h: 330 } },
  { id: 'rose', name: '山茶', oklch: { l: 0.68, c: 0.18, h: 15 } },
]

export const DEFAULT_ACCENT: Oklch = { l: 0.72, c: 0.17, h: 35 }
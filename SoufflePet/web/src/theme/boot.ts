// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/**
 * 首帧主题注入：在 React 挂载前把 URL 上的强调色写进 :root。
 *
 * 由 main.tsx 第一个 import 触发；不依赖内联脚本，满足 CSP `script-src 'self'`。
 * 只负责强调色与模式；surface/text 等派生 token 在 tokens.css 里用相对颜色语法计算。
 */
(function applyBootTheme() {
  const root = document.documentElement

  function readParams(): URLSearchParams {
    try {
      return new URLSearchParams(window.location.search)
    } catch {
      return new URLSearchParams('')
    }
  }

  function linearize(channel: number): number {
    return channel <= 0.04045 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4)
  }

  function hexToOklch(input: string): { l: number; c: number; h: number } | null {
    const match = /^#?([0-9a-f]{6})$/i.exec(input || '')
    if (!match) return null

    const value = Number.parseInt(match[1], 16)
    const r = linearize(((value >> 16) & 255) / 255)
    const g = linearize(((value >> 8) & 255) / 255)
    const b = linearize((value & 255) / 255)

    const l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b
    const m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b
    const s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b

    const lRoot = Math.cbrt(l)
    const mRoot = Math.cbrt(m)
    const sRoot = Math.cbrt(s)

    const okL = 0.2104542553 * lRoot + 0.793617785 * mRoot - 0.0040720468 * sRoot
    const okA = 1.9779984951 * lRoot - 2.428592205 * mRoot + 0.4505937099 * sRoot
    const okB = 0.0259040371 * lRoot + 0.7827717662 * mRoot - 0.808675766 * sRoot

    let hue = (Math.atan2(okB, okA) * 180) / Math.PI
    if (hue < 0) hue += 360

    return {
      l: Math.min(1, Math.max(0, okL)),
      c: Math.max(0, Math.hypot(okA, okB)),
      h: hue,
    }
  }

  const params = readParams()
  const mode = params.get('mode') === 'light' ? 'light' : 'dark'
  root.setAttribute('data-mode', mode)

  const accent = hexToOklch(params.get('accent') ?? '')
  if (!accent) return

  const style = root.style
  style.setProperty('--accent-l', accent.l.toFixed(4))
  style.setProperty('--accent-c', accent.c.toFixed(4))
  style.setProperty('--accent-h', accent.h.toFixed(2))
  style.setProperty(
    '--on-accent',
    accent.l >= 0.62 ? 'var(--on-accent-dark)' : 'var(--on-accent-light)',
  )
})()
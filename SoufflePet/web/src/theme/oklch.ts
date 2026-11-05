// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/**
 * sRGB ↔ OKLCH 转换、sRGB gamut mapping 与 WCAG 对比度。
 *
 * 公式来自 Björn Ottosson 的 OKLab/OKLCH；这里不依赖 colorjs，保证首帧/单测都可用。
 * 外观面板需要更完整的色域/对比度能力时，仍可叠加 colorjs.io（见 §M5）。
 */

export interface Rgb {
  r: number
  g: number
  b: number
}

export interface Oklch {
  l: number
  c: number
  h: number
}

const DARK_TEXT_L = 0.16
const LIGHT_TEXT_L = 0.98
const TARGET_CONTRAST = 4.5

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value))
}

export function parseHex(input: string): Rgb | null {
  const match = /^#?([0-9a-f]{6})$/i.exec(input.trim())
  if (!match) return null
  const value = Number.parseInt(match[1], 16)
  return {
    r: ((value >> 16) & 255) / 255,
    g: ((value >> 8) & 255) / 255,
    b: (value & 255) / 255,
  }
}

export function rgbToHex(rgb: Rgb): string {
  const toByte = (channel: number) => Math.round(clamp(channel, 0, 1) * 255).toString(16).padStart(2, '0')
  return `#${toByte(rgb.r)}${toByte(rgb.g)}${toByte(rgb.b)}`
}

function linearize(channel: number): number {
  return channel <= 0.04045 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4)
}

function delinearize(channel: number): number {
  const value = clamp(channel, 0, 1)
  return value <= 0.0031308 ? value * 12.92 : 1.055 * Math.pow(value, 1 / 2.4) - 0.055
}

function rgbToLinear(rgb: Rgb) {
  return {
    r: linearize(rgb.r),
    g: linearize(rgb.g),
    b: linearize(rgb.b),
  }
}

export function rgbToOklch(rgb: Rgb): Oklch {
  const { r, g, b } = rgbToLinear(rgb)

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

  return { l: clamp(okL, 0, 1), c: Math.max(0, Math.hypot(okA, okB)), h: hue }
}

export function hexToOklch(hex: string): Oklch | null {
  const rgb = parseHex(hex)
  return rgb ? rgbToOklch(rgb) : null
}

function oklchToLinearRgb(oklch: Oklch): Rgb {
  const hue = (oklch.h * Math.PI) / 180
  const a = oklch.c * Math.cos(hue)
  const b = oklch.c * Math.sin(hue)

  const lRoot = oklch.l + 0.3963377774 * a + 0.2158037573 * b
  const mRoot = oklch.l - 0.1055613458 * a - 0.0638541728 * b
  const sRoot = oklch.l - 0.0894841775 * a - 1.291485548 * b

  const l = lRoot ** 3
  const m = mRoot ** 3
  const s = sRoot ** 3

  return {
    r: 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    g: -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    b: -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  }
}

function inGamut(rgb: Rgb): boolean {
  const epsilon = 0.0001
  return (
    rgb.r >= -epsilon &&
    rgb.r <= 1 + epsilon &&
    rgb.g >= -epsilon &&
    rgb.g <= 1 + epsilon &&
    rgb.b >= -epsilon &&
    rgb.b <= 1 + epsilon
  )
}

/** 把 OKLCH 通过二分降低 C 夹进 sRGB gamut（保持 L/H）。 */
export function toSrgbGamut(oklch: Oklch): Oklch {
  const l = clamp(oklch.l, 0, 1)
  const h = ((oklch.h % 360) + 360) % 360
  let c = Math.max(0, oklch.c)

  if (inGamut(oklchToLinearRgb({ l, c, h }))) return { l, c, h }

  let low = 0
  let high = c
  for (let i = 0; i < 24; i++) {
    const mid = (low + high) / 2
    if (inGamut(oklchToLinearRgb({ l, c: mid, h }))) low = mid
    else high = mid
  }
  c = low
  return { l, c, h }
}

export function oklchToRgb(oklch: Oklch): Rgb {
  const gamut = toSrgbGamut(oklch)
  const linear = oklchToLinearRgb(gamut)
  return {
    r: delinearize(linear.r),
    g: delinearize(linear.g),
    b: delinearize(linear.b),
  }
}

export function oklchToHex(oklch: Oklch): string {
  return rgbToHex(oklchToRgb(oklch))
}

export function oklchCss(oklch: Oklch, alpha?: number): string {
  const l = clamp(oklch.l, 0, 1)
  const c = Math.max(0, oklch.c)
  const h = ((oklch.h % 360) + 360) % 360
  return alpha === undefined
    ? `oklch(${l.toFixed(4)} ${c.toFixed(4)} ${h.toFixed(2)})`
    : `oklch(${l.toFixed(4)} ${c.toFixed(4)} ${h.toFixed(2)} / ${alpha})`
}

function relativeLuminance(rgb: Rgb): number {
  const { r, g, b } = rgbToLinear(rgb)
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

export function contrastRatio(a: Rgb, b: Rgb): number {
  const la = relativeLuminance(a)
  const lb = relativeLuminance(b)
  const lighter = Math.max(la, lb)
  const darker = Math.min(la, lb)
  return (lighter + 0.05) / (darker + 0.05)
}

export type OnAccent = 'dark' | 'light'

export interface HarmonizedAccent {
  accent: Oklch
  onAccent: OnAccent
  contrast: number
  gamutClamped: boolean
  contrastAdjusted: boolean
}

/** 选择 on-accent 的明暗；如果对比度不足，向远离 on-accent 的方向移动 L。 */
export function harmonizeAccent(input: Oklch): HarmonizedAccent {
  const gamut = toSrgbGamut({ ...input, l: clamp(input.l, 0.2, 0.92) })
  const gamutClamped = Math.abs(gamut.c - input.c) > 0.0005 || Math.abs(gamut.l - input.l) > 0.0005

  const darkText: Rgb = { r: 0.08, g: 0.08, b: 0.09 }
  const lightText: Rgb = { r: 0.97, g: 0.97, b: 0.98 }

  const pick = (accent: Oklch): { onAccent: OnAccent; contrast: number } => {
    const rgb = oklchToRgb(accent)
    const dark = contrastRatio(rgb, darkText)
    const light = contrastRatio(rgb, lightText)
    return dark >= light
      ? { onAccent: 'dark', contrast: dark }
      : { onAccent: 'light', contrast: light }
  }

  let accent = gamut
  let choice = pick(accent)
  let contrastAdjusted = false

  if (choice.contrast < TARGET_CONTRAST) {
    contrastAdjusted = true
    // 深色字说明强调色偏亮，继续提亮；浅色字说明偏暗，继续压暗。
    const step = choice.onAccent === 'dark' ? 0.012 : -0.012
    for (let i = 0; i < 30; i++) {
      const next = toSrgbGamut({ l: clamp(accent.l + step, 0.2, 0.95), c: accent.c, h: accent.h })
      const nextChoice = pick(next)
      accent = next
      choice = nextChoice
      if (nextChoice.contrast >= TARGET_CONTRAST) break
    }
  }

  return {
    accent,
    onAccent: choice.onAccent,
    contrast: choice.contrast,
    gamutClamped,
    contrastAdjusted,
  }
}

export function onAccentCss(onAccent: OnAccent, hue: number): string {
  return onAccent === 'dark'
    ? oklchCss({ l: DARK_TEXT_L, c: 0.01, h: hue })
    : oklchCss({ l: LIGHT_TEXT_L, c: 0.005, h: hue })
}

export function formatPercent(value: number, digits = 1): string {
  if (!Number.isFinite(value)) return '—'
  return `${(value * 100).toFixed(digits)}%`
}
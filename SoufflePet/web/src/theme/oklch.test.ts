// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import {
  contrastRatio,
  harmonizeAccent,
  hexToOklch,
  oklchToHex,
  oklchToRgb,
  parseHex,
  rgbToHex,
  toSrgbGamut,
} from './oklch'

describe('oklch', () => {
  it('parses and formats hex', () => {
    expect(parseHex('#E0873C')).toEqual({
      r: 224 / 255,
      g: 135 / 255,
      b: 60 / 255,
    })
    expect(rgbToHex({ r: 1, g: 1, b: 1 })).toBe('#ffffff')
    expect(parseHex('not-a-color')).toBeNull()
  })

  it('round-trips a saturated orange through OKLCH', () => {
    const oklch = hexToOklch('#E0873C')
    expect(oklch).not.toBeNull()
    expect(oklch!.l).toBeGreaterThan(0.5)
    expect(oklch!.c).toBeGreaterThan(0.05)
    expect(oklch!.h).toBeGreaterThan(20)
    expect(oklch!.h).toBeLessThan(90)

    const hex = oklchToHex(oklch!)
    expect(hex).toMatch(/^#[0-9a-f]{6}$/)
    const back = hexToOklch(hex)!
    expect(Math.abs(back.l - oklch!.l)).toBeLessThan(0.01)
    expect(Math.abs(back.c - oklch!.c)).toBeLessThan(0.02)
  })

  it('clamps out-of-gamut colors by lowering chroma', () => {
    const clamped = toSrgbGamut({ l: 0.7, c: 1, h: 35 })
    expect(clamped.c).toBeLessThan(1)
    const rgb = oklchToRgb({ l: 0.7, c: 1, h: 35 })
    for (const channel of [rgb.r, rgb.g, rgb.b]) {
      expect(channel).toBeGreaterThanOrEqual(0)
      expect(channel).toBeLessThanOrEqual(1)
    }
  })

  it('computes WCAG contrast', () => {
    const black = { r: 0, g: 0, b: 0 }
    const white = { r: 1, g: 1, b: 1 }
    expect(contrastRatio(black, white)).toBeCloseTo(21, 0)
    expect(contrastRatio(black, black)).toBeCloseTo(1, 2)
  })

  it('harmonizes accent so on-accent text reaches 4.5:1', () => {
    const light = harmonizeAccent({ l: 0.9, c: 0.1, h: 35 })
    expect(light.onAccent).toBe('dark')
    expect(light.contrast).toBeGreaterThanOrEqual(4.5)

    const dark = harmonizeAccent({ l: 0.3, c: 0.1, h: 35 })
    expect(dark.onAccent).toBe('light')
    expect(dark.contrast).toBeGreaterThanOrEqual(4.5)
  })
})
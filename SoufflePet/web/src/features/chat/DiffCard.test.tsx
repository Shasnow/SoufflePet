// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { fireEvent, render } from '@testing-library/react'
import type { FileDiffDto } from '@/bridge/contracts'
import { DiffCard } from './DiffCard'

const diff: FileDiffDto = {
  path: 'D:/notes/table.md',
  added: 2,
  removed: 1,
  hunks: [
    {
      oldStart: 1,
      oldLines: 3,
      newStart: 1,
      newLines: 4,
      lines: [
        { kind: 'ctx', text: '# 整理结果' },
        { kind: 'del', text: '缓存命中率 = 缓存读取 / 输入 token' },
        { kind: 'add', text: '缓存命中率 = 缓存读取 / 输入 token（含缓存写入）' },
        { kind: 'add', text: '示例：12300 / 12480 = 98.6%' },
      ],
    },
  ],
}

describe('DiffCard', () => {
  it('默认只显示统计，展开后才渲染 hunk 行', () => {
    const { container } = render(<DiffCard diff={diff} />)

    expect(container.querySelector('.diff-head')?.tagName).toBe('BUTTON')
    expect(container.querySelector('.diff-path')?.textContent).toBe('table.md')
    expect(container.querySelector('.diff-added')?.textContent).toBe('+2')
    expect(container.querySelector('.diff-removed')?.textContent).toBe('−1')
    expect(container.querySelector('.diff-line')).toBeNull()

    fireEvent.click(container.querySelector('.diff-head-button')!)

    expect(container.querySelectorAll('.diff-line').length).toBe(4)
    expect(container.querySelectorAll('.diff-line.diff-add').length).toBe(2)
    expect(container.querySelectorAll('.diff-line.diff-del').length).toBe(1)
    expect(container.querySelectorAll('.diff-line.diff-ctx').length).toBe(1)
    expect(container.querySelector('.diff-hunk-head')?.textContent).toContain('@@ -1,3 +1,4 @@')
  })

  it('没有 hunk 的截断卡片直接提示，不渲染行', () => {
    const { container } = render(
      <DiffCard diff={{ path: 'big.txt', added: 0, removed: 0, truncated: true, hunks: [] }} />,
    )

    expect(container.querySelector('.diff-note')?.textContent).toContain('未生成 diff')
    expect(container.querySelector('.diff-head')?.tagName).toBe('DIV')
    expect(container.querySelector('.diff-line')).toBeNull()
  })

  it('展开后显示截断提示', () => {
    const { container } = render(<DiffCard diff={{ ...diff, truncated: true }} />)
    expect(container.querySelector('.diff-note')).toBeNull()

    fireEvent.click(container.querySelector('.diff-head-button')!)
    expect(container.querySelector('.diff-note')?.textContent).toContain('已截断')
  })
})
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it } from 'vitest'
import { render } from '@testing-library/react'
import { RenderMarkdown } from './Markdown'

describe('Markdown', () => {
  it('drops javascript: links and remote images', () => {
    const { container } = render(
      <RenderMarkdown text={'[bad](javascript:alert(1))\n\n![x](https://example.com/a.png)'} />,
    )
    const link = container.querySelector('a')
    expect(link?.getAttribute('href') ?? '').not.toContain('javascript')
    expect(container.querySelector('img')).toBeNull()
  })

  it('renders GFM tables inside a horizontal scroll wrapper', () => {
    const { container } = render(
      <RenderMarkdown text={'| a | b |\n| - | - |\n| 1 | 2 |'} />,
    )
    expect(container.querySelector('.md-table-scroll table')).not.toBeNull()
  })
})
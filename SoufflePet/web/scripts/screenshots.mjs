// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/*
 * 五档宽度截图验收（mock 宿主，无需 .NET 进程）。
 * 用法：pnpm --dir SoufflePet/web screenshots
 * 输出：仓库根 .verifybuild/ui/<场景>-<宽度>.png
 */

import { mkdir } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const here = dirname(fileURLToPath(import.meta.url))
const root = join(here, '..', '..', '..')
const outDir = join(root, '.verifybuild', 'ui')
const base = process.env.SOULLE_UI_URL ?? 'http://127.0.0.1:5173/app/'

const viewports = [
  { width: 1416, height: 900, label: '1416' },
  { width: 1024, height: 900, label: '1024' },
  { width: 800, height: 900, label: '800' },
  { width: 652, height: 1002, label: '652' },
  { width: 420, height: 800, label: '420' },
]

const scenarios = [
  { name: 'welcome', query: '?mock=1&scenario=empty', hash: '#/chat/s-welcome' },
  { name: 'rich', query: '?mock=1&scenario=rich', hash: '#/chat/s-rich' },
  { name: 'rich-top', query: '?mock=1&scenario=rich', hash: '#/chat/s-rich', scrollTop: true },
  { name: 'ask', query: '?mock=1&scenario=ask', hash: '#/chat/s-rich' },
  { name: 'settings', query: '?mock=1', hash: '#/settings/appearance' },
]

await mkdir(outDir, { recursive: true })

const browser = await chromium.launch({ headless: true })
const errors = []

for (const scenario of scenarios) {
  for (const viewport of viewports) {
    const context = await browser.newContext({
      viewport: { width: viewport.width, height: viewport.height },
      deviceScaleFactor: 1,
      locale: 'zh-CN',
    })
    const page = await context.newPage()
    page.on('pageerror', (error) => errors.push(`${scenario.name}-${viewport.label}: ${error.message}`))
    page.on('console', (message) => {
      if (message.type() === 'error') errors.push(`${scenario.name}-${viewport.label}: ${message.text()}`)
    })

    const url = `${base}${scenario.query}${scenario.hash}`
    await page.goto(url, { waitUntil: 'networkidle' })
    await page.waitForSelector('.app-shell', { timeout: 15_000 })
    await page.waitForTimeout(350)
    if (scenario.scrollTop) {
      await page.evaluate(() => {
        const list = document.querySelector('.message-list')
        if (list) list.scrollTop = 0
      })
      await page.waitForTimeout(250)
    }

    const file = join(outDir, `${scenario.name}-${viewport.label}.png`)
    await page.screenshot({ path: file })
    console.log(`captured ${file}`)
    await context.close()
  }
}

await browser.close()

if (errors.length > 0) {
  console.error('page errors:')
  for (const error of errors) console.error(`  ${error}`)
  process.exitCode = 1
}
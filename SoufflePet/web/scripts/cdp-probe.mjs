// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/*
 * 通过 WebView2 的 CDP（--remote-debugging-port）检查真实宿主窗口里的 SPA：
 * 页面 URL、bridge 注入、主题切换、toast、截图。
 *
 * 先以 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` 启动应用，再运行：
 * pnpm --dir SoufflePet/web exec node scripts/cdp-probe.mjs
 */

import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const here = dirname(fileURLToPath(import.meta.url))
const root = join(here, '..', '..', '..')
const outDir = join(root, '.verifybuild', 'ui')

const browser = await chromium.connectOverCDP('http://127.0.0.1:9222')
const contexts = browser.contexts()
const pages = contexts.flatMap((context) => context.pages())
const page = pages.find((candidate) => candidate.url().includes('/app/index.html'))
if (!page) {
  console.error('未找到 SPA 页面；已打开：', pages.map((candidate) => candidate.url()))
  process.exit(1)
}

const errors = []
page.on('pageerror', (error) => errors.push(error.message))
page.on('console', (message) => {
  if (message.type() === 'error') errors.push(message.text())
})

await page.reload({ waitUntil: 'networkidle' })
await page.waitForSelector('.app-shell', { timeout: 15_000 })
await page.waitForTimeout(500)

const info = await page.evaluate(() => ({
  url: location.href,
  title: document.querySelector('.topbar-title')?.textContent ?? '',
  bridgeInjected: typeof window.invokeCSharpAction === 'function',
  receiver: typeof window.__bridge?.receive === 'function',
  accentL: getComputedStyle(document.documentElement).getPropertyValue('--accent-l').trim(),
  welcome: Boolean(document.querySelector('.welcome')),
  sessions: document.querySelectorAll('.session-item').length,
  modelText: document.querySelector('.composer-tool .tool-text')?.textContent ?? '',
}))
console.log('spa info:', info)

await page.screenshot({ path: join(outDir, 'webview-app.png') })

// 直接走宿主桥改强调色：页面 client 不会匹配这个请求 id，但 host.theme 事件仍会应用。
await page.evaluate(() => {
  const request = {
    v: 1,
    id: 9999,
    type: 'req',
    method: 'settings.appearance.setAccent',
    params: { accent: '#7A5CF0' },
  }
  window.invokeCSharpAction?.(JSON.stringify(request))
})
await page.waitForTimeout(600)
const purple = await page.evaluate(() => ({
  accentL: getComputedStyle(document.documentElement).getPropertyValue('--accent-l').trim(),
  accentH: getComputedStyle(document.documentElement).getPropertyValue('--accent-h').trim(),
  accent: getComputedStyle(document.documentElement).getPropertyValue('--accent').trim(),
}))
console.log('after accent:', purple)
await page.screenshot({ path: join(outDir, 'webview-app-purple.png') })

// 发送一条 chat.send，确认 bootstrap RPC 的 toast 到页面
await page.evaluate(() => {
  window.invokeCSharpAction?.(
    JSON.stringify({
      v: 1,
      id: 10000,
      type: 'req',
      method: 'chat.send',
      params: { text: 'bridge probe' },
    }),
  )
})
await page.waitForTimeout(400)
const toast = await page.evaluate(() => document.querySelector('.toast')?.textContent ?? '')
console.log('toast:', toast)

// 恢复默认强调色，避免测试污染运行配置
await page.evaluate(() => {
  window.invokeCSharpAction?.(
    JSON.stringify({
      v: 1,
      id: 10001,
      type: 'req',
      method: 'settings.appearance.reset',
      params: {},
    }),
  )
})
await page.waitForTimeout(500)
console.log(
  'reset accent:',
  await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()),
)

console.log('page errors:', errors.length ? errors : 'none')
await browser.close()

if (errors.length) process.exitCode = 1
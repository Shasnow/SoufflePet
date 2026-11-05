// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/*
 * 颜色字面量检查：除 tokens.css 外，源码里不允许出现 #hex / rgb( / hsl( / oklch(。
 *
 * 迁移期规则（docs/react-ui-contract.md §6）：
 * - tokens.css 是唯一色值来源；
 * - 组件必须用 var(--…) / Tailwind 语义色；
 * - 测试文件可构造色值用于断言；
 * - 构建时由 pnpm check:colors 执行，package.json 的 build 前置调用。
 */

import { readFileSync, readdirSync, statSync } from 'node:fs'
import { extname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('..', import.meta.url))
const scanRoots = ['src', 'public', 'index.html']
const extensions = new Set(['.ts', '.tsx', '.css', '.html', '.js', '.mjs'])
const colorPattern = /#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(|oklch\(/g

const ignored = [
  /(^|[\\/])node_modules([\\/]|$)/,
  /(^|[\\/])test([\\/]|$)/,
  /\.test\.(ts|tsx)$/,
  /\.spec\.(ts|tsx)$/,
  /src[\\/]styles[\\/]tokens\.css$/,
  // 颜色引擎本身要生成 oklch(...) 字符串；它不承载设计色值。
  /src[\\/]theme[\\/]oklch\.ts$/,
]

function isIgnored(path) {
  const relativePath = relative(root, path).replaceAll('\\', '/')
  return ignored.some((pattern) => pattern.test(relativePath))
}

function* walk(path) {
  const stat = statSync(path)
  if (stat.isDirectory()) {
    for (const entry of readdirSync(path)) yield* walk(join(path, entry))
    return
  }
  if (!extensions.has(extname(path))) return
  if (isIgnored(path)) return
  yield path
}

const violations = []
for (const item of scanRoots) {
  const absolute = join(root, item)
  let files
  try {
    files = statSync(absolute).isDirectory() ? [...walk(absolute)] : [absolute]
  } catch {
    continue
  }

  for (const file of files) {
    const text = readFileSync(file, 'utf8')
    const lines = text.split(/\r?\n/)
    lines.forEach((line, index) => {
      colorPattern.lastIndex = 0
      if (!colorPattern.test(line)) return
      violations.push({
        file: relative(root, file).replaceAll('\\', '/'),
        line: index + 1,
        text: line.trim(),
      })
    })
  }
}

if (violations.length === 0) {
  console.log('check-colors: ok')
  process.exit(0)
}

console.error(`check-colors: ${violations.length} 处颜色字面量（只允许在 src/styles/tokens.css 中）`)
for (const violation of violations) {
  console.error(`  ${violation.file}:${violation.line}  ${violation.text}`)
}
process.exit(1)
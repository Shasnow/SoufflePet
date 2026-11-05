// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { createHighlighterCore } from 'shiki/core'
import { createJavaScriptRegexEngine } from 'shiki/engine/javascript'
import type { ThemeRegistration } from 'shiki'
import ts from 'shiki/langs/typescript.mjs'
import tsx from 'shiki/langs/tsx.mjs'
import js from 'shiki/langs/javascript.mjs'
import jsx from 'shiki/langs/jsx.mjs'
import json from 'shiki/langs/json.mjs'
import bash from 'shiki/langs/bash.mjs'
import shell from 'shiki/langs/shellscript.mjs'
import powershell from 'shiki/langs/powershell.mjs'
import csharp from 'shiki/langs/csharp.mjs'
import python from 'shiki/langs/python.mjs'
import css from 'shiki/langs/css.mjs'
import html from 'shiki/langs/html.mjs'
import xml from 'shiki/langs/xml.mjs'
import markdown from 'shiki/langs/markdown.mjs'
import yaml from 'shiki/langs/yaml.mjs'
import sql from 'shiki/langs/sql.mjs'
import diff from 'shiki/langs/diff.mjs'
import go from 'shiki/langs/go.mjs'
import rust from 'shiki/langs/rust.mjs'
import java from 'shiki/langs/java.mjs'

// 用 CSS 变量做主题色：代码块颜色与 tokens.css 同一套来源，不产生 hex 字面量。
const theme: ThemeRegistration = {
  name: 'soufflepet',
  type: 'dark',
  colors: {
    'editor.background': 'var(--shiki-background)',
    'editor.foreground': 'var(--shiki-foreground)',
  },
  tokenColors: [
    {
      scope: ['comment', 'punctuation.definition.comment'],
      settings: { foreground: 'var(--shiki-token-comment)' },
    },
    {
      scope: ['string', 'string.quoted', 'string.template', 'string.regexp'],
      settings: { foreground: 'var(--shiki-token-string)' },
    },
    {
      scope: ['constant', 'constant.numeric', 'constant.language', 'support.constant'],
      settings: { foreground: 'var(--shiki-token-constant)' },
    },
    {
      scope: ['keyword', 'storage', 'storage.type', 'keyword.control', 'keyword.operator'],
      settings: { foreground: 'var(--shiki-token-keyword)' },
    },
    {
      scope: ['entity.name.function', 'support.function', 'meta.function-call'],
      settings: { foreground: 'var(--shiki-token-function)' },
    },
    {
      scope: ['variable.parameter', 'parameter'],
      settings: { foreground: 'var(--shiki-token-parameter)' },
    },
    {
      scope: ['punctuation', 'meta.brace', 'meta.delimiter'],
      settings: { foreground: 'var(--shiki-token-punctuation)' },
    },
    {
      scope: ['entity.name.type', 'support.type', 'entity.name.class', 'entity.name.tag'],
      settings: { foreground: 'var(--shiki-token-constant)' },
    },
  ],
}

const LANGS = [
  ts,
  tsx,
  js,
  jsx,
  json,
  bash,
  shell,
  powershell,
  csharp,
  python,
  css,
  html,
  xml,
  markdown,
  yaml,
  sql,
  diff,
  go,
  rust,
  java,
]

let highlighterPromise: ReturnType<typeof createHighlighterCore> | null = null

const LANG_ALIASES: Record<string, string> = {
  ts: 'typescript',
  typescript: 'typescript',
  tsx: 'tsx',
  js: 'javascript',
  javascript: 'javascript',
  jsx: 'jsx',
  json: 'json',
  jsonc: 'json',
  sh: 'bash',
  bash: 'bash',
  shell: 'shellscript',
  shellscript: 'shellscript',
  zsh: 'bash',
  ps1: 'powershell',
  powershell: 'powershell',
  cs: 'csharp',
  csharp: 'csharp',
  py: 'python',
  python: 'python',
  css: 'css',
  scss: 'css',
  less: 'css',
  html: 'html',
  htm: 'html',
  xml: 'xml',
  md: 'markdown',
  markdown: 'markdown',
  yml: 'yaml',
  yaml: 'yaml',
  sql: 'sql',
  diff: 'diff',
  patch: 'diff',
  go: 'go',
  rust: 'rust',
  rs: 'rust',
  java: 'java',
}

function getHighlighter() {
  if (!highlighterPromise) {
    highlighterPromise = createHighlighterCore({
      themes: [theme],
      langs: LANGS,
      engine: createJavaScriptRegexEngine(),
    })
  }
  return highlighterPromise
}

export async function highlight(code: string, language?: string): Promise<string> {
  const mapped = language ? LANG_ALIASES[language.toLowerCase()] : undefined
  if (!mapped) {
    return `<pre class="shiki"><code>${escapeHtml(code)}</code></pre>`
  }

  const highlighter = await getHighlighter()
  if (!highlighter.getLoadedLanguages().includes(mapped)) {
    return `<pre class="shiki"><code>${escapeHtml(code)}</code></pre>`
  }
  return highlighter.codeToHtml(code, { lang: mapped, theme: 'soufflepet' })
}

function escapeHtml(text: string): string {
  return text
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
}
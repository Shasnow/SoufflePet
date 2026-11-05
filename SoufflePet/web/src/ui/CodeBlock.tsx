// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Check, Copy } from 'lucide-react'
import { useEffect, useState } from 'react'

interface CodeBlockProps {
  code: string
  language?: string
}

/** Shiki 按需加载（css-variables 主题），未加载/失败时回退纯文本 <pre>。 */
export function CodeBlock({ code, language }: CodeBlockProps) {
  const [html, setHtml] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    let cancelled = false
    setHtml(null)

    const run = async () => {
      try {
        const { highlight } = await import('./highlighter')
        const result = await highlight(code, language)
        if (!cancelled) setHtml(result)
      } catch {
        // 未知语言或加载失败：回退纯文本，不影响阅读
      }
    }

    void run()
    return () => {
      cancelled = true
    }
  }, [code, language])

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1200)
    } catch {
      // 剪贴板不可用时忽略
    }
  }

  return (
    <div className="code-wrap">
      <button type="button" className="code-copy" aria-label="复制代码" onClick={() => void copy()}>
        {copied ? <Check size={13} /> : <Copy size={13} />}
      </button>
      {language ? <span className="code-lang">{language}</span> : null}
      {html ? (
        <div className="shiki-host" dangerouslySetInnerHTML={{ __html: html }} />
      ) : (
        <pre>
          <code>{code}</code>
        </pre>
      )}
    </div>
  )
}
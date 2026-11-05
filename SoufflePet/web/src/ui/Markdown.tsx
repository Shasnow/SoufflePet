// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { memo, isValidElement, type ReactNode } from 'react'
import Markdown from 'react-markdown'
import type { Components, Options } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import remarkMath from 'remark-math'
import rehypeKatex from 'rehype-katex'
import rehypeSanitize from 'rehype-sanitize'
import 'katex/dist/katex.min.css'
import { invoke } from '@/bridge/client'
import { CodeBlock } from './CodeBlock'

const remarkPlugins: NonNullable<Options['remarkPlugins']> = [remarkGfm, remarkMath]
const rehypePlugins: NonNullable<Options['rehypePlugins']> = [
  rehypeSanitize,
  [rehypeKatex, { throwOnError: false, strict: false }],
]

function isSafeLink(url: string): boolean {
  return /^(https?:|mailto:|#)/i.test(url)
}

function extractLanguage(className: unknown): string | undefined {
  if (typeof className !== 'string') return undefined
  return /language-([\w-]+)/.exec(className)?.[1]
}

const components: Components = {
  a({ href, children }) {
    const url = href ?? ''
    return (
      <a
        href={url}
        title={url}
        onClick={(event) => {
          if (!url || url.startsWith('#')) return
          event.preventDefault()
          if (!isSafeLink(url)) return
          void invoke('chat.link.open', { url }).catch(() => undefined)
        }}
      >
        {children}
      </a>
    )
  },
  img({ alt }) {
    return <span className="md-image-blocked">[图片{alt ? `：${alt}` : ''}]</span>
  },
  table({ children }) {
    return (
      <div className="md-table-scroll">
        <table>{children}</table>
      </div>
    )
  },
  pre({ children }) {
    const child = Array.isArray(children) ? children[0] : children
    if (isValidElement<{ className?: string; children?: ReactNode }>(child)) {
      const code = String(child.props.children ?? '').replace(/\n$/, '')
      return <CodeBlock code={code} language={extractLanguage(child.props.className)} />
    }
    return <pre>{children}</pre>
  },
  code({ className, children }) {
    return <code className={className}>{children}</code>
  },
}

export const RenderMarkdown = memo(function RenderMarkdown({ text }: { text: string }) {
  return (
    <div className="md">
      <Markdown
        remarkPlugins={remarkPlugins}
        rehypePlugins={rehypePlugins}
        components={components}
        urlTransform={(url, key) => {
          if (key === 'src') return undefined
          return isSafeLink(url) ? url : undefined
        }}
      >
        {text}
      </Markdown>
    </div>
  )
})
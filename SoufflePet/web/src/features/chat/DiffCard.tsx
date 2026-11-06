// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { ChevronDown, FileDiff } from 'lucide-react'
import { useState } from 'react'
import type { FileDiffDto } from '@/bridge/contracts'

function fileName(path: string): string {
  const index = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
  return index >= 0 ? path.slice(index + 1) : path
}

/** 一个文件的编辑差异卡片：默认只显示文件头与增删统计，展开后才渲染行。 */
export function DiffCard({ diff }: { diff: FileDiffDto }) {
  const [expanded, setExpanded] = useState(false)
  const hasLines = diff.hunks.length > 0

  const summary = (
    <>
      {hasLines ? (
        <ChevronDown
          size={12}
          className="diff-chevron"
          style={{ transform: expanded ? 'rotate(0deg)' : 'rotate(-90deg)' }}
        />
      ) : null}
      <FileDiff size={13} />
      <span className="diff-path" title={diff.path}>
        {fileName(diff.path) || diff.path}
      </span>
      {hasLines || !diff.truncated ? (
        <>
          <span className="diff-count diff-added" aria-label={`新增 ${diff.added} 行`}>
            +{diff.added}
          </span>
          <span className="diff-count diff-removed" aria-label={`删除 ${diff.removed} 行`}>
            −{diff.removed}
          </span>
        </>
      ) : null}
    </>
  )

  return (
    <div className="diff-card">
      {hasLines ? (
        <button
          type="button"
          className="diff-head diff-head-button"
          onClick={() => setExpanded((value) => !value)}
          aria-expanded={expanded}
          aria-label={expanded ? '收起差异' : '展开差异'}
          title={diff.path}
        >
          {summary}
        </button>
      ) : (
        <div className="diff-head" title={diff.path}>
          {summary}
        </div>
      )}

      {hasLines && expanded ? (
        <div className="diff-body">
          {diff.hunks.map((hunk, hunkIndex) => (
            <div className="diff-hunk" key={hunkIndex}>
              <div className="diff-hunk-head">
                @@ -{hunk.oldStart},{hunk.oldLines} +{hunk.newStart},{hunk.newLines} @@
              </div>
              {hunk.lines.map((line, lineIndex) => (
                <div className={`diff-line diff-${line.kind}`} key={lineIndex}>
                  <span className="diff-sign">
                    {line.kind === 'add' ? '+' : line.kind === 'del' ? '−' : ' '}
                  </span>
                  <code>{line.text || ' '}</code>
                </div>
              ))}
            </div>
          ))}
        </div>
      ) : null}

      {diff.truncated && (!hasLines || expanded) ? (
        <div className="diff-note">
          {hasLines ? '差异过长，已截断' : '文件过大，未生成 diff'}
        </div>
      ) : null}
    </div>
  )
}
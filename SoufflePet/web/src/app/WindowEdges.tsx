// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { invoke } from '@/bridge/client'
import type { WindowResizeEdge } from '@/bridge/contracts'
import { useUiStore } from '@/stores/uiStore'

const EDGES: WindowResizeEdge[] = ['lt', 't', 'rt', 'l', 'r', 'lb', 'b', 'rb']

/** 页面最外圈 6 px 的缩放边带；最大化时禁用。 */
export function WindowEdges() {
  const maximized = useUiStore((state) => state.maximized)
  if (maximized) return null

  return (
    <>
      {EDGES.map((edge) => (
        <div
          key={edge}
          className="resize-edge"
          data-edge={edge}
          onPointerDown={(event) => {
            if (event.button !== 0) return
            event.preventDefault()
            void invoke('window.resize', { edge }).catch(() => undefined)
          }}
        />
      ))}
    </>
  )
}
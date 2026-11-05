// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import type { AppSnapshotDto } from './contracts'

export interface RequestEnvelope {
  v: 1
  id: number
  type: 'req'
  method: string
  params?: unknown
}

export interface ResponseOkEnvelope {
  v: 1
  id: number
  ok: true
  result: unknown
}

export interface ResponseErrorEnvelope {
  v: 1
  id: number
  ok: false
  error: {
    code: string
    message: string
    details?: unknown
  }
}

export interface EventEnvelope {
  v: 1
  type: 'event'
  name: string
  /** 宿主全局 rev；v1 新增的可选字段，非对象 payload 也靠它排序。 */
  rev?: number
  payload: unknown
}

export interface SnapshotEnvelope {
  v: 1
  type: 'snapshot'
  rev: number
  data: AppSnapshotDto
}

export interface SnapshotFileEnvelope {
  v: 1
  type: 'snapshot-file'
  rev: number
  url: string
}

export interface EventFileEnvelope {
  v: 1
  type: 'event-file'
  name: string
  rev: number
  url: string
}

export type IncomingEnvelope =
  | ResponseOkEnvelope
  | ResponseErrorEnvelope
  | EventEnvelope
  | SnapshotEnvelope
  | SnapshotFileEnvelope
  | EventFileEnvelope

export type BridgeErrorCode =
  | 'invalid_argument'
  | 'not_found'
  | 'forbidden'
  | 'conflict'
  | 'io_error'
  | 'timeout'
  | 'unsupported'
  | 'internal'
  | 'unknown_method'
  | 'bridge_unavailable'

export class BridgeError extends Error {
  readonly code: BridgeErrorCode
  readonly details?: unknown

  constructor(code: BridgeErrorCode, message: string, details?: unknown) {
    super(message)
    this.name = 'BridgeError'
    this.code = code
    this.details = details
  }
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function isRequestEnvelope(value: unknown): value is RequestEnvelope {
  return (
    isRecord(value) &&
    value.v === 1 &&
    value.type === 'req' &&
    typeof value.id === 'number' &&
    typeof value.method === 'string'
  )
}

export function parseIncoming(value: unknown): IncomingEnvelope | null {
  if (!isRecord(value) || value.v !== 1) return null

  if (value.type === 'snapshot' && typeof value.rev === 'number' && isRecord(value.data)) {
    return value as unknown as SnapshotEnvelope
  }

  if (value.type === 'snapshot-file' && typeof value.rev === 'number' && typeof value.url === 'string') {
    return value as unknown as SnapshotFileEnvelope
  }

  if (value.type === 'event-file' && typeof value.name === 'string' && typeof value.url === 'string') {
    return value as unknown as EventFileEnvelope
  }

  if (value.type === 'event' && typeof value.name === 'string') {
    return value as unknown as EventEnvelope
  }

  if (typeof value.id === 'number') {
    if (value.ok === true) return value as unknown as ResponseOkEnvelope
    if (value.ok === false && isRecord(value.error)) return value as unknown as ResponseErrorEnvelope
  }

  return null
}

export function makeRequest(id: number, method: string, params?: unknown): RequestEnvelope {
  return params === undefined
    ? { v: 1, id, type: 'req', method }
    : { v: 1, id, type: 'req', method, params }
}
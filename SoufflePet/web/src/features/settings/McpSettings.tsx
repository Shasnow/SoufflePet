// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Plus, RefreshCw, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { McpServerDto, McpToolDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import {
  errorMessage,
  formatKeyValueLines,
  parseKeyValueLines,
  SettingsCard,
  SettingsEmpty,
  SettingsError,
  splitLines,
  Toggle,
} from './SettingsShared'

interface McpListPayload {
  servers: McpServerDto[]
  path: string
  error?: string
}

const EMPTY_SERVER: McpServerDto = {
  name: '',
  enabled: true,
  transport: 'stdio',
  command: '',
  arguments: [],
  workingDirectory: '',
  environment: {},
  url: '',
  headers: {},
  timeoutSeconds: 30,
}

function cloneServer(server: McpServerDto): McpServerDto {
  return {
    ...server,
    arguments: [...server.arguments],
    environment: { ...server.environment },
    headers: { ...server.headers },
  }
}

export function McpSettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<McpListPayload | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [draft, setDraft] = useState<McpServerDto | null>(null)
  const [originalName, setOriginalName] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [argsText, setArgsText] = useState('')
  const [envText, setEnvText] = useState('')
  const [headersText, setHeadersText] = useState('')
  const [tools, setTools] = useState<McpToolDto[] | null>(null)
  const [testing, setTesting] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const load = useCallback(
    async (selectName?: string) => {
      try {
        const payload = await invoke<McpListPayload>('settings.mcp.list')
        setData(payload)
        const next =
          selectName && payload.servers.some((server) => server.name === selectName)
            ? selectName
            : selected && payload.servers.some((server) => server.name === selected)
              ? selected
              : payload.servers[0]?.name ?? null
        setSelected(next)
        if (payload.error) setError(payload.error)
      } catch (loadError) {
        setError(`读取 MCP 配置失败：${errorMessage(loadError)}`)
      }
    },
    [selected],
  )

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!data || creating) return
    if (!selected) {
      setDraft(null)
      setOriginalName(null)
      setTools(null)
      return
    }

    const server = data.servers.find((item) => item.name === selected) ?? null
    if (!server) return
    setDraft(cloneServer(server))
    setOriginalName(server.name)
    setArgsText(server.arguments.join('\n'))
    setEnvText(formatKeyValueLines(server.environment))
    setHeadersText(formatKeyValueLines(server.headers))
    setTools(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, selected, creating])

  const startNew = () => {
    const base = 'mcp'
    let name = base
    for (let index = 2; data?.servers.some((server) => server.name === name); index++) {
      name = `${base}-${index}`
    }

    const server = { ...EMPTY_SERVER, name }
    setSelected(null)
    setOriginalName(null)
    setCreating(true)
    setDraft(server)
    setArgsText('')
    setEnvText('')
    setHeadersText('')
    setTools(null)
    setError(null)
  }

  const save = async () => {
    if (!draft) return
    const environment = parseKeyValueLines(envText)
    const headers = parseKeyValueLines(headersText)
    if (!environment) {
      setError('环境变量格式应为每行 KEY=VALUE')
      return
    }
    if (!headers) {
      setError('请求头格式应为每行 KEY=VALUE')
      return
    }

    setBusy(true)
    setError(null)
    try {
      await invoke('settings.mcp.save', {
        ...draft,
        arguments: splitLines(argsText),
        environment,
        headers,
        previousName: originalName,
      })
      setCreating(false)
      await load(draft.name.trim())
      pushToast({ level: 'success', text: '已保存 MCP 服务器配置' })
    } catch (saveError) {
      setError(`保存失败：${errorMessage(saveError)}`)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!selected) return
    setBusy(true)
    try {
      await invoke('settings.mcp.delete', { name: selected })
      setConfirmDelete(false)
      await load()
      pushToast({ level: 'success', text: `已删除 MCP 服务器「${selected}」` })
    } catch (deleteError) {
      setError(`删除失败：${errorMessage(deleteError)}`)
    } finally {
      setBusy(false)
    }
  }

  const test = async () => {
    if (!draft || testing) return
    const environment = parseKeyValueLines(envText)
    const headers = parseKeyValueLines(headersText)
    if (!environment || !headers) {
      setError('环境变量/请求头格式应为每行 KEY=VALUE')
      return
    }

    setTesting(true)
    setError(null)
    setTools(null)
    try {
      const result = await invoke<{ tools: McpToolDto[] }>('settings.mcp.test', {
        ...draft,
        arguments: splitLines(argsText),
        environment,
        headers,
      })
      setTools(result.tools)
      pushToast({
        level: 'success',
        text: result.tools.length === 0 ? '连接成功，但没有可用工具' : `连接成功，共 ${result.tools.length} 个工具`,
      })
    } catch (testError) {
      setError(`连接失败：${errorMessage(testError)}`)
    } finally {
      setTesting(false)
    }
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />
      {data?.error ? <SettingsError message={data.error} /> : null}

      <div className="settings-split">
        <div className="settings-list">
          <div className="settings-list-head">
            <span>服务器</span>
            <button type="button" className="btn btn-sm btn-ghost" onClick={startNew}>
              <Plus size={13} /> 新增
            </button>
          </div>
          {data?.servers.length === 0 ? (
            <SettingsEmpty>还没有 MCP 服务器；添加后它的工具会交给桌宠调用。</SettingsEmpty>
          ) : (
            data?.servers.map((server) => (
              <button
                key={server.name}
                type="button"
                className="settings-list-item"
                data-active={(server.name === selected).toString()}
                onClick={() => {
                  setCreating(false)
                  setSelected(server.name)
                }}
              >
                <span className="settings-list-title">{server.name}</span>
                <span className="settings-list-desc">
                  {server.enabled ? '已启用' : '已停用'} · {server.transport}
                </span>
              </button>
            ))
          )}
          {data?.path ? <div className="settings-footnote">配置文件：{data.path}</div> : null}
        </div>

        <div className="settings-editor">
          {!draft ? (
            <SettingsEmpty>选择左侧服务器，或新增一个。</SettingsEmpty>
          ) : (
            <SettingsCard
              title={originalName ? `编辑「${originalName}」` : '新建 MCP 服务器'}
              desc="Stdio 走本地子进程，Http 走远程 Streamable HTTP / SSE。"
              actions={
                originalName ? (
                  <button type="button" className="btn btn-sm btn-danger" onClick={() => setConfirmDelete(true)}>
                    <Trash2 size={13} /> 删除
                  </button>
                ) : null
              }
            >
              <div className="settings-form">
                <div className="settings-grid-2">
                  <div className="field">
                    <label className="field-label" htmlFor="mcp-name">名称</label>
                    <input
                      id="mcp-name"
                      className="input"
                      value={draft.name}
                      onChange={(event) => setDraft({ ...draft, name: event.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label className="field-label" htmlFor="mcp-transport">传输方式</label>
                    <select
                      id="mcp-transport"
                      className="select"
                      value={draft.transport}
                      onChange={(event) =>
                        setDraft({ ...draft, transport: event.target.value === 'http' ? 'http' : 'stdio' })
                      }
                    >
                      <option value="stdio">Stdio</option>
                      <option value="http">Http</option>
                    </select>
                  </div>
                </div>

                <Toggle
                  checked={draft.enabled}
                  onChange={(enabled) => setDraft({ ...draft, enabled })}
                  label="启用该服务器"
                />

                {draft.transport === 'stdio' ? (
                  <>
                    <div className="field">
                      <label className="field-label" htmlFor="mcp-command">启动命令</label>
                      <input
                        id="mcp-command"
                        className="input"
                        placeholder="npx / python / 可执行文件路径"
                        value={draft.command}
                        onChange={(event) => setDraft({ ...draft, command: event.target.value })}
                      />
                    </div>
                    <div className="field">
                      <label className="field-label" htmlFor="mcp-args">参数（每行一个）</label>
                      <textarea
                        id="mcp-args"
                        className="textarea"
                        rows={3}
                        value={argsText}
                        onChange={(event) => setArgsText(event.target.value)}
                      />
                    </div>
                    <div className="field">
                      <label className="field-label" htmlFor="mcp-cwd">工作目录（可选）</label>
                      <input
                        id="mcp-cwd"
                        className="input"
                        value={draft.workingDirectory}
                        onChange={(event) => setDraft({ ...draft, workingDirectory: event.target.value })}
                      />
                    </div>
                    <div className="field">
                      <label className="field-label" htmlFor="mcp-env">环境变量（每行 KEY=VALUE）</label>
                      <textarea
                        id="mcp-env"
                        className="textarea"
                        rows={3}
                        value={envText}
                        onChange={(event) => setEnvText(event.target.value)}
                      />
                    </div>
                  </>
                ) : (
                  <>
                    <div className="field">
                      <label className="field-label" htmlFor="mcp-url">服务地址</label>
                      <input
                        id="mcp-url"
                        className="input"
                        placeholder="http://localhost:3000/mcp"
                        value={draft.url}
                        onChange={(event) => setDraft({ ...draft, url: event.target.value })}
                      />
                    </div>
                    <div className="field">
                      <label className="field-label" htmlFor="mcp-headers">请求头（每行 KEY=VALUE）</label>
                      <textarea
                        id="mcp-headers"
                        className="textarea"
                        rows={3}
                        value={headersText}
                        onChange={(event) => setHeadersText(event.target.value)}
                      />
                    </div>
                  </>
                )}

                <div className="field" style={{ maxWidth: 200 }}>
                  <label className="field-label" htmlFor="mcp-timeout">超时（秒）</label>
                  <input
                    id="mcp-timeout"
                    className="input"
                    type="number"
                    min={1}
                    max={600}
                    value={draft.timeoutSeconds}
                    onChange={(event) =>
                      setDraft({ ...draft, timeoutSeconds: Number(event.target.value) || 30 })
                    }
                  />
                </div>

                <div className="settings-actions">
                  <button type="button" className="btn btn-ghost" disabled={testing} onClick={() => void test()}>
                    <RefreshCw size={13} /> {testing ? '连接中…' : '测试连接'}
                  </button>
                  <button type="button" className="btn btn-primary" disabled={busy} onClick={() => void save()}>
                    {busy ? '保存中…' : '保存配置'}
                  </button>
                </div>

                {tools ? (
                  <div className="settings-tools">
                    <div className="settings-subhead">
                      <span>可用工具（{tools.length}）</span>
                    </div>
                    {tools.length === 0 ? (
                      <div className="field-hint">连接成功，但服务器没有提供工具。</div>
                    ) : (
                      tools.map((tool) => (
                        <div className="settings-tool-row" key={tool.name}>
                          <span className="settings-tool-name">{tool.name}</span>
                          <span className="settings-tool-desc">{tool.description}</span>
                        </div>
                      ))
                    )}
                  </div>
                ) : null}
              </div>
            </SettingsCard>
          )}
        </div>
      </div>

      <Dialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title="删除 MCP 服务器"
        description={`「${selected ?? ''}」会从 mcp.json 移除，它的工具下一次对话不再可用。`}
      >
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setConfirmDelete(false)}>
            取消
          </button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={() => void remove()}>
            删除
          </button>
        </div>
      </Dialog>
    </div>
  )
}
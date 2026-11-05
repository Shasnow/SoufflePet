// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.Models;

/// <summary>
/// 待发送的附件：文件与文件夹只记录本地绝对路径，发送时以「@路径」附加到消息里，
/// 文件本身不必反复上传，模型可随时按路径自行读取。
/// 粘贴的图片先以 PNG 字节留在内存（<see cref="FullPath"/> 为空），真正发送时才落盘换成路径。
/// </summary>
public sealed record ChatAttachment(string FileName, string FullPath, byte[]? Data = null)
{
    /// <summary>是否还在内存里（粘贴的图片），发送时才转储成文件。</summary>
    public bool IsPending => Data is not null;

    /// <summary>附件标签的悬停提示：文件显示路径，内存里的图片说明发送时才保存。</summary>
    public string Location => IsPending ? "发送时保存到本地" : FullPath;
}

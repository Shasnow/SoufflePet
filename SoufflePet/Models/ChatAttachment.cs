// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.Models;

/// <summary>
/// 待发送的附件：只记录本地绝对路径，发送时以「@路径」附加到消息里，
/// 文件本身不必反复上传，模型可随时按路径自行读取。
/// </summary>
public sealed record ChatAttachment(string FileName, string FullPath);

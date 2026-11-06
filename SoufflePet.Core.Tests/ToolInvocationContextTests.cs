// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Threading.Tasks;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>工具调用上下文：callId 必须随异步流程流动，工具记录差异时才能取到。</summary>
public sealed class ToolInvocationContextTests
{
    [Fact]
    public async Task FlowsThroughAsyncCallsAndRestores()
    {
        Assert.Equal(string.Empty, ToolInvocationContext.CurrentCallId);

        using (ToolInvocationContext.Enter("call-1"))
        {
            await Task.Yield();
            Assert.Equal("call-1", ToolInvocationContext.CurrentCallId);

            await Task.Run(() => Assert.Equal("call-1", ToolInvocationContext.CurrentCallId));

            using (ToolInvocationContext.Enter("call-2"))
            {
                Assert.Equal("call-2", ToolInvocationContext.CurrentCallId);
            }

            Assert.Equal("call-1", ToolInvocationContext.CurrentCallId);
        }

        Assert.Equal(string.Empty, ToolInvocationContext.CurrentCallId);
    }

    [Fact]
    public async Task BackupDirectoryFlowsThroughAsyncCalls()
    {
        Assert.Null(ToolInvocationContext.BackupDirectory);

        using (ToolInvocationContext.EnterBackupDirectory(@"C:\tmp\session"))
        {
            await Task.Yield();
            Assert.Equal(@"C:\tmp\session", ToolInvocationContext.BackupDirectory);
            await Task.Run(() => Assert.Equal(@"C:\tmp\session", ToolInvocationContext.BackupDirectory));
        }

        Assert.Null(ToolInvocationContext.BackupDirectory);
    }
}
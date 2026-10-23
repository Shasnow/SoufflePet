// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using Avalonia;
using Serilog;
using Serilog.Filters;

namespace SoufflePet;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Serilog：DEBUG 级别，按天滚动写入 logs/pet-YYYYMMDD.log（保留 7 天）。
        // 完整 LLM 请求/响应不走 Serilog，而是覆盖写入 logs/llm/latest.json（见 LlmChatService）。
        // DEBUG 构建额外输出到控制台。日志模板带 SourceContext（类名）便于定位。
        const string template = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "pet-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: template);
#if DEBUG
        // FileOnly 标记的日志（如完整请求体）体积大，只写文件，不打到控制台。
        // 过滤器必须放在子 logger 里，直接挂在 LoggerConfiguration 上会连文件 sink 一起过滤掉。
        loggerConfig = loggerConfig.WriteTo.Logger(console => console
            .Filter.ByExcluding(Matching.WithProperty<bool>("FileOnly", fileOnly => fileOnly))
            .WriteTo.Console(outputTemplate: template));
#endif
        Log.Logger = loggerConfig.CreateLogger();

        Log.Information("Application started args={Args}", string.Join(',', args));
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception: {Reason}", e.ExceptionObject);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log.Information("Main loop exited");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal exception in Main");
        }
        finally
        {
            Log.CloseAndFlush(); // 确保退出前把日志刷盘
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

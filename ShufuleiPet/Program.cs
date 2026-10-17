using System;
using System.IO;
using Avalonia;
using Serilog;

namespace ShufuleiPet;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Serilog：DEBUG 级别，按天滚动写入 logs/pet-YYYYMMDD.log（保留 7 天）；
        // DEBUG 构建额外输出到控制台。日志模板带 SourceContext（类名）便于定位。
        const string template = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "pet-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: template);
#if DEBUG
        loggerConfig = loggerConfig.WriteTo.Console(outputTemplate: template);
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

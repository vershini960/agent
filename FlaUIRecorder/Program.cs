using FlaUIRecorder.Core.Hooks;
using FlaUIRecorder.UI;
using Serilog;

namespace FlaUIRecorder
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            UIAutomationManager.Initialize();

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .WriteTo.File(
                    "logs/app.log",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}"
                )
                .CreateLogger();

            // 🔥 GLOBAL EXCEPTION HANDLING
            Application.ThreadException += (sender, args) =>
            {
                Log.Error(args.Exception, "UI Thread Exception");
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Log.Fatal(args.ExceptionObject as Exception, "Unhandled Exception");
            };

            // 🔥 CLEAN EXIT
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                GlobalMouseHook.Stop();
            };

            try
            {
                Log.Information("Application Starting");

                ApplicationConfiguration.Initialize();
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application crashed");
            }
            finally
            {
                UIAutomationManager.Dispose(); // 🔥 IMPORTANT
                Log.CloseAndFlush();
            }
        }
    }
}
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Microsoft.UI.Xaml;
using System;
using WinRT;

namespace Galbox.App;

/// <summary>
/// Program entry point with Bootstrap initialization for self-contained deployment.
/// </summary>
public static class Program
{
    [System.STAThreadAttribute]
    static int Main(string[] args)
    {
        // TEMPORARY DIAGNOSTIC (verification worktree only): dump every first-chance managed
        // exception with its stack to %LocalAppData%\Galbox\logs\firstchance.log. WinUI reports
        // native failures as stowed exceptions (0xC000027B) with no managed stack, so this is the
        // only way to see which managed call site is actually failing.
        if (Environment.GetEnvironmentVariable("GALBOX_FIRSTCHANCE_TRACE") == "1")
        {
            var tracePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Galbox", "logs", "firstchance.log");
            Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try
                {
                    File.AppendAllText(tracePath,
                        $"{DateTime.Now:HH:mm:ss.fff} {e.Exception.GetType().FullName}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n");
                }
                catch { }
            };
        }

        // Windows App SDK bootstrap.
        //
        // Framework-dependent builds rely on the bootstrapper to locate the installed
        // Windows App SDK framework package. A SELF-CONTAINED build ships the runtime next to
        // the executable, and calling the bootstrapper there pulls the framework package in
        // as well, mixing two runtime versions - observed as a native crash (0xC000027B) inside
        // Microsoft.UI.Xaml.dll. WINDOWSAPPSDK_SELFCONTAINED is defined by the csproj when
        // WindowsAppSDKSelfContained=true, so the bootstrapper is compiled out entirely.
#if !WINDOWSAPPSDK_SELFCONTAINED
        try
        {
            // For Windows App SDK 1.6, use version 0x00010006
            Bootstrap.Initialize(0x00010006);
            System.Diagnostics.Debug.WriteLine("Bootstrap initialized successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bootstrap initialization failed: {ex.Message}");
            // Continue anyway - might work with installed runtime
        }
#endif

        try
        {
            ComWrappersSupport.InitializeComWrappers();
            Application.Start((p) =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Application startup failed: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                Bootstrap.Shutdown();
            }
            catch
            {
                // Ignore shutdown errors
            }
        }

        return 0;
    }
}
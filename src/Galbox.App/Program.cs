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
        // Initialize Windows App SDK Bootstrap
        // For Windows App SDK 1.6, use version 0x00010006
        try
        {
            // Use the simplest Bootstrap initialization
            Bootstrap.Initialize(0x00010006);
            System.Diagnostics.Debug.WriteLine("Bootstrap initialized successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bootstrap initialization failed: {ex.Message}");
            // Continue anyway - might work with installed runtime
        }

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
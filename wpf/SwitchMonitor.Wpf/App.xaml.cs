using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace SwitchMonitor.Wpf;

public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;
    private CancellationTokenSource? pipeCancellation;
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string instanceName = $"Local\\SwitchMonitor.Wpf.{Process.GetCurrentProcess().SessionId}";
        string pipeName = $"SwitchMonitor.Wpf.{Process.GetCurrentProcess().SessionId}";
        instanceMutex = new Mutex(true, instanceName, out bool firstInstance);
        if (!firstInstance)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            _ = NotifyExistingInstanceAsync(pipeName);
            return;
        }

        pipeCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(pipeName, pipeCancellation.Token);
        mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private async Task NotifyExistingInstanceAsync(string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1800);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync("SHOW_SETTINGS");
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The existing process may still be starting or already exiting.
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task ListenForActivationAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                if (await reader.ReadLineAsync(cancellationToken) == "SHOW_SETTINGS")
                    await Dispatcher.InvokeAsync(() => mainWindow?.RequestShowSettings());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A client may disconnect before sending a full request.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeCancellation?.Cancel();
        pipeCancellation?.Dispose();
        instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

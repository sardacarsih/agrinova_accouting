using System.Windows;
using System.Windows.Threading;
using Accounting.Infrastructure.Logging;
using Accounting.Services;

namespace Accounting;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppServices.InitializeLogger(new FileAppLogger());
        RegisterGlobalExceptionHandlers();
        AppServices.InitializeThemeCoordinator(new AppThemeCoordinator());
        AppServices.ThemeCoordinator.Initialize();
        AppServices.Logger.LogInfo(nameof(App), "Startup", "action=startup status=initialized");
        base.OnStartup(e);
        StartRuntimeMigrationVerification();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Logger.LogInfo(nameof(App), "Exit", $"action=exit code={e.ApplicationExitCode}");
        AppServices.ShutdownThemeCoordinator();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppServices.Logger.LogCritical(
            nameof(App),
            "DispatcherUnhandledException",
            "action=unhandled_exception scope=dispatcher_ui",
            e.Exception);

        MessageBox.Show(
            "Terjadi kesalahan tidak terduga. Silakan cek log aplikasi.",
            "Accounting Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppServices.Logger.LogCritical(
            nameof(App),
            "AppDomainUnhandledException",
            $"action=unhandled_exception scope=app_domain is_terminating={e.IsTerminating}",
            exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppServices.Logger.LogCritical(
            nameof(App),
            "UnobservedTaskException",
            "action=unhandled_exception scope=task_scheduler",
            e.Exception);
        e.SetObserved();
    }

    private void StartRuntimeMigrationVerification()
    {
        _ = VerifyRuntimeMigrationsAsync();
    }

    private static async Task VerifyRuntimeMigrationsAsync()
    {
        try
        {
            var verificationResult = await InventoryApiInvRuntimeMigrationVerifier.VerifyAsync(
                DatabaseAuthOptions.FromConfiguration());
            if (!verificationResult.ShouldWarn)
            {
                return;
            }

            AppServices.Logger.LogWarning(
                nameof(App),
                "RuntimeMigrationVerification",
                $"action=inventory_api_inv_permission_check status=warning details=\"{verificationResult.Message}\"");

            MessageBox.Show(
                $"{verificationResult.Message}{Environment.NewLine}{Environment.NewLine}Required script: database\\backfill_inventory_api_inv_import_actions.sql",
                "Accounting Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppServices.Logger.LogWarning(
                nameof(App),
                "RuntimeMigrationVerificationFailed",
                "action=inventory_api_inv_permission_check status=failed",
                ex);
        }
    }
}



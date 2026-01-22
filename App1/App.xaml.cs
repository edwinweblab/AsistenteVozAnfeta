using Anfeta.UI.Data;
using Anfeta.UI.Services;
using Anfeta.UI.ViewModels;
using Anfeta.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace Anfeta.UI
{
    public partial class App : Application
    {
        private Window? _window;
        private GlobalHotkeyService? _hotkey;
        private FloatingMicButton? _floatingButton;
        private bool _isShuttingDown = false;

        public static Window? MainWindowInstance { get; private set; }
        public static IHost AppHost { get; private set; } = null!;
        public static DispatcherQueue? UIQueue { get; private set; }
        public static HomeViewModel HomeVM => AppHost.Services.GetRequiredService<HomeViewModel>();

        public App()
        {
            InitializeComponent();

            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<AppStateService>();
                    services.AddSingleton<SettingsService>();
                    services.AddSingleton<AudioService>();
                    services.AddSingleton<ISpeechToTextService, SpeechToTextService>();
                    services.AddSingleton<ITextToSpeechService, TextToSpeechService>();
                    services.AddSingleton<GlobalHotkeyService>();
                    services.AddSingleton(new HttpClient
                    {
                        BaseAddress = new Uri(OllamaConfig.BaseUrl),
                        Timeout = TimeSpan.FromMinutes(3)
                    });
                    services.AddSingleton<IOllamaHealthService, OllamaHealthService>();
                    services.AddSingleton<ICommandInterpretationService>(sp =>
                    {
                        var http = sp.GetRequiredService<HttpClient>();
                        return new OllamaInterpretationService(http, OllamaConfig.ModelName);
                    });
                    services.AddSingleton<HomeViewModel>();
                })
                .Build();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Debug.WriteLine("APP INICIADA");
            DatabaseInitializer.InitializeDatabase();

#if DEBUG
            TestDatabaseConnection();
#endif

            _window = new MainWindow();
            MainWindowInstance = _window;
            UIQueue = DispatcherQueue.GetForCurrentThread();

            _ = HomeVM;
            _ = CheckAndWarmupOllamaAsync();

            _hotkey = AppHost.Services.GetRequiredService<GlobalHotkeyService>();
            _hotkey.Start();
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            _hotkey.RegistrationFailed += Hotkey_RegistrationFailed;

            ((MainWindow)_window).SizeChanged += Window_SizeChanged;

            HomeVM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(HomeViewModel.IsListening))
                    _floatingButton?.SetListeningState(HomeVM.IsListening);
            };

            _window.Activate();
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            var appWindow = AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(_window)
                )
            );

            if (appWindow?.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                    ShowFloatingButton();
                else
                    HideFloatingButton();
            }
        }

        private void Hotkey_HotkeyPressed(object? sender, EventArgs e)
        {
            Debug.WriteLine("[HOTKEY] Detectado -> mostrar flotante + UI thread");
            ShowFloatingButton();

            UIQueue?.TryEnqueue(async () =>
            {
                BringMainWindowToFront();
                await HomeVM.TriggerVoiceFromHotkeyAsync();
            });
        }

        private void ShowFloatingButton()
        {
            UIQueue?.TryEnqueue(() =>
            {
                if (_floatingButton == null)
                {
                    _floatingButton = new FloatingMicButton();
                    _floatingButton.OpenAppRequested += (s, e) => BringMainWindowToFront();
                    _floatingButton.ExitRequested += (s, e) => CleanupAndExit();
                    _floatingButton.VoiceActivationRequested += async (s, e) =>
                    {
                        await HomeVM.ListenOnceCommand.ExecuteAsync(null);
                    };

                    var appState = AppHost.Services.GetRequiredService<AppStateService>();
                    _floatingButton.UpdateHotkeyDisplay(appState.GetHotkeyDisplayString());
                }
                _floatingButton.Activate();
            });
        }

        private void HideFloatingButton()
        {
            UIQueue?.TryEnqueue(() =>
            {
                try
                {
                    if (_floatingButton != null)
                    {
                        _floatingButton.Close();
                        _floatingButton = null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[APP] Error ocultando flotante: {ex.Message}");
                }
            });
        }

        private void BringMainWindowToFront()
        {
            if (_window != null)
            {
                var appWindow = AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                        WinRT.Interop.WindowNative.GetWindowHandle(_window)
                    )
                );
                appWindow?.Show(true);
            }
        }

        /// <summary>Limpia componentes sin cerrar ventana (llamado por MainWindow_Closed)</summary>
        public void CleanupComponents()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            Debug.WriteLine("[APP] Limpiando componentes...");

            try
            {
                _hotkey?.Stop();
                _hotkey?.Dispose();
                _hotkey = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error hotkey: {ex.Message}");
            }

            try
            {
                if (_floatingButton != null)
                {
                    _floatingButton.Close();
                    _floatingButton = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error flotante: {ex.Message}");
            }

            Application.Current.Exit();
        }

        /// <summary>Cierre completo (llamado por FloatingMicButton.ExitRequested)</summary>
        public void CleanupAndExit()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            Debug.WriteLine("[APP] Cierre completo...");

            try
            {
                _hotkey?.Stop();
                _hotkey?.Dispose();
                _hotkey = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error hotkey: {ex.Message}");
            }

            try
            {
                if (_floatingButton != null)
                {
                    _floatingButton.Close();
                    _floatingButton = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error flotante: {ex.Message}");
            }

            try
            {
                _window?.Close();
                _window = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] Error ventana: {ex.Message}");
            }

            Application.Current.Exit();
        }

        private void Hotkey_RegistrationFailed(object? sender, string message)
        {
            UIQueue?.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Error al configurar atajo",
                    Content = message,
                    CloseButtonText = "Entendido",
                    XamlRoot = _window?.Content?.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private async Task CheckAndWarmupOllamaAsync()
        {
            try
            {
                using var quick = new HttpClient
                {
                    BaseAddress = new Uri(OllamaConfig.BaseUrl),
                    Timeout = TimeSpan.FromSeconds(5)
                };

                var res = await quick.GetAsync("/api/tags");
                Debug.WriteLine($"OLLAMA STATUS: {(int)res.StatusCode}");

                if (!res.IsSuccessStatusCode)
                {
                    Debug.WriteLine("OLLAMA NO RESPONDE OK.");
                    return;
                }

                var interpreter = AppHost.Services.GetRequiredService<ICommandInterpretationService>();
                await interpreter.InterpretRawAsync("ping");

                Debug.WriteLine("OLLAMA WARMUP OK");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OLLAMA CHECK ERROR: " + ex.Message);
            }
        }

        private void TestDatabaseConnection()
        {
            try
            {
                using var connection = DbConnectionFactory.Create();
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                var result = command.ExecuteScalar();
                Debug.WriteLine("SQLITE OK: " + result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SQLITE ERROR: " + ex.Message);
            }
        }
    }
}
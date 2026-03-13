using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Anfeta.UI.Services
{
    public sealed class FilePickerService : IFilePickerService
    {
        public async Task<string?> PickExePathAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };

            picker.FileTypeFilter.Add(".exe");

            // WinUI 3: hay que inicializar el picker con el HWND
            var window = App.MainWindowInstance
                ?? throw new InvalidOperationException("MainWindowInstance no está disponible.");

            var hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        public async Task<string?> PickCsvFileAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };

            picker.FileTypeFilter.Add(".csv");

            var window = App.MainWindowInstance
                ?? throw new InvalidOperationException("MainWindowInstance no está disponible.");

            var hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
    }
}
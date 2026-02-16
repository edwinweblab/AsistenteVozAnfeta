using System;

namespace Anfeta.UI.Services.Search
{
    public static class DropboxIndexCoordinator
    {
        public static event Action? StateChanged;

        public static bool IsIndexing { get; private set; }
        public static bool IsReady { get; private set; }
        public static string? RootPath { get; private set; }
        public static string? LastError { get; private set; }

        // Útil para que Search detecte “cambios” aunque sea el mismo root
        public static long Version { get; private set; }

        public static void StartIndexing(string rootPath)
        {
            RootPath = rootPath;
            IsIndexing = true;
            IsReady = false;
            LastError = null;
            Version++;
            StateChanged?.Invoke();
        }

        public static void MarkReady(string rootPath)
        {
            RootPath = rootPath;
            IsIndexing = false;
            IsReady = true;
            LastError = null;
            Version++;
            StateChanged?.Invoke();
        }

        public static void MarkError(string rootPath, string error)
        {
            RootPath = rootPath;
            IsIndexing = false;
            IsReady = false;
            LastError = error;
            Version++;
            StateChanged?.Invoke();
        }

        public static void Reset()
        {
            RootPath = null;
            IsIndexing = false;
            IsReady = false;
            LastError = null;
            Version++;
            StateChanged?.Invoke();
        }
    }
}

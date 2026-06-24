using System;

namespace Anfeta.UI.Services.Search
{
    public static class SearchFocusBridge
    {
        public static event Action? FocusRequested;

        public static void RequestFocus()
        {
            FocusRequested?.Invoke();
        }
    }
}
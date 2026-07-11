namespace Anfeta.UI.Models
{
    public sealed record DropboxConnectionInfo(
        bool IsConnected,
        string DisplayName,
        string Email,
        string AccountId,
        string Message
    )
    {
        public static DropboxConnectionInfo Disconnected(string message = "Dropbox no vinculado.")
            => new(false, string.Empty, string.Empty, string.Empty, message);
    }
}

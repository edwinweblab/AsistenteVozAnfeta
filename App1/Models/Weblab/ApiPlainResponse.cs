// Models/ApiPlainResponse.cs
namespace Anfeta.UI.Models.Weblab
{
    public sealed class ApiPlainResponse
    {
        public bool Ok { get; init; }
        public string PlainText { get; init; } = "";
    }
}

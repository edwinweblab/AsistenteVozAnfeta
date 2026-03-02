using System.Threading.Tasks;

namespace Anfeta.UI.Services.Search;

public interface ISearchCommandSink
{
    Task ExecuteSearchTextAsync(string text);
}
using System.Threading.Tasks;

namespace Anfeta.UI.Services
{
    public interface IFilePickerService
    {
        Task<string?> PickExePathAsync();

    }

}
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Blackbird.Services;

public interface ITemplateService
{
    Task<string> CreateFromTemplateAsync(string name, string templateName);
    Task<string> CreateModAsync(string name, IEnumerable<string> zoneNames);
}

using System.Threading;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public interface IProjectAnalysisService
{
    Task<ProjectAnalysisResult> AnalyzeAsync(ProjectItem project, CancellationToken cancellationToken = default);
    Task<ProjectAnalysisResult> CleanDuplicateGdtAssetsAsync(ProjectItem project);
}

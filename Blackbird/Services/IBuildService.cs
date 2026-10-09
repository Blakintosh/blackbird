using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Blackbird.Models;

namespace Blackbird.Services;

public interface IBuildService
{
    Task<bool> RunBuildAsync(
        List<BuildCommand> commands,
        bool ignoreErrors,
        IProgress<string> progress,
        CancellationToken ct);
}

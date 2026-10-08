using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Http;

// STUB: owned by the HTTP providers module agent.
public sealed class ModelCatalog : IModelCatalog
{
    public ModelCatalog(HttpClient? http = null) { }
    public Task<ModelListResult> ListModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct) => throw new NotImplementedException("STUB");
}

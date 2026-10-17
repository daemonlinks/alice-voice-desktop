using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.Inference;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInferenceServices(this IServiceCollection services)
    {
        services.AddSingleton<IWhisperEngine, OnnxWhisperEngine>();
        return services;
    }
}

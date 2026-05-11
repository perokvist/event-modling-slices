namespace SampleApp.Modules;

public static class ModuleAspNetExtensions
{
    public static WebApplication UseModuleEndpoints<TModule>(
       this WebApplication app, Func<WebApplication, RouteGroupBuilder> routeGroup)
       where TModule : class, IModule
    {
        var register = "MapEndpoints";
        var t = typeof(TModule);
        var moduleNamespace = t.Namespace;
        var endpointTypes = t.Assembly
            .GetTypes()
            .Where(type => type.Namespace is not null)
            .Where(type => moduleNamespace is null || type.Namespace!.StartsWith(moduleNamespace))
            .Where(type => type.IsClass)
            .Where(type => type.GetMethod(register) != null);

        var rg = routeGroup(app);
        foreach (var type in endpointTypes)
        {
            var method = type.GetMethod(register);
            method?.Invoke(null, [rg]); //TODO pretty ex
        }
        return app;

    }

    public static IServiceCollection AddModule<T>(this IServiceCollection services)
        where T : class, IModule
    {
        services.AddScoped<T>();
        services.AddScoped<IModule>(sp => sp.GetRequiredService<T>());
        return services;
    }

    public static IServiceCollection AddModule<T>(this IServiceCollection services, T instance)
       where T : class, IModule
    {
        services.AddScoped(sp => instance);
        services.AddScoped<IModule>(sp => sp.GetRequiredService<T>());
        return services;
    }
}

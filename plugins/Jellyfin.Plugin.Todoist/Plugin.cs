using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Todoist;

public sealed class Configuration : BasePluginConfiguration
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public string StatePath { get; }

    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        Instance = this;
        StatePath = Path.Combine(paths.DataPath, "todoist", "state.json");
    }

    public override string Name => "Todoist";
    public override Guid Id => Guid.Parse("6c5e78d3-d5ac-433b-b2ec-05bb05096dba");
    public override string Description => "Synchronize episodes with each user's Todoist project.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "todoist",
            DisplayName = "Todoist",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "checklist",
            EmbeddedResourcePath = GetType().Namespace + ".Web.config.html"
        }
    };
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient("Todoist", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<TodoistApi>();
        services.AddSingleton<TodoistService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TodoistService>());
    }
}

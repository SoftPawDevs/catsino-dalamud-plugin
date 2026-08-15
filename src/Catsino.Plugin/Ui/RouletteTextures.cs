using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Catsino.Plugin.Ui;

// Resolves the embedded roulette art (Assets/Roulette/*.png) to ImGui texture handles via the Dalamud
// texture provider, which caches per frame. Mirrors CardTextures.
public sealed class RouletteTextures
{
    private readonly ITextureProvider textureProvider;
    private readonly Assembly assembly = typeof(RouletteTextures).Assembly;
    private readonly Dictionary<string, string> resourceByName = new(StringComparer.OrdinalIgnoreCase);

    public RouletteTextures(ITextureProvider textureProvider)
    {
        this.textureProvider = textureProvider;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(".Roulette.", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var withoutExtension = name[..^4];
            resourceByName[withoutExtension[(withoutExtension.LastIndexOf('.') + 1)..]] = name;
        }
    }

    // The stationary wooden frame the wheel sits in (600x600).
    public ImTextureID? Board => Handle("roulette_base");

    // The numbered disc (452x452 on that 600px board).
    public ImTextureID? Wheel => Handle("roulette_wheel");

    // The ball on its own (20x20), drawn at whichever pocket the round landed in.
    public ImTextureID? Ball => Handle("roulette_pill2");

    private ImTextureID? Handle(string name)
    {
        if (!resourceByName.TryGetValue(name, out var resource))
        {
            return null;
        }

        return textureProvider.GetFromManifestResource(assembly, resource).GetWrapOrDefault()?.Handle;
    }
}

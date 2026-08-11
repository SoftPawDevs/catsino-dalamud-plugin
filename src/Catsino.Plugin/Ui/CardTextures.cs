using System.Reflection;
using Catsino.Plugin.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Catsino.Plugin.Ui;

// Resolves the embedded card PNGs (Assets/Cards/*.png) to ImGui texture handles via the Dalamud texture
// provider, which caches per frame. Card codes are [Rank][Suit], rank 0 = Ten, plus "CardBack".
public sealed class CardTextures
{
    public const float Width = 86f;
    public const float Height = 120f;

    private readonly ITextureProvider textureProvider;
    private readonly Assembly assembly = typeof(CardTextures).Assembly;
    private readonly Dictionary<string, string> resourceByCode = new(StringComparer.OrdinalIgnoreCase);

    public CardTextures(ITextureProvider textureProvider)
    {
        this.textureProvider = textureProvider;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var withoutExtension = name[..^4];
            var code = withoutExtension[(withoutExtension.LastIndexOf('.') + 1)..];
            resourceByCode[code] = name;
        }
    }

    // ImGui texture id for a card, or null if the resource is missing / not yet uploaded (caller draws a fallback).
    public ImTextureID? Handle(BlackjackCardDto card) => Handle($"{RankCode(card.Rank)}{SuitCode(card.Suit)}");

    public ImTextureID? Back => Handle("CardBack");

    public ImTextureID? Handle(string code)
    {
        if (!resourceByCode.TryGetValue(code, out var resource))
        {
            return null;
        }

        var wrap = textureProvider.GetFromManifestResource(assembly, resource).GetWrapOrDefault();
        return wrap?.Handle;
    }

    public static string RankCode(int rank) => rank switch { 1 => "A", 10 => "0", 11 => "J", 12 => "Q", 13 => "K", _ => rank.ToString() };
    public static string SuitCode(int suit) => suit switch { 0 => "C", 1 => "D", 2 => "H", _ => "S" };
}

using System.Numerics;
using Dalamud.Interface.Windowing;

namespace Catsino.Plugin.Ui;

public sealed class SessionWindow(Guid sessionId, SessionPanelRenderer renderer, Action<Guid> closed)
    : Window($"Catsino Session###CatsinoSession-{sessionId:D}")
{
    public Guid SessionId { get; } = sessionId;

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw() => renderer.Draw(SessionId);

    public override void OnClose() => closed(SessionId);
}

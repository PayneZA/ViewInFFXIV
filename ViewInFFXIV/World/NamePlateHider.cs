using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ViewInFFXIV.World;

/// <summary>
/// "Hide name plates while watching": while a video is on the wall TV in your zone, every name plate and item marker
/// (yours, other players, pets, minions, NPCs, enemies, housing items) is hidden by hiding the game's NamePlate
/// window, and shown again afterwards. Chat bubbles and the HUD are other windows and stay.
/// </summary>
/// <remarks>
/// Only the window's root node in memory changes, never the character's name plate settings, so a crash or a closed
/// game cannot leave anything hidden: the next start draws them as usual. The root node is used rather than the
/// window's own visibility so the game's own hiding (cutscenes, hide UI, loading) keeps working, and coming back
/// never shows the window when the game wants it hidden. Applied every tick, which also covers a window the game
/// re-creates on a zone change.
/// </remarks>
public sealed unsafe class NamePlateHider : IDisposable
{
    /// <summary>Seconds a video must be on the TV before name plates go, and gone before they come back.</summary>
    private const double HideAfter = 1.0;

    private const double RestoreAfter = 3.0;

    private readonly Configuration config;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime wantedSince = DateTime.MaxValue;
    private DateTime unwantedSince = DateTime.MaxValue;

    public NamePlateHider(Configuration config, IGameGui gameGui, IPluginLog log)
    {
        this.config = config;
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Name plates are hidden by us right now.</summary>
    public bool Hidden { get; private set; }

    private AtkResNode* Root
    {
        get
        {
            var addon = (AtkUnitBase*)gameGui.GetAddonByName("NamePlate").Address;
            return addon != null ? addon->RootNode : null;
        }
    }

    /// <summary>Call every framework tick with whether a video is on the wall TV in this zone.</summary>
    public void Tick(bool watching)
    {
        var now = DateTime.UtcNow;
        if (watching && config.HideNamePlatesWhileWatching)
        {
            unwantedSince = DateTime.MaxValue;
            if (wantedSince == DateTime.MaxValue)
                wantedSince = now;
            if ((now - wantedSince).TotalSeconds >= HideAfter)
                Hide();
        }
        else
        {
            wantedSince = DateTime.MaxValue;
            if (unwantedSince == DateTime.MaxValue)
                unwantedSince = now;
            if (Hidden && (now - unwantedSince).TotalSeconds >= RestoreAfter)
                Restore();
        }
    }

    /// <summary>Shows the name plates again now (plugin unloading, option switched off).</summary>
    public void Restore()
    {
        if (!Hidden)
            return;

        Hidden = false;
        var root = Root;
        if (root != null)
            root->ToggleVisibility(true);
        log.Information("Name plates: shown again");
    }

    public void Dispose() => Restore();

    /// <summary>Current state, for /matinee nameplates.</summary>
    public string Describe()
    {
        var root = Root;
        var shown = root == null ? "no window" : root->IsVisible() ? "shown" : "hidden";
        return $"Name plates: {shown} · hidden by Matinee {Hidden}";
    }

    private void Hide()
    {
        var root = Root;
        if (root == null)
            return;

        if (root->IsVisible())
            root->ToggleVisibility(false);
        if (!Hidden)
            log.Information("Name plates: hidden while watching");
        Hidden = true;
    }
}

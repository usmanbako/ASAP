using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using ECommons.Configuration;
using ECommons.ImGuiMethods;
using Splatoon.SplatoonScripting;

namespace SplatoonScriptsOfficial.Duties.Dawntrail.Dancing_Mad;

// Graven Image 1 "Mystery Magic" fire resolution helper.
// Tells you whether the fire mechanic resolves as STACK or SPREAD (handling the
// "?" inversion) and which side to go to based on your role (DPS east, supports west).
internal class P1_Graven1_StackSpread : SplatoonScript
{
    #region Metadata

    public override Metadata? Metadata => new(2, "ASAP");
    public override HashSet<uint>? ValidTerritories => [TerritoryDmad];

    #endregion

    #region Constant

    private const uint TerritoryDmad = 1363;

    private const uint CastGravenImage = 48370;     // Each Graven Image; counted to scope to image 1.
    private const uint CastMysteryMagic = 47764;    // The Graven 1 fire stack/spread mystery cast.
    private const uint CastUltimateEmbrace = 49740; // Enrage; used as a reset signal.

    // Boss "true" vs "?" telegraph, and player spread/stack telegraph (shared with Graven 3).
    private const string VfxKefkaTrue = "vfx/lockon/eff/m0462trg_c02c.avfx";
    private const string VfxKefkaFalse = "vfx/lockon/eff/m0462trg_c01c.avfx";
    private const string VfxPlayerSpread = "vfx/lockon/eff/m0462trg_a0c.avfx";
    private const string VfxPlayerStack = "vfx/lockon/eff/m0462trg_b0c.avfx";

    private const string ElLabel = "G1Label";
    private const string ElDestination = "G1Destination";
    private const string ElPreviewEast = "G1PreviewEast";
    private const string ElPreviewWest = "G1PreviewWest";

    // Head-up label that rides on the player.
    private const string JsonLabel =
        """{"Name":"","type":1,"Enabled":false,"radius":0.0,"overlayTextColor":4244635647,"overlayVOffset":4.0,"overlayFScale":2.0,"overlayText":"","refActorType":1}""";

    // Fixed-position destination circle; position/colour/tether are set at runtime.
    private const string JsonDestination =
        """{"Name":"","Enabled":false,"refX":108.0,"refY":100.0,"radius":2.0,"Donut":0.2,"Filled":true,"fillIntensity":0.5,"thicc":5.0,"FillStep":0.5,"tether":true}""";

    // Preview circles ({0}=x, {1}=z, {2}=label).
    private const string PreviewTemplate =
        """{{"Name":"","refX":{0},"refY":{1},"radius":2.0,"Donut":0.2,"color":3355443455,"Filled":true,"fillIntensity":0.35,"overlayBGColor":1879048192,"overlayTextColor":3372220415,"overlayVOffset":2.0,"thicc":4.0,"overlayText":"{2}"}}""";

    #endregion

    #region Config

    private Config C => Controller.GetConfig<Config>();

    #endregion

    #region State

    private int _gravenImageCount;
    private bool _active;
    private KefkaVfx _kefkaVfx = KefkaVfx.None;
    private PlayerVfx _playerVfx = PlayerVfx.None;
    private Solution _solution = Solution.None;

    // Debug aid: every distinct m0462trg lockon code seen this pull, so the real telegraph codes
    // can be read from the settings window even before detection is wired up correctly.
    private readonly List<string> _seenLockonVfx = [];

    #endregion

    #region Private Class

    private enum PartyRole
    {
        T1,
        T2,
        H1,
        H2,
        M1,
        M2,
        R1,
        R2,
    }

    private enum RoleGroup
    {
        Dps,
        Support,
    }

    private enum Side
    {
        East,
        West,
    }

    private enum KefkaVfx
    {
        None,
        True,
        False,
    }

    private enum PlayerVfx
    {
        None,
        Spread,
        Stack,
    }

    private enum Solution
    {
        None,
        Stack,
        Spread,
    }

    private sealed class Config : IEzConfig
    {
        public PartyRole Role = PartyRole.R2;
        public bool SwapSides;                 // Mirror DPS/Support east-west if your strat flips it.
        public float EastAnchorX = 108f;
        public float EastAnchorZ = 100f;
        public float WestAnchorX = 92f;
        public float WestAnchorZ = 100f;
        public bool IgnoreKefkaVfx;            // Safety: treat every telegraph as "true".
        public bool ShowPreview;
    }

    #endregion

    #region LifeCycle

    public override void OnSetup()
    {
        Controller.RegisterElementFromCode(ElLabel, JsonLabel, overwrite: true);
        Controller.RegisterElementFromCode(ElDestination, JsonDestination, overwrite: true);
        RegisterPreview(ElPreviewEast, C.EastAnchorX, C.EastAnchorZ, "DPS");
        RegisterPreview(ElPreviewWest, C.WestAnchorX, C.WestAnchorZ, "SUP");
    }

    public override void OnReset() => ResetState();

    public override void OnStartingCast(uint source, uint castId)
    {
        switch(castId)
        {
            case CastUltimateEmbrace:
                ResetState();
                break;
            case CastGravenImage:
                _gravenImageCount++;
                ClearActive();
                break;
            case CastMysteryMagic:
                _active = true;
                break;
        }
    }

    public override void OnVFXSpawn(uint target, string vfxPath)
    {
        RecordLockonVfx(vfxPath);

        if(!_active) return;

        if(_playerVfx == PlayerVfx.None && TryMapPlayerVfx(vfxPath, out var playerVfx))
            _playerVfx = playerVfx;

        if(_kefkaVfx == KefkaVfx.None && TryMapKefkaVfx(vfxPath, out var kefkaVfx))
            _kefkaVfx = kefkaVfx;
    }

    public override void OnUpdate()
    {
        DisableAllElements();

        if(C.ShowPreview)
        {
            ApplyPreview();
            return;
        }

        if(!_active) return;

        _solution = _kefkaVfx == KefkaVfx.None || _playerVfx == PlayerVfx.None
            ? Solution.None
            : Resolve(_playerVfx, _kefkaVfx);

        if(_solution == Solution.None) return;

        ApplyDisplay(_solution, GetSide(C.Role));
    }

    public override void OnSettingsDraw()
    {
        ImGui.TextDisabled("Configuration");
        ImGui.Separator();
        ImGui.SetNextItemWidth(200f);
        ImGuiEx.EnumCombo("Role", ref C.Role);
        ImGui.Checkbox("Swap sides (DPS west / supports east)", ref C.SwapSides);
        ImGui.Checkbox("Ignore boss telegraph (treat as true)", ref C.IgnoreKefkaVfx);

        ImGui.Spacing();
        ImGui.TextDisabled("Destination anchors (X / Z)");
        ImGui.Separator();
        DrawAnchorInput("East anchor", ref C.EastAnchorX, ref C.EastAnchorZ);
        DrawAnchorInput("West anchor", ref C.WestAnchorX, ref C.WestAnchorZ);

        ImGui.Spacing();
        ImGui.TextDisabled("Preview");
        ImGui.Separator();
        ImGui.Checkbox("Show both anchors for preview", ref C.ShowPreview);

        ImGui.Spacing();
        ImGui.TextDisabled("Debug");
        ImGui.Separator();
        ImGui.TextUnformatted($"Graven image #: {_gravenImageCount}  Active: {_active}");
        ImGui.TextUnformatted($"Boss telegraph: {_kefkaVfx}");
        ImGui.TextUnformatted($"Player telegraph: {_playerVfx}");
        ImGui.TextUnformatted($"Resolution: {_solution}");
        ImGui.TextUnformatted($"Lockon VFX seen: {(_seenLockonVfx.Count == 0 ? "(none)" : string.Join(", ", _seenLockonVfx))}");
    }

    #endregion

    #region Private Method

    // Remember distinct lockon telegraph codes (e.g. m0462trg_c03c.avfx) for diagnostics.
    private void RecordLockonVfx(string vfxPath)
    {
        if(!vfxPath.Contains("m0462trg")) return;

        var code = vfxPath;
        var slash = code.LastIndexOf('/');
        if(slash >= 0 && slash + 1 < code.Length) code = code[(slash + 1)..];

        if(_seenLockonVfx.Contains(code)) return;
        _seenLockonVfx.Add(code);
        if(_seenLockonVfx.Count > 16) _seenLockonVfx.RemoveAt(0);
    }

    private void RegisterPreview(string name, float x, float z, string label)
        => Controller.RegisterElementFromCode(
            name,
            string.Format(CultureInfo.InvariantCulture, PreviewTemplate, x, z, label),
            overwrite: true);

    private static void DrawAnchorInput(string label, ref float x, ref float z)
    {
        ImGui.SetNextItemWidth(90f);
        ImGui.InputFloat($"{label} X", ref x);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputFloat($"{label} Z", ref z);
    }

    // Full reset between pulls.
    private void ResetState()
    {
        _gravenImageCount = 0;
        _seenLockonVfx.Clear();
        ClearActive();
        DisableAllElements();
        Controller.Hide();
    }

    // Clear the per-resolution state without touching the image counter.
    private void ClearActive()
    {
        _active = false;
        _kefkaVfx = KefkaVfx.None;
        _playerVfx = PlayerVfx.None;
        _solution = Solution.None;
    }

    private void DisableAllElements()
    {
        foreach(var element in Controller.GetRegisteredElements().Values)
            element.Enabled = false;
    }

    // Apply the "?" inversion: on a true telegraph the marker is literal, on "?" it flips.
    private Solution Resolve(PlayerVfx player, KefkaVfx kefka)
    {
        if(C.IgnoreKefkaVfx) kefka = KefkaVfx.True;

        if(player == PlayerVfx.Spread)
            return kefka == KefkaVfx.True ? Solution.Spread : Solution.Stack;

        return kefka == KefkaVfx.True ? Solution.Stack : Solution.Spread;
    }

    private Side GetSide(PartyRole role)
    {
        var dpsSide = C.SwapSides ? Side.West : Side.East;
        var supportSide = C.SwapSides ? Side.East : Side.West;
        return GetRoleGroup(role) == RoleGroup.Dps ? dpsSide : supportSide;
    }

    private static RoleGroup GetRoleGroup(PartyRole role)
        => role is PartyRole.M1 or PartyRole.M2 or PartyRole.R1 or PartyRole.R2
            ? RoleGroup.Dps
            : RoleGroup.Support;

    private void ApplyDisplay(Solution solution, Side side)
    {
        var mech = solution == Solution.Spread ? "SPREAD" : "STACK";
        var sideText = side == Side.East ? "EAST" : "WEST";

        if(Controller.TryGetElementByName(ElLabel, out var label))
        {
            label.Enabled = true;
            label.overlayText = $"{mech}  >>  {sideText}";
        }

        if(!Controller.TryGetElementByName(ElDestination, out var dest)) return;

        var x = side == Side.East ? C.EastAnchorX : C.WestAnchorX;
        var z = side == Side.East ? C.EastAnchorZ : C.WestAnchorZ;
        dest.refX = x;
        dest.refY = z;
        dest.color = Controller.AttentionColor;
        dest.tether = true;
        dest.Enabled = true;
    }

    private void ApplyPreview()
    {
        if(Controller.TryGetElementByName(ElPreviewEast, out var east))
        {
            east.refX = C.EastAnchorX;
            east.refY = C.EastAnchorZ;
            east.Enabled = true;
        }

        if(Controller.TryGetElementByName(ElPreviewWest, out var west))
        {
            west.refX = C.WestAnchorX;
            west.refY = C.WestAnchorZ;
            west.Enabled = true;
        }
    }

    private static bool TryMapPlayerVfx(string vfxPath, out PlayerVfx playerVfx)
    {
        playerVfx = vfxPath switch
        {
            VfxPlayerSpread => PlayerVfx.Spread,
            VfxPlayerStack => PlayerVfx.Stack,
            _ => PlayerVfx.None,
        };
        return playerVfx != PlayerVfx.None;
    }

    private static bool TryMapKefkaVfx(string vfxPath, out KefkaVfx kefkaVfx)
    {
        kefkaVfx = vfxPath switch
        {
            VfxKefkaTrue => KefkaVfx.True,
            VfxKefkaFalse => KefkaVfx.False,
            _ => KefkaVfx.None,
        };
        return kefkaVfx != KefkaVfx.None;
    }

    #endregion
}

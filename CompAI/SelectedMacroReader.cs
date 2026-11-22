using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CompAI;

/// <summary>Framework-thread copy of one loaded macro selected in the normally open Macro window.</summary>
internal sealed unsafe class SelectedMacroReader(IGameGui gui)
{
    internal const string AddonName = "Macro";
    internal const int LineCount = 15;
    // Capture limits, not assertions about the game's editor limits.
    internal const int NameByteLimit = 128;
    internal const int LineByteLimit = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal SelectedMacroObservation? Capture(out bool menuObserved)
    {
        var observed = false;
        SelectedMacroOwner? ObserveTracked()
        {
            var visible = false;
            try { return Observe(out visible); }
            finally { observed |= visible; }
        }
        SelectedMacroObservation? result;
        try { result = CaptureStable(ObserveTracked, Copy); }
        catch (Exception) { result = null; }
        menuObserved = observed;
        return result;
    }

    private SelectedMacroOwner? Observe(out bool menuObserved)
    {
        menuObserved = false;
        var player = PlayerState.Instance();
        var agents = AgentModule.Instance();
        var manager = RaptureAtkUnitManager.Instance();
        if (!Aligned((nint)player) || !Aligned((nint)agents) || !Aligned((nint)manager) ||
            !player->IsLoaded || player->ContentId == 0) return null;
        var agent = (AgentMacro*)agents->GetAgentByInternalId(AgentId.Macro);
        if (!Aligned((nint)agent) || agent->AddonId is 0 or > ushort.MaxValue || !agent->IsAgentActive() ||
            !agent->IsAddonReady() || !agent->IsAddonShown()) return null;
        var addon = manager->GetAddonById((ushort)agent->AddonId);
        if (!Aligned((nint)addon) || addon->NameString != AddonName) return null;
        var handle = gui.GetAddonByName(AddonName);
        if (handle.IsNull || !Aligned(handle.Address) || !MatchesAddon((nint)addon, agent->AddonId, addon->NameString,
                handle.Address, handle.Id, handle.IsReady, handle.IsVisible)) return null;
        // An unreadable new selection must invalidate an older retained macro.
        // Only an ordinary closed/unobserved window may retain the old result.
        menuObserved = true;
        if (agent->SelectedMacroSet > 1 || agent->SelectedMacroIndex > 99) return null;
        var macros = RaptureMacroModule.Instance();
        if (!Aligned((nint)macros) || macros->CharacterContentId != player->ContentId) return null;
        return new((nint)player, player->ContentId, (nint)agents, (nint)manager, (nint)agent,
            (nint)addon, agent->AddonId, (nint)macros, (int)agent->SelectedMacroSet, (int)agent->SelectedMacroIndex);
    }

    internal static bool MatchesAddon(nint addon, uint addonId, string name,
        nint handleAddress, uint handleId, bool ready, bool visible) =>
        Aligned(addon) && addonId is > 0 and <= ushort.MaxValue && name == AddonName &&
        addon == handleAddress && addonId == handleId && ready && visible;

    private static bool Aligned(nint address) => address > 0 && address % IntPtr.Size == 0;

    private static bool ValidOwner(SelectedMacroOwner owner) => Aligned(owner.Player) && owner.ContentId != 0 &&
        Aligned(owner.Agents) && Aligned(owner.Manager) && Aligned(owner.Agent) && Aligned(owner.Addon) && Aligned(owner.Macros) &&
        owner.AddonId is > 0 and <= ushort.MaxValue && owner.Set is 0 or 1 && owner.Index is >= 0 and <= 99;

    private static bool NativeOwnerMatches(SelectedMacroOwner owner)
    {
        if (!ValidOwner(owner)) return false;
        var player = (PlayerState*)owner.Player;
        var agent = (AgentMacro*)owner.Agent;
        return player->IsLoaded && player->ContentId == owner.ContentId &&
            ((RaptureMacroModule*)owner.Macros)->CharacterContentId == owner.ContentId &&
            agent->AddonId == owner.AddonId && ((AtkUnitBase*)owner.Addon)->Id == owner.AddonId &&
            agent->SelectedMacroSet == owner.Set && agent->SelectedMacroIndex == owner.Index;
    }

    internal static SelectedMacroSample? Copy(SelectedMacroOwner owner)
    {
        if (!NativeOwnerMatches(owner)) return null;
        var macros = (RaptureMacroModule*)owner.Macros;
        // These managed span getters expose fixed native arrays. Index only the
        // selected record; never copy the module, other macros or editor history.
        var selectedSet = owner.Set == 0 ? macros->Individual : macros->Shared;
        ref var selected = ref selectedSet[owner.Index];
        var buffers = new SelectedMacroTextBuffer[LineCount + 1];
        var name = CopyText((Utf8String*)Unsafe.AsPointer(ref selected.Name), NameByteLimit);
        if (name is null) return null;
        buffers[0] = name;
        var lines = selected.Lines;
        for (var index = 0; index < LineCount; index++)
        {
            var line = CopyText((Utf8String*)Unsafe.AsPointer(ref lines[index]), LineByteLimit);
            if (line is null) return null;
            buffers[index + 1] = line;
        }
        if (!NativeOwnerMatches(owner)) return null;
        for (var index = 0; index < buffers.Length; index++)
            if (!TextBufferMatches(buffers[index], index == 0 ? NameByteLimit : LineByteLimit)) return null;
        return NativeOwnerMatches(owner) ? new(buffers) : null;
    }

    private static SelectedMacroTextBuffer? CopyText(Utf8String* text, int limit)
    {
        if (!Aligned((nint)text)) return null;
        var buffer = (byte*)text->StringPtr;
        var used = text->BufUsed;
        var capacity = text->BufSize;
        if ((nint)buffer <= 0 || used < 1 || used > limit + 1 || capacity < used) return null;
        var bytes = new ReadOnlySpan<byte>(buffer, (int)used).ToArray();
        var result = new SelectedMacroTextBuffer((nint)text, (nint)buffer, used, capacity, bytes);
        return TextBufferMatches(result, limit) ? result : null;
    }

    internal static bool TextBufferMatches(SelectedMacroTextBuffer value, int limit)
    {
        if (!ValidBuffer(value, limit)) return false;
        var text = (Utf8String*)value.Address;
        return (nint)(byte*)text->StringPtr == value.Buffer && text->BufUsed == value.Used && text->BufSize == value.Capacity &&
            new ReadOnlySpan<byte>((byte*)value.Buffer, (int)value.Used).SequenceEqual(value.Bytes);
    }

    private static bool ValidBuffer(SelectedMacroTextBuffer? value, int limit) => value is not null &&
        Aligned(value.Address) && value.Buffer > 0 && value.Used >= 1 && value.Used <= limit + 1 && value.Capacity >= value.Used &&
        value.Bytes is not null && value.Bytes.Length == value.Used;

    private static string? Decode(SelectedMacroTextBuffer value, int limit, bool allowTab = false)
    {
        if (!ValidBuffer(value, limit) || value.Bytes[^1] != 0) return null;
        var content = value.Bytes.AsSpan(0, value.Bytes.Length - 1);
        if (content.Contains((byte)0)) return null;
        try
        {
            var text = StrictUtf8.GetString(content);
            // Payloads (including auto-translate) require a faithful representation.
            // Reject them rather than silently remove content from a command.
            return text.Any(character => char.IsControl(character) && !(allowTab && character == '\t')) ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool SameSample(SelectedMacroSample first, SelectedMacroSample? second)
    {
        if (second?.Buffers is not { Length: LineCount + 1 }) return false;
        for (var index = 0; index <= LineCount; index++)
        {
            var left = first.Buffers[index];
            var right = second.Buffers[index];
            if (!ValidBuffer(right, index == 0 ? NameByteLimit : LineByteLimit) || left.Address != right.Address ||
                left.Buffer != right.Buffer || left.Used != right.Used || left.Capacity != right.Capacity ||
                !left.Bytes.AsSpan().SequenceEqual(right.Bytes)) return false;
        }
        return true;
    }

    internal static SelectedMacroObservation? CaptureStable(Func<SelectedMacroOwner?> observe,
        Func<SelectedMacroOwner, SelectedMacroSample?> copy)
    {
        var before = observe();
        if (before is not { } owner || !ValidOwner(owner) || observe() != before) return null;
        var first = copy(owner);
        if (first?.Buffers is not { Length: LineCount + 1 } || observe() != before) return null;
        var name = Decode(first.Buffers[0], NameByteLimit);
        if (name is null) return null;
        var lines = new string[LineCount];
        for (var index = 0; index < LineCount; index++)
        {
            var line = Decode(first.Buffers[index + 1], LineByteLimit, allowTab: true);
            if (line is null) return null;
            lines[index] = line;
        }
        // Re-resolve after managed decoding before touching retained addresses.
        if (observe() != before || !SameSample(first, copy(owner)) || observe() != before) return null;
        return new(DateTimeOffset.UtcNow, owner.Set, owner.Index, name, Array.AsReadOnly(lines));
    }
}

internal sealed record SelectedMacroObservation(DateTimeOffset CapturedAtUtc, int Set, int Index,
    string Name, IReadOnlyList<string> Lines);
internal readonly record struct SelectedMacroOwner(nint Player, ulong ContentId, nint Agents, nint Manager,
    nint Agent, nint Addon, uint AddonId, nint Macros, int Set, int Index);
internal sealed record SelectedMacroTextBuffer(nint Address, nint Buffer, long Used, long Capacity, byte[] Bytes);
internal sealed record SelectedMacroSample(SelectedMacroTextBuffer[] Buffers);

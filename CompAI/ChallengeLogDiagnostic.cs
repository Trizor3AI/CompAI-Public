using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CompAI;

/// <summary>Capture is a manual local diagnostic. Its bounded text walker also corroborates supported Challenge counters on the framework thread.</summary>
internal static unsafe class ChallengeLogDiagnostic
{
    internal const int MaxOutputBytes = 16384;
    internal const int MaxTextNodes = 200;
    private const int MaxNodes = 512;
    private const int MaxDepth = 8;
    private const int MaxAncestors = 32;
    private const int MaxTextBytes = 1024;
    private const int MaxTotalTextBytes = 8192;

    internal static ChallengeDiagnosticResult Capture(IGameGui gui)
    {
        try
        {
            var player = PlayerState.Instance();
            if (player == null || !player->IsLoaded || player->ContentId == 0)
                return Failed("Log into a character first.");
            var owner = player->ContentId;
            var before = ChallengeLogReader.Observe();
            var handle = gui.GetAddonByName("ContentsNote");
            if (before is null || handle.IsNull || !handle.IsReady || !handle.IsVisible)
                return Failed("Open Challenge Log, select the affected tab and let it load, then copy again.");
            var addon = (AtkUnitBase*)handle.Address;
            if (addon->NameString != "ContentsNote" || !TryCopyVisibleText(&addon->UldManager, out var texts))
                return Failed("Challenge Log layout exceeds the supported diagnostic bounds; nothing copied.");
            var after = ChallengeLogReader.Observe();
            var current = gui.GetAddonByName("ContentsNote");
            if (after is null || !before.SameAs(after) || current.Address != handle.Address ||
                current.IsNull || !current.IsReady || !current.IsVisible || PlayerState.Instance() != player ||
                !player->IsLoaded || player->ContentId != owner)
                return Failed("Challenge Log changed during capture; let it settle and copy again.");
            return Format(before.Tab, before.Rows, texts);
        }
        catch (Exception)
        {
            return Failed("Challenge Log diagnostic is unavailable; nothing copied.");
        }
    }

    internal static ChallengeDiagnosticResult Format(byte tab, ChallengeRowObservation[] rows, ChallengeDiagnosticText[] texts)
    {
        // Explicit projection excludes pointers, player identity, paths and raw SeString payloads.
        if (tab > 14 || rows.Length is < 1 or > ChallengeLogReader.DisplaySlots ||
            rows.Any(row => row.Id is < 1 or > ChallengeLogReader.CompletionBits) ||
            texts.Length is < 1 or > MaxTextNodes)
            return Failed("Challenge Log diagnostic is incomplete; nothing copied.");
        var output = JsonSerializer.Serialize(new
        {
            diagnostic = "compai_challenge_log_v1", addon = "ContentsNote", selected_tab = tab,
            rows = rows.Select(row => new { id = row.Id, raw_status = row.RawStatus, completed = row.Completed }),
            text_nodes = texts.Select(row => new
            {
                node_id = row.NodeId, parent_node_id = row.ParentNodeId,
                component_path = row.ComponentPath, text = row.Text,
            }),
        });
        return Encoding.UTF8.GetByteCount(output) <= MaxOutputBytes
            ? new(output, "Challenge Log diagnostic copied. Paste it into the support chat.")
            : Failed("Challenge Log diagnostic exceeds 16 KB; nothing copied.");
    }

    internal static bool TryCopyVisibleText(AtkUldManager* manager, out ChallengeDiagnosticText[] texts)
    {
        texts = [];
        var copied = new List<ChallengeDiagnosticText>();
        var visited = new HashSet<nint>();
        var bytes = 0; var examined = 0;
        if (!CopyManager(manager, [], copied, visited, ref bytes, ref examined)) return false;
        texts = copied.ToArray();
        return true;
    }

    private static bool CopyManager(AtkUldManager* manager, uint[] componentPath,
        List<ChallengeDiagnosticText> texts, HashSet<nint> visited, ref int bytes, ref int examined)
    {
        if (manager == null || componentPath.Length > MaxDepth || manager->NodeListCount > MaxNodes) return false;
        var count = manager->NodeListCount; var list = manager->NodeList;
        if (count > 0 && list == null) return false;
        for (var i = 0; i < count; ++i)
        {
            if (++examined > MaxNodes * 4) return false;
            var node = list[i];
            if (node == null || !visited.Add((nint)node)) continue;
            if (visited.Count > MaxNodes) return false;
            if (!VisibleThroughParents(node)) continue;
            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                var length = text->NodeText.StringLength;
                if (length < 0 || length > MaxTextBytes || text->NodeText.BufUsed < 0 ||
                    text->NodeText.BufUsed > MaxTextBytes + 1 || text->NodeText.BufUsed < length ||
                    text->NodeText.BufSize < text->NodeText.BufUsed) return false;
                if (text->NodeText.BufUsed <= 1) continue;
                if ((byte*)text->NodeText.StringPtr == null) return false;
                var span = text->NodeText.AsSpan();
                if (span.Length > MaxTextBytes || bytes + span.Length > MaxTotalTextBytes) return false;
                bytes += span.Length;
                var value = SeString.Parse(span.ToArray()).TextValue;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (texts.Count >= MaxTextNodes) return false;
                texts.Add(new(node->NodeId, node->ParentNode == null ? null : node->ParentNode->NodeId,
                    componentPath, value));
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component == null || !CopyManager(&component->UldManager, [..componentPath, node->NodeId],
                        texts, visited, ref bytes, ref examined)) return false;
            }
        }
        return manager->NodeList == list && manager->NodeListCount == count;
    }

    private static bool VisibleThroughParents(AtkResNode* node)
    {
        for (var depth = 0; node != null && depth < MaxAncestors; ++depth, node = node->ParentNode)
            if ((node->NodeFlags & NodeFlags.Visible) == 0 || node->Color.A == 0) return false;
        return node == null;
    }

    internal static ChallengeDiagnosticResult Failed(string message) => new(null, message);
}

internal sealed record ChallengeDiagnosticText(uint NodeId, uint? ParentNodeId, uint[] ComponentPath, string Text);
internal sealed record ChallengeDiagnosticResult(string? Text, string Status);

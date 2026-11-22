using System;

namespace CompAI;

// Managed copies only: no game pointers or native state escape the framework callback.
internal sealed record TooltipRun(string Text, ushort Color);
internal sealed record TooltipText(uint NodeId, float X, float Y, string Text, byte[] Encoded,
    TooltipRun[] Runs);
internal sealed record HoveredChocoboTooltip(string Bag, uint Slot, uint ItemId, uint BaseItemId,
    string ItemName, int BreedingCapacity, DateTimeOffset CapturedAtUtc, TooltipText[] Nodes,
    string[] StatLabels);
internal sealed record PedigreeReading(string Kind, int Grade, string Sex, int[] FatherStars,
    int[] MotherStars);

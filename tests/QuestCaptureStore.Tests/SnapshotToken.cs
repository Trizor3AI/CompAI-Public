namespace CompAI;

// Publication-only stand-in: the production store treats QuestSnapshot as an opaque
// reference. QuestReader.Tests owns data/capture correctness. This project intentionally
// has no game, Dalamud, native reader, network, or model dependencies.
internal sealed record QuestSnapshot(string Token);

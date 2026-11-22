using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly CharacterDetailsReader characterDetailsReader;

    // Caller holds stateGate. No native reads until the model requests this tool.
    private Func<JsonElement, string>? CreateCharacterDetailsQueryUnderLock()
    {
        if (!this.Access.CharacterContext || !this.CanReadCurrentCharacter) return null;
        var owner = this.activeContentId;
        var session = this.contextLoadEpoch;
        bool Permitted() => this.Access.CharacterContext && this.CanReadCurrentCharacter &&
            owner == this.activeContentId && session == this.contextLoadEpoch;
        var metadata = this.characterDetailsReader.CreateMetadataCache();
        var captures = new CharacterDetailsCaptureCache(selection =>
        {
            var questionContinues = this.companion.CaptureQuestionContinuation();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = deadline.Token;
            // Never hold stateGate while waiting for the framework thread.
            try
            {
                return Framework.RunOnFrameworkThread(() =>
                {
                    lock (this.stateGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Permitted() || !questionContinues()) throw new OperationCanceledException();
                        using var timing = PerformanceDiagnostics.Measure("reader.character");
                        var reading = ReadLoadedCharacterId() == owner
                            ? this.characterDetailsReader.Capture(selection, metadata)
                            : new CharacterDetailsReading(DateTimeOffset.UtcNow, null, null, null,
                                "Character data is loading.", "Equipment is loading.", "Saved gearsets are loading.");
                        token.ThrowIfCancellationRequested();
                        if (!Permitted() || !questionContinues()) throw new OperationCanceledException();
                        return new CharacterDetailsLookup(reading);
                    }
                }).WaitAsync(token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                lock (this.stateGate)
                    if (!Permitted() || !questionContinues()) throw;
                throw new TimeoutException("Character details reader timed out.");
            }
        });
        return arguments =>
        {
            CharacterDetailsLookup.ValidateArguments(arguments);
            lock (this.stateGate) if (!Permitted()) throw new OperationCanceledException();
            var lookup = captures.GetLookup(arguments);
            lock (this.stateGate)
            {
                if (!Permitted()) throw new OperationCanceledException();
                return JsonSerializer.Serialize(lookup.Query(arguments));
            }
        };
    }
}

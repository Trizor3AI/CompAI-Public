using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace CompAI;

public sealed partial class Plugin
{
    private void RegisterTalkCapture()
    {
        AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", this.OnTalkCapture);
        AddonLifecycle.RegisterListener(AddonEvent.PostHide, "Talk", this.OnTalkCapture);
        AddonLifecycle.RegisterListener(AddonEvent.PostClose, "Talk", this.OnTalkCapture);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", this.OnTalkCapture);
    }

    // Addon lifecycle executes on the framework thread. PostUpdate catches pages
    // between the slower readiness polls; only the bounded Talk reader is used.
    private void OnTalkCapture(AddonEvent type, AddonArgs _)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.Access.NpcDialogue) return;
            var now = DateTimeOffset.UtcNow;
            if (type != AddonEvent.PostUpdate)
            {
                this.gameContextReader.ClearDialogueCache();
                this.recentNpcDialogue.Observe(null, now);
                this.npcConversation.Observe(null, now);
                return;
            }
            if (!this.CanReadCurrentCharacter) return;
            var loadedId = ReadLoadedCharacterId();
            if (loadedId == 0) return; // Loading is not a confirmed Talk closure.
            if (loadedId != this.activeContentId)
            {
                this.OnDetectedCharacterMismatch();
                return;
            }
            this.ObserveNpcReadingUnderLock(this.gameContextReader.Capture(false, true));
            this.nextNpcDialogueCheck = now.AddMilliseconds(250);
        }
    }
}

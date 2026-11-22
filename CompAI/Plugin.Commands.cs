using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Command;
using Lumina.Excel.Sheets;

namespace CompAI;

public sealed partial class Plugin
{
    private readonly Dictionary<string, (CommandInfo Info, string PresetId)> presetCommandHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> presetCommandStatus = new(StringComparer.Ordinal);
    private string? chatCommandNotice;
    private readonly Dictionary<string, CommandInfo> coreCommandHandlers = new(StringComparer.OrdinalIgnoreCase);
    internal string? CoreCommandWarning { get; private set; }

    private void RegisterCoreCommands()
    {
        this.RegisterCoreCommand(ChatCommandName, new CommandInfo(this.OnChatCommand)
            { HelpMessage = "Open or close chat. Add a question to send it, or use /compai settings (or /compai s) for settings." });
    }

    private void RegisterCoreCommand(string command, CommandInfo info)
    {
        if (Commands.AddHandler(command, info)) this.coreCommandHandlers[command] = info;
        else this.CoreCommandWarning = (this.CoreCommandWarning is null ? "" : this.CoreCommandWarning + " ") +
            command + " could not be registered. Open CompAI from the plugin installer instead.";
    }

    private void RemoveCoreCommands()
    {
        foreach (var (command, info) in this.coreCommandHandlers)
            if (Commands.Commands.TryGetValue(command, out var current) && ReferenceEquals(current, info))
                Commands.RemoveHandler(command);
        this.coreCommandHandlers.Clear();
    }

    internal bool PresetCommandsEnabled => this.personalityPresets.CommandsEnabled;
    internal string? ChatCommandNotice { get { lock (this.stateGate) return this.chatCommandNotice; } }

    internal bool SetPresetCommandsEnabled(bool enabled)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.personalityPresets.SetCommandsEnabled(enabled)) return false;
            this.RefreshPresetCommands();
            return true;
        }
    }

    internal string GetPresetCommandStatus(string presetId)
    {
        lock (this.stateGate)
            return !this.PresetCommandsEnabled ? "Preset commands are off. /compai question still uses the default personality." :
                this.presetCommandStatus.GetValueOrDefault(presetId, "No shortcut is registered for this preset.");
    }

    internal void RefreshPresetCommands()
    {
        lock (this.stateGate)
        {
            this.RemovePresetCommands();
            this.presetCommandStatus.Clear();
            if (this.contextDisposed || !this.PresetCommandsEnabled) return;
            HashSet<string> reserved;
            try
            {
                reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var language in new[] { ClientLanguage.English, ClientLanguage.German, ClientLanguage.French, ClientLanguage.Japanese })
                {
                    var sheet = DataManager.GetExcelSheet<TextCommand>(language);
                    if (sheet.Count == 0) throw new InvalidOperationException("Empty game command list");
                    foreach (var row in sheet)
                    {
                        foreach (var value in new[] { row.Command.ExtractText(), row.ShortCommand.ExtractText(),
                            row.Alias.ExtractText(), row.ShortAlias.ExtractText() })
                        {
                            var command = value.Trim();
                            if (command.Length > 0) reserved.Add(command.StartsWith('/') ? command : "/" + command);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // If we cannot establish game-command collisions, leave all optional shortcuts unregistered.
                foreach (var preset in this.personalityPresets.Presets)
                    this.presetCommandStatus[preset.Id] = $"Shortcuts unavailable: game command list could not be checked ({ex.GetType().Name}). Use /compai question; retry with Refresh shortcuts.";
                return;
            }
            foreach (var plan in PresetCommands.Plan(this.personalityPresets.Presets, reserved, Commands.Commands.Keys))
            {
                if (plan.UnavailableReason is { } reason)
                {
                    this.presetCommandStatus[plan.PresetId] = reason;
                    continue;
                }
                var command = plan.Command!;
                var info = new CommandInfo((_, input) => this.OnPresetCommand(command, plan.PresetId, input))
                {
                    HelpMessage = "CompAI: open this chat, or add a question to ask right away.",
                };
                if (Commands.AddHandler(command, info))
                {
                    this.presetCommandHandlers[command] = (info, plan.PresetId);
                    this.presetCommandStatus[plan.PresetId] = $"{command} question — send immediately; with game-chat replies on, the window stays as it is. {command} alone opens the matching chat.";
                }
                else this.presetCommandStatus[plan.PresetId] = command + " could not be registered. Use /compai question or Refresh shortcuts.";
            }
        }
    }

    private void RemovePresetCommands()
    {
        foreach (var (command, owned) in this.presetCommandHandlers)
            if (Commands.Commands.TryGetValue(command, out var current) && ReferenceEquals(current, owned.Info))
                Commands.RemoveHandler(command);
        this.presetCommandHandlers.Clear();
    }

    private void OnChatCommand(string _, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) { this.chatWindow.Toggle(); return; }
        // Only an entire argument is a local subcommand; ordinary questions stay intact.
        var argument = input.Trim();
        if (argument.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("s", StringComparison.OrdinalIgnoreCase))
        { this.OpenSettings(); return; }
        this.RunPersonalityCommand(null, input);
    }

    private void OnPresetCommand(string command, string presetId, string input)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed || !this.PresetCommandsEnabled ||
                !this.presetCommandHandlers.TryGetValue(command, out var handler) || handler.PresetId != presetId) return;
            this.RunPersonalityCommand(presetId, input);
        }
    }

    private void RunPersonalityCommand(string? presetId, string input)
    {
        lock (this.stateGate)
        {
            if (this.contextDisposed) return;
            this.chatCommandNotice = null;
            var question = input.Trim();
            if (question.Length == 0 || !this.Access.GameChatReplies) this.chatWindow.IsOpen = true;
            if (question.Length > 2000)
            {
                this.SetChatCommandNotice("Command not sent: questions can contain at most 2,000 characters.");
                return;
            }
            var preset = presetId is null ? this.personalityPresets.Default :
                this.personalityPresets.Presets.FirstOrDefault(p => p.Id == presetId);
            if (preset is null || !this.companion.ContinuePersonalityChat(preset))
            {
                this.SetChatCommandNotice("Could not open the matching chat. Check saved-chat storage and try again; nothing was sent.");
                return;
            }
            this.preparedQuestion = null;
            if (question.Length == 0) return;
            // An unrelated unsent draft belongs to the user. Preserve it even while a shortcut sends a question.
            if (string.IsNullOrWhiteSpace(this.companion.Draft)) this.companion.Draft = question;
            if (this.companion.View.SendBlockReason is { } blocked)
                this.SetChatCommandNotice("Command not sent or queued: " + blocked);
            else if (!this.SendChatQuestion(question, replyInGameChat: true))
                this.SetChatCommandNotice("Command not sent or queued: " + this.questionPreviewState);
        }
    }

    private void SetChatCommandNotice(string message)
    {
        this.chatCommandNotice = message;
        if (this.Access.GameChatReplies) this.PrintGameChatNotice(message);
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CompAI;

// Links are text fragments in the same wrapping layout as their surrounding prose.
// Navigation still goes through the companion's current-answer membership check.
internal sealed class AnswerTextRenderer
{
    private string? cachedAnswer;
    private bool cachedPrefix;
    private IReadOnlyList<QuestionSource> cachedSources = Array.Empty<QuestionSource>();
    private readonly Dictionary<string, QuestionSource> sourcesByUrl = new(StringComparer.Ordinal);
    private IReadOnlyList<AnswerTextRun> runs = Array.Empty<AnswerTextRun>();
    private AnswerTextLayoutResult? layout;
    private float cachedWidth;
    private float cachedLineHeight;
    private int cachedFont;
    private string? cachedQuestion;
    private IReadOnlyList<QuestionSource> questionSources = Array.Empty<QuestionSource>();
    private string? cachedMacroAnswer;
    private IReadOnlyList<MacroCopyPart> macroParts = Array.Empty<MacroCopyPart>();

    internal void Draw(CompanionView companion) =>
        this.DrawCore(companion.QuestionAnswer, companion.QuestionSources, companion.OpenQuestionSource, true);

    internal void Draw(string answer, IReadOnlyList<QuestionSource> sources, Action<string> open,
        Action<string>? openMap = null) => this.DrawCore(answer, sources, open, false, openMap: openMap);

    internal void DrawQuestion(string question, Action<string> open)
    {
        if (this.cachedQuestion != question)
        {
            this.cachedQuestion = question;
            var sources = new List<QuestionSource>();
            foreach (var uri in AnswerLinks.Extract(question))
                sources.Add(new QuestionSource("Link from your message", uri.AbsoluteUri));
            this.questionSources = sources;
        }
        this.DrawCore(question, this.questionSources, open, false, userProvided: true);
    }

    // Preserve URLs and Markdown in the copied answer.
    internal void DrawCopyActions(string answer)
    {
        if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(answer);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy the full answer, including links.");

        if (this.cachedMacroAnswer != answer)
        {
            this.cachedMacroAnswer = answer;
            this.macroParts = MacroCopy.Extract(answer);
        }
        for (var index = 0; index < this.macroParts.Count; index++)
        {
            var part = this.macroParts[index];
            ContinueActionRow(part.Label);
            ImGui.PushID($"macro_copy_{index}");
            if (ImGui.SmallButton(part.Label)) ImGui.SetClipboardText(part.Text);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36);
                ImGui.TextUnformatted($"Copy {part.LineCount} lines into User Macros. Run each part yourself, in order when intended.");
                ImGui.Separator();
                ImGui.TextUnformatted(part.Text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
            ImGui.PopID();
        }
    }

    internal static void ContinueActionRow(string label)
    {
        var style = ImGui.GetStyle();
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var nextRight = ImGui.GetItemRectMax().X + style.ItemSpacing.X +
            ImGui.CalcTextSize(label).X + style.FramePadding.X * 2;
        if (nextRight <= right) ImGui.SameLine();
    }

    private void DrawCore(string answer, IReadOnlyList<QuestionSource> sources, Action<string> open, bool prefix,
        bool userProvided = false, Action<string>? openMap = null)
    {
        if (this.cachedAnswer != answer || this.cachedPrefix != prefix || !SameSources(this.cachedSources, sources))
        {
            this.cachedAnswer = answer;
            this.cachedPrefix = prefix;
            this.cachedSources = sources;
            this.sourcesByUrl.Clear();
            foreach (var source in sources)
                this.sourcesByUrl[source.Url] = source;

            var parsed = AnswerLinks.Parse(answer);
            var displayedUrls = new HashSet<string>(StringComparer.Ordinal);
            var nextRuns = new List<AnswerTextRun>();
            if (prefix) nextRuns.Add(new AnswerTextRun("Answer: ", null));
            foreach (var run in parsed)
            {
                if (run.Url is { } url && (this.sourcesByUrl.ContainsKey(url) || MapLocationLink.TryParse(url, out _)))
                {
                    displayedUrls.Add(url);
                    nextRuns.Add(run);
                }
                else
                    nextRuns.Add(run with { Url = null });
            }
            var firstExtraSource = true;
            foreach (var source in sources)
            {
                if (!source.IsBundled || displayedUrls.Contains(source.Url))
                    continue;
                nextRuns.Add(new AnswerTextRun(firstExtraSource ? "\nSources: " : " · ", null));
                nextRuns.Add(new AnswerTextRun(source.Title, source.Url));
                firstExtraSource = false;
            }
            this.runs = nextRuns;
            this.layout = null;
        }

        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        var lineHeight = ImGui.GetTextLineHeight();
        var font = ImGui.GetFont().GetHashCode();
        // Slightly thicken the active font so all its existing glyphs remain available.
        var boldStroke = Math.Max(0.5f, lineHeight / 24f);
        if (this.layout is null || width != this.cachedWidth || lineHeight != this.cachedLineHeight || font != this.cachedFont)
        {
            this.layout = AnswerTextLayout.Build(this.runs, width, lineHeight,
                (text, bold) => ImGui.CalcTextSize(text).X + (bold ? boldStroke : 0));
            this.cachedWidth = width;
            this.cachedLineHeight = lineHeight;
            this.cachedFont = font;
        }

        ImGui.PushID("companion_answer_text");
        ImGui.BeginGroup();
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, this.layout.Height));
        var end = ImGui.GetCursorPos();
        var draw = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        // These bindings predate ImGui's TextLink color/widget; use the theme accent.
        var linkColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
        // Window bounds are deliberately conservative: ImGui retains its own
        // precise clip rectangle, while offscreen fragments never submit text or
        // link hit boxes. Original indices keep visible link IDs stable.
        var windowTop = ImGui.GetWindowPos().Y;
        var visible = this.layout.VisibleFragments(windowTop - start.Y,
            windowTop + ImGui.GetWindowSize().Y - start.Y);
        for (var i = visible.Start; i < visible.End; i++)
        {
            var fragment = this.layout.Fragments[i];
            var position = start + new Vector2(fragment.X, fragment.Y);
            var mapLink = openMap is not null && MapLocationLink.TryParse(fragment.Url, out _);
            var linked = mapLink || fragment.Url is not null && this.sourcesByUrl.TryGetValue(fragment.Url, out _);
            var hovered = false;
            if (linked && fragment.Width > 0)
            {
                ImGui.SetCursorScreenPos(position);
                ImGui.PushID(i);
                if (ImGui.InvisibleButton("##link", new Vector2(fragment.Width, fragment.Height)))
                {
                    if (mapLink) openMap!(fragment.Url!);
                    else open(fragment.Url!);
                }
                hovered = ImGui.IsItemHovered();
                if (hovered)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (hovered)
                {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(450f);
                    if (mapLink && MapLocationLink.TryParse(fragment.Url, out var location))
                    {
                        ImGui.TextUnformatted("Open game map · " + location.CoordinateLabel);
                        ImGui.TextUnformatted("Model-provided location; map IDs are checked when clicked.");
                    }
                    else
                    {
                        ImGui.TextUnformatted(fragment.Url!);
                        ImGui.TextUnformatted(userProvided ? "Link from your message; destination not verified" : this.sourcesByUrl[fragment.Url!].IsBundled
                            ? "Bundled reference" : "Model-provided link; destination not verified");
                    }
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
                ImGui.PopID();
            }
            // AddText is unformatted: percent signs and ImGui's ## marker stay literal.
            draw.AddText(position, linked ? linkColor : textColor, fragment.Text);
            if (fragment.Bold)
                draw.AddText(position + new Vector2(boldStroke, 0), linked ? linkColor : textColor, fragment.Text);
            if (linked)
            {
                var underline = position + new Vector2(0, fragment.Height - 1);
                draw.AddLine(underline, underline + new Vector2(fragment.Width, 0), linkColor, hovered ? 2f : 1f);
            }
        }
        ImGui.SetCursorPos(end);
        ImGui.EndGroup();
        ImGui.PopID();
    }

    private static bool SameSources(IReadOnlyList<QuestionSource> left, IReadOnlyList<QuestionSource> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}

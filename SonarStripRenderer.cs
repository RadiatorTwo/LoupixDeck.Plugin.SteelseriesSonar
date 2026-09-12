using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>What a bar can show right now.</summary>
internal enum SonarBarStatus
{
    /// <summary>The dial controls no Sonar channel.</summary>
    Unbound,

    /// <summary>Sonar is unreachable or reports nothing for this channel.</summary>
    Offline,

    /// <summary>The dial drives the mix of the mode Sonar is currently not running.</summary>
    InactiveMix,

    /// <summary>A real level is available.</summary>
    Live
}

/// <summary>
/// Draws the Sonar mixer bars onto a host <see cref="IRenderCanvas"/>, either as side-by-side
/// vertical bars or as stacked horizontal bands. Only host primitives are used, so the strip keeps
/// the core's font and symbols. The host serializes the call against its own drawing.
/// </summary>
internal static class SonarStripRenderer
{
    /// <summary>One dial's bar. <paramref name="Routed"/> is null for a mix that has no routing.</summary>
    public readonly record struct BarView(
        string Name,
        string? MixTag,
        SonarBarStatus Status,
        float Volume,
        bool Muted,
        bool? Routed);

    /// <summary>The strip's mode header.</summary>
    public readonly record struct HeaderView(bool Connected, bool StreamMode, bool MonitoringEnabled);

    private static readonly PluginColor Background = new(18, 18, 18);
    private static readonly PluginColor Track = new(48, 48, 48);
    private static readonly PluginColor FillActive = new(0x4C, 0xAF, 0x50);    // green
    private static readonly PluginColor FillMuted = new(0x9E, 0x9E, 0x9E);     // gray
    private static readonly PluginColor FillUnrouted = new(0xFF, 0xB7, 0x4D);  // amber — not in the mix
    private static readonly PluginColor TextColor = new(0xE0, 0xE0, 0xE0);
    private static readonly PluginColor DimColor = new(0x8A, 0x8A, 0x8A);
    private static readonly PluginColor MuteColor = new(0xE5, 0x73, 0x73);     // red — mute indicator

    // The device bezel overlaps the outermost pixels of the 60×270 panel, so keep all content
    // clear of every edge by this inset.
    private const int Edge = 4;

    private const int HeaderHeight = 20;

    public static void Render(IReadOnlyList<BarView> bars, HeaderView? header, IRenderCanvas canvas, bool horizontal)
    {
        canvas.Clear(Background);

        int top = Edge;
        if (header != null)
        {
            DrawHeader(canvas, header.Value);
            top += HeaderHeight + 4;
        }

        if (horizontal)
        {
            RenderHorizontal(bars, canvas, top);
        }
        else
        {
            RenderVertical(bars, canvas, top);
        }
    }

    /// <summary>
    /// Draws one channel band filling the whole canvas. Used for a single segment in the host's
    /// segmented strip mode, where each dial owns its own region.
    /// </summary>
    public static void RenderBand(BarView bar, IRenderCanvas canvas)
    {
        // No opaque clear here: in segmented mode the host has already drawn the page wallpaper
        // into this segment, so the band sits on top of it and its text is outlined.
        DrawBand(canvas, bar, 0, canvas.Height);
    }

    // ---- Header -----------------------------------------------------------------------------

    /// <summary>
    /// Sonar's current mode, plus the state of stream monitoring while in stream mode. Without it
    /// a bar gives no clue which of the mixes is even live right now.
    /// </summary>
    private static void DrawHeader(IRenderCanvas canvas, HeaderView header)
    {
        int width = canvas.Width - (2 * Edge);
        canvas.FillRoundedRectangle(Edge, Edge, width, HeaderHeight, 4, Track);

        string text = !header.Connected ? "NO SONAR" : header.StreamMode ? "STREAM" : "CLASSIC";
        PluginColor color = header.Connected ? TextColor : DimColor;

        // In stream mode the monitoring indicator takes the right end of the header, so the label
        // gets the remaining width.
        const int iconSize = 12;
        bool withIcon = header is { Connected: true, StreamMode: true };
        int textWidth = withIcon ? width - iconSize - 4 : width;

        canvas.DrawText(text, Edge, Edge, textWidth, HeaderHeight, color, 10f, bold: true, centered: true);

        if (withIcon)
        {
            canvas.DrawSymbol("headphones",
                Edge + width - iconSize - 2, Edge + ((HeaderHeight - iconSize) / 2), iconSize, iconSize,
                new SymbolStyle(header.MonitoringEnabled ? FillActive : DimColor));
        }
    }

    // ---- Vertical layout --------------------------------------------------------------------

    /// <summary>Side-by-side columns, one per dial, filled from the bottom.</summary>
    private static void RenderVertical(IReadOnlyList<BarView> bars, IRenderCanvas canvas, int top)
    {
        int width = canvas.Width;
        int height = canvas.Height;

        int count = Math.Max(1, bars.Count);
        float contentLeft = Edge;
        float contentRight = width - Edge;
        float columnWidth = (contentRight - contentLeft) / count;

        const int gap = 4;
        const int tagHeight = 12;    // mix marker above the track
        const int labelHeight = 14;  // channel name below the track
        const float tagSize = 9f;
        const float labelSize = 11f;

        int trackTop = top + tagHeight;
        int trackBottom = height - Edge - labelHeight;
        int trackHeight = Math.Max(1, trackBottom - trackTop);

        for (int i = 0; i < bars.Count; i++)
        {
            BarView bar = bars[i];
            int left = (int)Math.Round(contentLeft + (i * columnWidth) + gap);
            int right = (int)Math.Round(contentLeft + ((i + 1) * columnWidth) - gap);
            int columnPixels = Math.Max(1, right - left);

            if (bar.MixTag != null)
            {
                canvas.DrawText(bar.MixTag, left, top, columnPixels, tagHeight,
                    TagColor(bar), tagSize, bold: true, centered: true);
            }

            canvas.FillRoundedRectangle(left, trackTop, columnPixels, trackHeight, 4, Track);

            if (bar.Status != SonarBarStatus.Live)
            {
                // The reason goes inside the empty track, where there is room for it: a bar that
                // is merely bound to the other mode's mix must not look like a failure.
                (string note, PluginColor noteColor) = StatusNote(bar.Status);
                canvas.DrawText(note, left, trackTop, columnPixels, trackHeight, noteColor, tagSize,
                    centered: true);
                canvas.DrawText(Fit(bar.Name, canvas, labelSize, columnPixels), left, trackBottom + 1,
                    columnPixels, height - trackBottom - 1, DimColor, labelSize, centered: true);
                continue;
            }

            int fillHeight = (int)Math.Round(trackHeight * bar.Volume);
            if (fillHeight > 0)
            {
                canvas.FillRoundedRectangle(left, trackBottom - fillHeight, columnPixels, fillHeight, 4,
                    FillColor(bar));
            }

            // A column is too narrow for both the name and the value, so the name gives way while
            // the channel is muted — that is the state worth seeing at a glance.
            string label = bar.Muted ? "MUTE" : Fit(bar.Name, canvas, labelSize, columnPixels);
            canvas.DrawText(label, left, trackBottom + 1, columnPixels, height - trackBottom - 1,
                bar.Muted ? MuteColor : TextColor, labelSize, centered: true);
        }
    }

    // ---- Horizontal layout ------------------------------------------------------------------

    /// <summary>Bands stacked top to bottom, one self-contained card per dial.</summary>
    private static void RenderHorizontal(IReadOnlyList<BarView> bars, IRenderCanvas canvas, int top)
    {
        int count = Math.Max(1, bars.Count);
        float bandHeight = (canvas.Height - Edge - top) / (float)count;

        for (int i = 0; i < bars.Count; i++)
        {
            DrawBand(canvas, bars[i], (int)Math.Round(top + (i * bandHeight)), (int)Math.Round(bandHeight));
        }
    }

    /// <summary>
    /// Draws one channel band — mix marker, name, a full-width bar and the value — centered within
    /// the band rect of the given canvas. This is also the whole content of a segment, so the mix
    /// marker rides along here: a segment carries no header to explain which mix it belongs to.
    /// </summary>
    private static void DrawBand(IRenderCanvas canvas, BarView bar, int bandTop, int bandHeight)
    {
        const int sideInset = 10;  // left/right margin so the bar clears the bezel
        const int textInset = 2;   // text may run wider than the bar
        const int tagHeight = 10;
        const int barHeight = 12;
        const int nameHeight = 12;
        const int valueHeight = 12;
        const int gap = 6;
        const float tagSize = 9f;
        const float fontSize = 12f;

        int contentWidth = canvas.Width - (2 * sideInset);
        int textWidth = canvas.Width - (2 * textInset);
        int radius = barHeight / 2;

        bool withTag = bar.MixTag != null;
        int groupHeight = (withTag ? tagHeight : 0) + nameHeight + gap + barHeight + gap + valueHeight;
        int groupTop = bandTop + ((bandHeight - groupHeight) / 2);

        int nameTop = groupTop + (withTag ? tagHeight : 0);
        int barTop = nameTop + nameHeight + gap;
        int valueTop = barTop + barHeight + gap;

        // Mix marker on top, outlined like every other text so it stays legible over a wallpaper.
        if (withTag)
        {
            canvas.DrawText(bar.MixTag!, textInset, groupTop, textWidth, tagHeight, TagColor(bar), tagSize,
                bold: true, centered: true, outlined: true, outlineColor: PluginColor.Black);
        }

        canvas.DrawText(Fit(bar.Name, canvas, fontSize, textWidth), textInset, nameTop, textWidth, nameHeight,
            bar.Status == SonarBarStatus.Live ? TextColor : DimColor, fontSize,
            centered: true, outlined: true, outlineColor: PluginColor.Black);

        // Track plus a left-anchored fill. The fill grows from a thin sliver so low volumes stay
        // visible, and its radius is clamped to half its width so it stays a pill.
        canvas.FillRoundedRectangle(sideInset, barTop, contentWidth, barHeight, radius, Track);

        if (bar is { Status: SonarBarStatus.Live, Volume: > 0f })
        {
            int fillWidth = Math.Max(2, (int)Math.Round(contentWidth * bar.Volume));
            canvas.FillRoundedRectangle(sideInset, barTop, fillWidth, barHeight,
                Math.Min(radius, fillWidth / 2), FillColor(bar));
        }

        // A red mute symbol when muted, otherwise the percentage — or, when there is no level, the
        // reason there is none.
        if (bar is { Status: SonarBarStatus.Live, Muted: true })
        {
            const int iconSize = 16;
            canvas.DrawSymbol("volume-mute", (canvas.Width - iconSize) / 2, valueTop - 2, iconSize, iconSize,
                new SymbolStyle(MuteColor) { Outlined = true, OutlineColor = PluginColor.Black, OutlineWidth = 1.5f });
            return;
        }

        string value;
        PluginColor valueColor;
        float valueSize;

        if (bar.Status == SonarBarStatus.Live)
        {
            value = $"{(int)MathF.Round(bar.Volume * 100f)}%";
            valueColor = TextColor;
            valueSize = fontSize;
        }
        else
        {
            (value, valueColor) = StatusNote(bar.Status);
            valueSize = tagSize;
        }

        canvas.DrawText(value, textInset, valueTop, textWidth, valueHeight, valueColor, valueSize,
            centered: true, outlined: true, outlineColor: PluginColor.Black);
    }

    // ---- Shared -----------------------------------------------------------------------------

    /// <summary>
    /// What to write where the value would go. "OTHER MIX" is the common case by far: the dial
    /// drives the classic mix while Sonar runs in stream mode, or the other way round, and Sonar
    /// reports no usable level for the mix it is not running.
    /// </summary>
    private static (string Text, PluginColor Color) StatusNote(SonarBarStatus status) => status switch
    {
        SonarBarStatus.InactiveMix => ("OTHER MIX", FillUnrouted),
        SonarBarStatus.Offline => ("NO SONAR", DimColor),
        _ => ("–", DimColor)
    };

    /// <summary>A channel that is not routed into its stream mix is drawn in amber: its slider
    /// still moves, but nothing of it reaches that mix.</summary>
    private static PluginColor FillColor(BarView bar)
    {
        if (bar.Muted)
        {
            return FillMuted;
        }

        return bar.Routed == false ? FillUnrouted : FillActive;
    }

    private static PluginColor TagColor(BarView bar)
    {
        if (bar.Status == SonarBarStatus.InactiveMix || bar.Routed == false)
        {
            return FillUnrouted;
        }

        return bar.Status == SonarBarStatus.Live ? DimColor : new PluginColor(0x6A, 0x6A, 0x6A);
    }

    /// <summary>Truncates text with a trailing ellipsis so it fits the given width in the host
    /// font. Keeps the narrow strip readable.</summary>
    private static string Fit(string text, IRenderCanvas canvas, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || canvas.MeasureText(text, fontSize) <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        string trimmed = text;

        while (trimmed.Length > 1 && canvas.MeasureText(trimmed + ellipsis, fontSize) > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed + ellipsis;
    }
}

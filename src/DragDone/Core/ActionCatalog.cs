namespace DragDone.Core;

/// <summary>
/// Builds the rings shown for a drag. Pure: depends only on the payload, the tab and the
/// machine capabilities, so it is fully unit-tested.
/// </summary>
public static class ActionCatalog
{
    public const int MaxSectors = 8;

    public static readonly MenuTab[] TabOrder = { MenuTab.Convert, MenuTab.Tools, MenuTab.Actions };

    public static IReadOnlyList<ActionDef> Build(DragPayload p, MenuTab tab, Capabilities caps) => tab switch
    {
        MenuTab.Convert => Convert(p, caps),
        MenuTab.Tools => Tools(p, caps),
        _ => Actions(p, caps),
    };

    /// <summary>The tab to open when the requested one has nothing to offer.</summary>
    public static MenuTab Resolve(DragPayload p, MenuTab wanted, Capabilities caps)
    {
        if (Build(p, wanted, caps).Count > 0) return wanted;
        foreach (var t in TabOrder)
        {
            if (Build(p, t, caps).Count > 0) return t;
        }
        return MenuTab.Actions;
    }

    // ---------------------------------------------------------------- convert

    static ActionDef Fmt(string token, DragPayload p, string? needs = null)
    {
        var a = new ActionDef { Id = "convert:" + token, Literal = FormatLabel(token), Style = ActionStyle.Format };
        if (needs is not null) return a.Disabled(needs);
        if (p.CommonFormat == token) return a.Disabled("reason.sameFormat");
        return a;
    }

    public static string FormatLabel(string token) => token switch
    {
        "webp" => "WebP",
        "webm" => "WebM",
        "opus" => "Opus",
        _ => token.ToUpperInvariant(),
    };

    static IReadOnlyList<ActionDef> Convert(DragPayload p, Capabilities caps)
    {
        var ff = caps.Ffmpeg ? null : "reason.needsFfmpeg";
        switch (p.Kind)
        {
            case FileKind.Image:
                return new[]
                {
                    Fmt("png", p), Fmt("jpg", p), Fmt("webp", p, ff), Fmt("pdf", p), Fmt("ico", p),
                    new ActionDef
                    {
                        Id = "convert:more", LabelKey = "action.more", Glyph = Glyphs.More, GlyphOnly = true,
                        Children = new[] { Fmt("bmp", p), Fmt("tiff", p), Fmt("gif", p), Fmt("avif", p, ff) },
                    },
                };
            case FileKind.Video:
                return new[] { Fmt("mp4", p, ff), Fmt("mov", p, ff), Fmt("webm", p, ff), Fmt("gif", p, ff), Fmt("mp3", p, ff), Fmt("m4a", p, ff) };
            case FileKind.Audio:
                return new[] { Fmt("mp3", p, ff), Fmt("m4a", p, ff), Fmt("wav", p, ff), Fmt("flac", p, ff), Fmt("ogg", p, ff), Fmt("opus", p, ff) };
            case FileKind.Pdf:
                return new[] { Fmt("png", p), Fmt("jpg", p) };
            case FileKind.Document:
            {
                var pdf = Fmt("pdf", p);
                var needsOffice = p.Paths.Any(FileKinds.IsOfficeDocument);
                var unsupported = p.Paths.Any(x => !FileKinds.IsOfficeDocument(x) && !FileKinds.IsPlainText(x));
                if (unsupported) pdf = pdf.Disabled("reason.unsupported");
                else if (needsOffice && !caps.Office) pdf = pdf.Disabled("reason.needsOffice");
                return new[] { pdf };
            }
            default:
                return Array.Empty<ActionDef>();
        }
    }

    // ---------------------------------------------------------------- tools

    static ActionDef Icon(string id, string key, string glyph, string? hint = null) =>
        new() { Id = id, LabelKey = key, Glyph = glyph, HintKey = hint };

    static ActionDef Chip(string id, string literal) =>
        new() { Id = id, Literal = literal, Style = ActionStyle.Format };

    static IReadOnlyList<ActionDef> Tools(DragPayload p, Capabilities caps)
    {
        var ff = caps.Ffmpeg ? null : "reason.needsFfmpeg";
        ActionDef Media(ActionDef a) => ff is null ? a : a.Disabled(ff);

        switch (p.Kind)
        {
            case FileKind.Image:
            {
                var merge = Icon("img.mergepdf", "action.mergePdf", Glyphs.Pdf, "hint.mergePdf");
                if (!p.IsMultiple) merge = merge.Disabled("reason.needsMultiple");
                return new[]
                {
                    Icon("img.compress", "action.compress", Glyphs.Compress, "hint.compressImage"),
                    Icon("img.crop", "action.crop", Glyphs.Crop) with
                    {
                        DefaultChildId = "img.crop:free",
                        Children = new[]
                        {
                            Chip("img.crop:1x1", "1:1"), Chip("img.crop:4x3", "4:3"), Chip("img.crop:16x9", "16:9"),
                            Chip("img.crop:9x16", "9:16"),
                            Icon("img.crop:auto", "action.autoTrim", Glyphs.Crop, "hint.autoTrim"),
                            Icon("img.crop:free", "action.freeCrop", Glyphs.Edit, "hint.freeCrop"),
                        },
                    },
                    Icon("img.rotate", "action.rotate", Glyphs.Rotate) with
                    {
                        DefaultChildId = "img.rotate:90",
                        Children = new[]
                        {
                            Icon("img.rotate:90", "action.rotateRight", Glyphs.Rotate),
                            Icon("img.rotate:270", "action.rotateLeft", Glyphs.RotateLeft),
                            Icon("img.rotate:180", "action.rotate180", Glyphs.Rotate),
                            Icon("img.flip:h", "action.flipH", Glyphs.Flip),
                            Icon("img.flip:v", "action.flipV", Glyphs.Flip),
                        },
                    },
                    Icon("img.strip", "action.stripMeta", Glyphs.Hide, "hint.stripMeta"),
                    merge,
                    Icon("img.resize", "action.resize", Glyphs.Resize) with
                    {
                        DefaultChildId = "img.resize:50%",
                        Children = new[]
                        {
                            Chip("img.resize:25%", "25%"), Chip("img.resize:50%", "50%"), Chip("img.resize:75%", "75%"),
                            Chip("img.resize:1920", "1920px"), Chip("img.resize:1280", "1280px"), Chip("img.resize:800", "800px"),
                        },
                    },
                };
            }
            case FileKind.Video:
                return new[]
                {
                    Media(Icon("vid.compress", "action.compress", Glyphs.Compress) with
                    {
                        DefaultChildId = "vid.compress:balanced",
                        Children = new[]
                        {
                            Icon("vid.compress:light", "action.qualityLight", Glyphs.Compress, "hint.compressLight"),
                            Icon("vid.compress:balanced", "action.qualityBalanced", Glyphs.Compress, "hint.compressBalanced"),
                            Icon("vid.compress:strong", "action.qualityStrong", Glyphs.Compress, "hint.compressStrong"),
                        },
                    }),
                    Media(Icon("vid.extract", "action.extractAudio", Glyphs.Music, "hint.extractAudio")),
                    Media(Icon("vid.mute", "action.mute", Glyphs.Mute)),
                    Media(Icon("vid.snapshot", "action.snapshot", Glyphs.Camera, "hint.snapshot")),
                    Media(Icon("vid.rotate", "action.rotate", Glyphs.Rotate) with
                    {
                        DefaultChildId = "vid.rotate:90",
                        Children = new[]
                        {
                            Icon("vid.rotate:90", "action.rotateRight", Glyphs.Rotate),
                            Icon("vid.rotate:270", "action.rotateLeft", Glyphs.RotateLeft),
                            Icon("vid.rotate:180", "action.rotate180", Glyphs.Rotate),
                        },
                    }),
                    Media(Icon("vid.resize", "action.resize", Glyphs.Resize) with
                    {
                        DefaultChildId = "vid.resize:720",
                        Children = new[] { Chip("vid.resize:1080", "1080p"), Chip("vid.resize:720", "720p"), Chip("vid.resize:480", "480p") },
                    }),
                };
            case FileKind.Audio:
                return new[]
                {
                    Media(Icon("aud.compress", "action.compress", Glyphs.Compress, "hint.compressAudio")),
                    Media(Icon("aud.normalize", "action.normalize", Glyphs.Equalizer, "hint.normalize")),
                    Media(Icon("aud.trim", "action.trimSilence", Glyphs.Trim, "hint.trimSilence")),
                    Media(Icon("aud.mono", "action.mono", Glyphs.Volume)),
                };
            case FileKind.Pdf:
            {
                var merge = Icon("pdf.merge", "action.merge", Glyphs.Pdf);
                if (!p.IsMultiple) merge = merge.Disabled("reason.needsMultiple");
                return new[]
                {
                    Icon("pdf.compress", "action.compress", Glyphs.Compress, "hint.compressPdf"),
                    merge,
                    Icon("pdf.split", "action.split", Glyphs.Split, "hint.split"),
                    Icon("pdf.rotate", "action.rotate", Glyphs.Rotate) with
                    {
                        DefaultChildId = "pdf.rotate:90",
                        Children = new[]
                        {
                            Icon("pdf.rotate:90", "action.rotateRight", Glyphs.Rotate),
                            Icon("pdf.rotate:270", "action.rotateLeft", Glyphs.RotateLeft),
                            Icon("pdf.rotate:180", "action.rotate180", Glyphs.Rotate),
                        },
                    },
                    Icon("pdf.strip", "action.stripMeta", Glyphs.Hide, "hint.stripMeta"),
                };
            }
            case FileKind.Archive:
                return new[]
                {
                    Icon("arc.extract", "action.extractHere", Glyphs.Extract, "hint.extract"),
                    Hash(p),
                };
            default:
                return new[] { Icon("file.zip", "action.zip", Glyphs.Zip, "hint.zip"), Hash(p) };
        }
    }

    static ActionDef Hash(DragPayload p)
    {
        var a = Icon("file.hash", "action.hash", Glyphs.Hash, "hint.hash");
        return p.AllFiles ? a : a.Disabled("reason.filesOnly");
    }

    // ---------------------------------------------------------------- actions

    static IReadOnlyList<ActionDef> Actions(DragPayload p, Capabilities caps)
    {
        var moveTargets = new List<ActionDef>
        {
            Icon("act.move:desktop", "folder.desktop", Glyphs.Desktop),
            Icon("act.move:documents", "folder.documents", Glyphs.Document),
            Icon("act.move:downloads", "folder.downloads", Glyphs.Download),
            Icon("act.move:pictures", "folder.pictures", Glyphs.Picture),
        };
        foreach (var recent in caps.RecentFolders.Take(3))
        {
            moveTargets.Add(new ActionDef
            {
                Id = "act.move:" + recent,
                Literal = ShortFolderName(recent),
                Glyph = Glyphs.Folder,
            });
        }
        moveTargets.Add(Icon("act.move:browse", "action.browse", Glyphs.Browse));

        return new[]
        {
            Icon("act.share", "action.share", Glyphs.Share, "hint.share"),
            Icon("act.copypath", "action.copyPath", Glyphs.Link),
            p.IsMultiple ? Icon("act.rename", "action.rename", Glyphs.Rename).Disabled("reason.singleOnly") : Icon("act.rename", "action.rename", Glyphs.Rename),
            Icon("act.move", "action.moveTo", Glyphs.MoveTo) with { Children = moveTargets, DefaultChildId = "act.move:browse" },
            Icon("act.terminal", "action.terminal", Glyphs.Terminal),
            Icon("act.shelf", "action.shelf", Glyphs.Pin, "hint.shelf"),
            Icon("file.zip", "action.zip", Glyphs.Zip, "hint.zip"),
        };
    }

    static string ShortFolderName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path;
        return name.Length > 14 ? name[..13] + "…" : name;
    }

    /// <summary>Every action reachable from any ring, for invariant tests.</summary>
    public static IEnumerable<ActionDef> Flatten(IEnumerable<ActionDef> ring)
    {
        foreach (var a in ring)
        {
            yield return a;
            foreach (var c in Flatten(a.Children)) yield return c;
        }
    }
}

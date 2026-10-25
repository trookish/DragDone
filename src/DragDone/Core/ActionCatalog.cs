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
                return DocumentConvert(p, caps);
            default:
                return Array.Empty<ActionDef>();
        }
    }

    /// <summary>
    /// Keeps a ring inside <see cref="MaxSectors"/>: the last sectors move into a "more" submenu so
    /// each wedge stays wide enough to hit.
    /// </summary>
    static IReadOnlyList<ActionDef> Ring(IReadOnlyList<ActionDef> items, string moreId)
    {
        if (items.Count <= MaxSectors) return items;
        var head = items.Take(MaxSectors - 1).ToList();
        head.Add(new ActionDef
        {
            Id = moreId,
            LabelKey = "action.more",
            Glyph = Glyphs.More,
            GlyphOnly = true,
            Children = items.Skip(MaxSectors - 1).ToList(),
        });
        return head;
    }

    /// <summary>A format sector whose label is translated (used where the label is two words).</summary>
    static ActionDef Labelled(string id, string labelKey, string? needs)
    {
        var a = new ActionDef { Id = id, LabelKey = labelKey, Style = ActionStyle.Format };
        return needs is null ? a : a.Disabled(needs);
    }

    /// <summary>
    /// The convert ring for a document. Word, Excel, PowerPoint, Visio and Publisher each have
    /// their own set of targets and their own application; every sector is dimmed with the name of
    /// the missing app when it is not installed. Plain text stays on the text path, and CSV — which
    /// is a text file — gains Excel conversions only when Excel is really there.
    /// </summary>
    static IReadOnlyList<ActionDef> DocumentConvert(DragPayload p, Capabilities caps)
    {
        var family = p.Office;
        var need = family == OfficeFamily.None || caps.Can(family) ? null : Capabilities.MissingKey(family);
        switch (family)
        {
            case OfficeFamily.Word:
                return Ring(new[]
                {
                    Fmt("pdf", p, need), Fmt("docx", p, need), Fmt("doc", p, need), Fmt("rtf", p, need),
                    Fmt("odt", p, need), Fmt("txt", p, need), Fmt("html", p, need), Fmt("xps", p, need),
                }, "convert:more");

            case OfficeFamily.Excel:
                return Ring(new[]
                {
                    Fmt("pdf", p, need), Fmt("xlsx", p, need), Fmt("xls", p, need), Fmt("csv", p, need),
                    Fmt("ods", p, need), Fmt("html", p, need), Fmt("xps", p, need),
                }, "convert:more");

            case OfficeFamily.PowerPoint:
                // No MP4: PowerPoint's CreateVideo is asynchronous, needs the desktop app and a
                // working video driver, and reports failure through a status poll with no reason —
                // a video sector would promise something the ring cannot guarantee.
                return Ring(new[]
                {
                    Fmt("pdf", p, need), Fmt("pptx", p, need), Fmt("ppt", p, need), Fmt("odp", p, need),
                    Fmt("xps", p, need),
                    Labelled("convert:pngslides", "convert.pngSlides", need),
                    Labelled("convert:jpgslides", "convert.jpgSlides", need),
                }, "convert:more");

            case OfficeFamily.Visio:
                return new[] { Fmt("pdf", p, need) };

            case OfficeFamily.Publisher:
                return new[] { Fmt("pdf", p, need) };

            default:
            {
                var pdf = Fmt("pdf", p);
                var office = p.Paths.Where(FileKinds.IsOfficeDocument).Select(FileKinds.Office).ToList();
                var unsupported = p.Paths.Any(x => !FileKinds.IsOfficeDocument(x) && !FileKinds.IsPlainText(x));
                if (unsupported) pdf = pdf.Disabled("reason.unsupported");
                // A mixed drag is only as available as the least available file in it.
                else if (office.FirstOrDefault(f => !caps.Can(f)) is var missing && missing != OfficeFamily.None)
                    pdf = pdf.Disabled(Capabilities.MissingKey(missing));

                if (p.AllFiles && p.Paths.All(FileKinds.IsSpreadsheetText) && caps.Can(OfficeFamily.Excel))
                    return new[] { pdf, Fmt("xlsx", p), Fmt("xls", p), Fmt("ods", p) };

                return new[] { pdf };
            }
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
            case FileKind.Document:
                return DocumentTools(p, caps);
            default:
                return new[] { Icon("file.zip", "action.zip", Glyphs.Zip, "hint.zip"), Hash(p) };
        }
    }

    /// <summary>
    /// The tools ring for documents. The OOXML sectors read the package directly, so they work on a
    /// machine with no Office at all; the two COM sectors appear only when their app is installed,
    /// because pretending otherwise would be a dead button.
    /// </summary>
    static IReadOnlyList<ActionDef> DocumentTools(DragPayload p, Capabilities caps)
    {
        var items = new List<ActionDef>();
        if (p.AllFiles && p.Paths.All(FileKinds.IsOpenXml))
        {
            items.Add(Icon("doc.strip", "action.stripDocMeta", Glyphs.Hide, "hint.stripDocMeta"));
            items.Add(Icon("doc.images", "action.extractImages", Glyphs.Picture, "hint.extractImages"));
            items.Add(Icon("doc.text", "action.extractText", Glyphs.Document, "hint.extractText"));
            items.Add(Icon("doc.stats", "action.docStats", Glyphs.Page, "hint.docStats"));
            items.Add(Icon("doc.compress", "action.compress", Glyphs.Compress, "hint.compressDoc"));
            if (p.Office == OfficeFamily.Excel && caps.Can(OfficeFamily.Excel))
                items.Add(Icon("doc.sheets", "action.eachSheetCsv", Glyphs.Split, "hint.eachSheetCsv"));
            if (p.Office == OfficeFamily.PowerPoint && caps.Can(OfficeFamily.PowerPoint))
                items.Add(Icon("doc.slides", "action.slidesToImages", Glyphs.Camera, "hint.slidesToImages"));
        }
        items.Add(Icon("file.zip", "action.zip", Glyphs.Zip, "hint.zip"));
        items.Add(Hash(p));
        return Ring(items, "doc.more");
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
            p.AllFiles
                ? Icon("act.print", "action.print", Glyphs.Print, "hint.print")
                : Icon("act.print", "action.print", Glyphs.Print, "hint.print").Disabled("reason.filesOnly"),
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

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>What "Locate in 3D" managed to do. Revit-free so the messages
    /// are unit-tested; the Revit side is the panel's LocateInRevitAsync.</summary>
    public enum JkrLocateKind
    {
        /// <summary>Shown and selected.</summary>
        Shown,
        /// <summary>The issue points at a type: its placed instances were shown.</summary>
        ShownTypeInstances,
        /// <summary>The issue points at a type with no placed instances.</summary>
        TypeNotPlaced,
        /// <summary>Selected, but hidden in the view Revit chose (hidden element or category).</summary>
        HiddenInView,
        /// <summary>Revit could not show it (no view shows it); it is still selected.</summary>
        NotShown,
        /// <summary>No open view shows it: selected only, without ShowElements (which
        /// raises Revit's own "There is no open view that shows…" / "No good view" boxes).
        /// <see cref="JkrLocateResult.Detail"/> is the element's label.</summary>
        NotVisible,
        /// <summary>The issue is about a view (sheet, schedule, plan…): it was opened.</summary>
        OpenedView,
        /// <summary>The issue is about a view template, which cannot be opened.</summary>
        ViewTemplate,
        /// <summary>Not a view and no geometry (e.g. Project Information): selected
        /// only. ShowElements would raise Revit's own "No good view could be found".</summary>
        NoGeometry,
        /// <summary>No element with that id in the model.</summary>
        Missing,
        NoDocument,
        NoElement,
    }

    public sealed class JkrLocateResult
    {
        public JkrLocateKind Kind { get; }
        /// <summary>Instances shown, for <see cref="JkrLocateKind.ShownTypeInstances"/>.</summary>
        public int Count { get; }
        /// <summary>Type name, view name or Revit's message, depending on <see cref="Kind"/>.</summary>
        public string Detail { get; }

        public JkrLocateResult(JkrLocateKind kind, int count = 0, string detail = "")
        {
            Kind = kind;
            Count = count;
            Detail = detail ?? "";
        }

        /// <summary>Toast text in the pane's language; null when nothing needs saying (plain success).</summary>
        public string Message => MessageIn(RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Current);

        /// <summary>Toast text in BM ("ms") or EN ("en").</summary>
        public string MessageIn(string lang)
        {
            var en = string.Equals(lang, "en", System.StringComparison.OrdinalIgnoreCase);
            string L(string ms, string e) => en ? e : ms;
            switch (Kind)
            {
                case JkrLocateKind.Shown:
                    return null;
                case JkrLocateKind.ShownTypeInstances:
                    return L($"Isu ini pada jenis (type). {Count} instance yang diletakkan telah dipilih.",
                             $"This issue is on a type. Selected its {Count} placed instance{(Count == 1 ? "" : "s")}.");
                case JkrLocateKind.TypeNotPlaced:
                    return L($"Isu ini pada jenis '{Detail}', yang tiada instance diletakkan untuk ditunjukkan.",
                             $"This issue is on the type '{Detail}', which has no placed instances to show.");
                case JkrLocateKind.HiddenInView:
                    return L($"Dipilih, tetapi ia tersembunyi dalam paparan '{Detail}' (elemen atau kategori disembunyikan).",
                             $"Selected, but it is hidden in the view '{Detail}' (hidden element or category).");
                case JkrLocateKind.NotShown:
                    return string.IsNullOrEmpty(Detail)
                        ? L("Dipilih, tetapi Revit tidak menemui paparan yang menunjukkannya.",
                            "Selected, but Revit could not find a view that shows it.")
                        : L($"Dipilih, tetapi Revit tidak dapat menunjukkannya: {Detail}",
                            $"Selected, but Revit could not show it: {Detail}");
                case JkrLocateKind.NotVisible:
                    return RevitWebAppSync.UI.Jkr.Panel.Core.PanelStrings.Get(en ? "en" : "ms", "toastLoc") + ": " + Detail
                           + L(" — tidak kelihatan dalam paparan semasa.", " — not visible in the current view.");
                case JkrLocateKind.OpenedView:
                    return Count > 1
                        ? L($"Isu ini menyenaraikan {Count} paparan. Paparan pertama dibuka: '{Detail}'.",
                            $"This issue lists {Count} views. Opened the first: '{Detail}'.")
                        : L($"Paparan '{Detail}' dibuka.", $"Opened the view '{Detail}'.");
                case JkrLocateKind.ViewTemplate:
                    return L($"'{Detail}' ialah templat paparan (view template), yang tidak boleh dibuka. Cari di View → View Templates → Manage View Templates.",
                             $"'{Detail}' is a view template, which can't be opened. Find it under View → View Templates → Manage View Templates.");
                case JkrLocateKind.NoGeometry:
                    return L($"'{Detail}' tiada geometri untuk ditunjukkan dalam paparan. Ia telah dipilih.",
                             $"'{Detail}' has no geometry to show in a view. It is selected.");
                case JkrLocateKind.Missing:
                    return L("Elemen tiada lagi dalam model — cuba imbas semula.",
                             "Element no longer exists in the model — try re-scanning.");
                case JkrLocateKind.NoDocument:
                    return L("Tiada dokumen Revit aktif — buka model dan cuba lagi.",
                             "No active Revit document — open a model and try again.");
                default:
                    return L("Isu ini tiada elemen yang dipautkan untuk ditunjukkan.",
                             "This issue has no linked element to locate.");
            }
        }
    }
}

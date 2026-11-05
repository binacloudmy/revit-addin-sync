// Pane [Undo] state for the latest Copilot change — Revit-free so it is
// unit-tested (AI-202).
//
// Each approved mutate batch is ONE named TransactionGroup, so it is ONE entry
// in Revit's undo list and the pane posts Revit's Undo exactly once. That is
// only safe while our entry is still on top of the undo list. Revit has no API
// to read the undo list, so ReceiptUndo feeds every DocumentChanged on the
// target document in here and this class keeps the answer:
//   commit after ours (drafter edit, another tool)  -> superseded, refuse
//   undo while ours is on top                       -> undone (pane or Ctrl+Z)
//   redo right after that undo                      -> live again
// Static state dies with Revit, so a receipt from an earlier session is
// refused with its own reason.

using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    public enum UndoStatus { None, Live, Posted, Undone, Superseded, Unavailable, DocumentClosed }

    public sealed class ReceiptUndoState
    {
        private readonly object _lock = new object();

        public string ReceiptId { get; private set; } = "";
        public UndoStatus Status { get; private set; } = UndoStatus.None;
        /// <summary>True while a different document is active (soft: the
        /// undo list is per document, so switching back restores Undo).</summary>
        public bool OtherDocumentActive { get; private set; }
        private bool _armedThisSession;

        /// <summary>Our group was assimilated as one undo entry and is on top.</summary>
        public void Arm(string receiptId)
        {
            lock (_lock)
            {
                ReceiptId = receiptId ?? "";
                Status = UndoStatus.Live;
                OtherDocumentActive = false;
                _armedThisSession = true;
            }
        }

        /// <summary>The batch did not land as one undo entry (no document,
        /// group could not assimilate) — the pane must not post Undo for it.</summary>
        public void MarkUnavailable(string receiptId)
        {
            lock (_lock)
            {
                ReceiptId = receiptId ?? "";
                Status = UndoStatus.Unavailable;
                _armedThisSession = true;
            }
        }

        public void OnPosted() { lock (_lock) { if (Status == UndoStatus.Live) Status = UndoStatus.Posted; } }

        public void OnCommitted()
        {
            lock (_lock)
            {
                if (Status == UndoStatus.Live || Status == UndoStatus.Posted || Status == UndoStatus.Undone)
                    Status = UndoStatus.Superseded;
            }
        }

        public void OnUndone()
        {
            lock (_lock)
            {
                if (Status == UndoStatus.Live || Status == UndoStatus.Posted) Status = UndoStatus.Undone;
                // A second undo goes below our entry; a redo would no longer
                // land on ours first, so stop tracking it.
                else if (Status == UndoStatus.Undone) Status = UndoStatus.Superseded;
            }
        }

        public void OnRedone()
        {
            lock (_lock)
            {
                if (Status == UndoStatus.Undone) Status = UndoStatus.Live;
                else if (Status == UndoStatus.Live || Status == UndoStatus.Posted) Status = UndoStatus.Superseded;
            }
        }

        public void OnDocumentClosed()
        {
            lock (_lock)
            {
                if (Status != UndoStatus.None && Status != UndoStatus.Unavailable) Status = UndoStatus.DocumentClosed;
            }
        }

        public void SetOtherDocumentActive(bool other) { lock (_lock) { OtherDocumentActive = other; } }

        /// <summary>True when the pane may post Undo for this receipt.</summary>
        public bool CanUndo(string receiptId) => WhyNot(receiptId) == null;

        /// <summary>One plain line saying why [Undo] is not offered for this
        /// receipt, or null when it may run.</summary>
        public string WhyNot(string receiptId)
        {
            lock (_lock)
            {
                if (!_armedThisSession || string.IsNullOrEmpty(receiptId))
                    return "Undo tidak tersedia — perubahan ini dari sesi sebelum Revit dimulakan semula. Guna Undo Revit (Ctrl+Z).";
                if (receiptId != ReceiptId)
                    return "Undo di sini hanya untuk perubahan Copilot yang terkini. Guna Undo Revit (Ctrl+Z).";
                switch (Status)
                {
                    case UndoStatus.Live:
                        return OtherDocumentActive
                            ? "Dokumen lain sedang aktif — kembali ke dokumen asal untuk Undo."
                            : null;
                    case UndoStatus.Posted:
                    case UndoStatus.Undone:
                        return "Perubahan ini sudah di-undo.";
                    case UndoStatus.Superseded:
                        return "Ada perubahan lain dalam model selepas ini, jadi Undo di sini ditutup supaya sejarah Revit kekal betul. Guna Undo Revit (Ctrl+Z).";
                    case UndoStatus.DocumentClosed:
                        return "Dokumen asal sudah ditutup — Undo tidak lagi tersedia.";
                    default:
                        return "Undo tidak tersedia untuk perubahan ini. Guna Undo Revit (Ctrl+Z).";
                }
            }
        }

        // Tools that save, link, load families or open/close other documents.
        // Revit refuses those while a TransactionGroup is open, so a batch
        // holding one runs ungrouped (and its Undo chip explains why).
        private static readonly HashSet<string> NoGroupTools = new HashSet<string>
        {
            "save_document_as", "batch_link_models", "link_file", "load_family",
            "dwg_open_attachment", "pdf_open_attachment", "extract_cad_geometry",
            "cad_walls_to_centerlines", "cad_walls_from_attachment",
            "cad_doors_from_attachment", "cad_windows_from_attachment",
        };

        /// <summary>True when every tool of the batch may run inside one
        /// TransactionGroup.</summary>
        public static bool BatchCanGroup(IEnumerable<string> tools)
        {
            if (tools == null) return true;
            foreach (var t in tools)
                if (t != null && (NoGroupTools.Contains(t) || t.StartsWith("get_dwg_"))) return false;
            return true;
        }

        /// <summary>Name shown in Revit's undo list for the batch group: the
        /// single transaction's own name, or the first plus a count.</summary>
        public static string GroupName(IReadOnlyList<string> txNames)
        {
            var names = new List<string>();
            if (txNames != null)
                foreach (var n in txNames)
                    if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n)) names.Add(n);
            if (names.Count == 0) return "BinaVibe: perubahan Copilot";
            if (names.Count == 1) return names[0];
            return $"{names[0]} (+{names.Count - 1} lagi)";
        }
    }
}

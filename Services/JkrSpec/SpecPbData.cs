using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>
    /// One parameter row of a Buku Parameter table: the name as printed, the page it is on,
    /// and the LOi columns ticked (the book ticks a contiguous run, e.g. LOi 200–500).
    /// <see cref="FromLoi"/> = 0 when the book ticks no LOi for it (a transcription gap,
    /// listed in <see cref="SpecPbData.Gaps"/>).
    /// </summary>
    public sealed class PbField
    {
        public PbField(string name, int page, int fromLoi, int toLoi)
        {
            Name = name ?? ""; Page = page; FromLoi = fromLoi; ToLoi = toLoi;
        }

        /// <summary>Parameter name as printed in the book ("Arkitek_jkr_sit", "Project Name").</summary>
        public string Name { get; }
        /// <summary>Buku Parameter page (the book's own page number, not the PDF's).</summary>
        public int Page { get; }
        /// <summary>First LOi ticked (100–500); 0 = none ticked.</summary>
        public int FromLoi { get; }
        /// <summary>Last LOi ticked (100–500); 0 = none ticked.</summary>
        public int ToLoi { get; }
        public bool HasLoi => FromLoi > 0 && ToLoi >= FromLoi;

        /// <summary>The book marks this field at <paramref name="loi"/> (100, 200 … 500).</summary>
        public bool RequiredAt(int loi) => HasLoi && loi >= FromLoi && loi <= ToLoi;

        /// <summary>"100–500", "500", "?" (none ticked).</summary>
        public string LoiRange => !HasLoi ? "?" : FromLoi == ToLoi ? FromLoi.ToString() : FromLoi + "–" + ToLoi;

        public override string ToString() => Name + " (PB p." + Page + ", LOi " + LoiRange + ")";
    }

    /// <summary>An AR category of the Buku Parameter (PB p.36–99) and the Revit category
    /// names that count as it (in <see cref="SpecModel.CategoryCounts"/> and schedule categories).</summary>
    public sealed class PbCategory
    {
        public PbCategory(string name, int page, params string[] revitCategories)
        {
            Name = name; Page = page; RevitCategories = revitCategories ?? new string[0];
        }

        /// <summary>The book's category name ("Railing").</summary>
        public string Name { get; }
        public int Page { get; }
        /// <summary>Revit category names, English ("Railings").</summary>
        public IReadOnlyList<string> RevitCategories { get; }

        public bool Matches(string revitCategory) =>
            !string.IsNullOrWhiteSpace(revitCategory)
            && RevitCategories.Any(c => string.Equals(c, revitCategory.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Buku Parameter (BIM: Spesifikasi Parameter JKR) data the spec checks need:
    /// the Project Information fields by LOi (PB p.30–34) and the AR category list
    /// (PB p.36–103, sheet "Buku Parameter (AR)"). Static, transcribed from the book.
    /// <para>
    /// Transcribed from the book's text layer (pdftotext -layout): each ✓ was assigned to
    /// the LOi column whose header it sits under; ticks in the "Maklumat Parameter (Contoh)"
    /// column (the example value of a Yes/No parameter) are not LOi ticks. Page numbers are
    /// the book's printed numbers (PDF page = book page + 11). What could not be read
    /// reliably is in <see cref="Gaps"/>.
    /// </para>
    /// </summary>
    public static class SpecPbData
    {
        private static PbField F(string name, int page, int fromLoi, int toLoi) => new PbField(name, page, fromLoi, toLoi);

        /// <summary>AR, Category: Project Information — PB p.30–34 ("SENARAI PARAMETER ARKITEK").</summary>
        public static readonly IReadOnlyList<PbField> ProjectInfoAr = new[]
        {
            // p.30 Identity Data · p.31 Model Properties, General, Other · p.32 Other (cont.), Data · p.33–34 Data (cont.)
            F("Building Name", 30, 100, 500),
            F("Arkitek_jkr_sit", 30, 100, 500),
            F("Arkitek_Kanan_jkr_sit", 30, 100, 500),
            F("Arkitek_Penguasa_jkr_sit", 30, 100, 500),
            F("Arkitek_Penguasa_Kanan_jkr_sit", 30, 100, 500),
            F("Pengarah_AR_jkr_sit", 30, 100, 500),
            F("Pengarah_Kanan_AR_jkr_sit", 30, 100, 500),
            F("Pengarah_Projek_jkr_sit", 30, 100, 500),
            F("Perunding_Alam_Sekitar_jkr_sit", 30, 200, 500),
            F("Perunding_Arkitek_jkr_sit", 30, 200, 500),
            F("Perunding_Arkitek_Landskap_jkr_sit", 30, 200, 500),
            F("Perunding_Elektrik_jkr_sit", 30, 200, 500),
            F("Perunding_Informasi_Teknologi_jkr_sit", 30, 200, 500),
            F("Perunding_Juru_Ukur_Bahan_jkr_sit", 30, 200, 500),
            F("Perunding_Jurutera_Awam_dan_Struktur_jkr_sit", 30, 200, 500),
            F("Perunding_Mekanikal_jkr_sit", 30, 200, 500),
            F("Perunding_Peralatan_Perubatan_jkr_sit", 30, 200, 500),
            F("Perunding_Perancangan_Ruang_Perubatan_jkr_sit", 30, 200, 500),
            F("Kedudukan_Project_Base_Point_jkr_sit", 31, 100, 500),
            F("No_Fail_AR_jkr_sit", 31, 100, 500),
            F("No_Model_BIM_AR_jkr_sit", 31, 100, 500),
            F("Template_Asal_jkr_sit", 31, 100, 500),
            F("URL_Projek_jkr_siu", 31, 300, 500),
            F("Bilangan_Kapasiti_jkr_sit", 31, 200, 500),
            F("Bilangan_Parkir_Diperlukan_jkr_sii", 31, 100, 500),
            F("Bilangan_Parkir_Disediakan_jkr_sii", 31, 300, 500),
            F("Luas_Brif_Projek_jkr_sia", 31, 100, 500),
            F("Kumpulan_Maksud_Bangunan_jkr_sit", 31, 100, 500),
            F("Luas_Bersih_NFA_jkr_sia", 31, 300, 500),
            F("Luas_Kasar_GFA_jkr_sia", 31, 200, 500),
            F("Luas_Tapak_Bangunan_jkr_sia", 31, 200, 500),
            F("Luas_Tapak_Projek_jkr_sia", 31, 200, 500),
            F("Alamat_Pelanggan_jkr_six", 31, 100, 500),
            F("Client Name", 31, 100, 500),
            F("Project Address", 31, 100, 500),
            F("Project Name", 32, 100, 500),
            F("Kos_Projek_jkr_sic", 32, 100, 500),
            F("Harga_Kontrak_jkr_sic", 32, 400, 500),
            F("Tarikh_Milik_Tapak_jkr_sit", 32, 300, 500),
            F("Tarikh_Kelulusan_Bomba_jkr_sit", 32, 300, 500),
            F("Tarikh_Kelulusan_PBT_jkr_sit", 32, 300, 500),
            F("Tarikh_Kelulusan_Pelanggan_jkr_sit", 32, 300, 500),
            F("Tarikh_CMGD_jkr_sit", 32, 400, 500),
            F("Tarikh_CPC_jkr_sit", 32, 400, 500),
            F("Tarikh_DLP_jkr_sit", 32, 400, 500),
            F("Nama_Premis_jkr_sit", 32, 400, 500),
            F("Alamat_Premis_jkr_sit", 32, 400, 500),
            F("Poskod_jkr_sit", 32, 400, 500),
            F("Koordinat_GPS_X_jkr_sit", 32, 400, 500),
            F("Koordinat_GPS_Y_jkr_sit", 32, 400, 500),
            F("Kumpulan_Agensi_jkr_sit", 32, 400, 500),
            F("Kementerian_jkr_sit", 32, 400, 500),
            F("Jabatan_jkr_sit", 32, 400, 500),
            F("Negara_jkr_sit", 32, 400, 500),
            F("Negeri_jkr_sit", 32, 400, 500),
            F("Daerah_jkr_sit", 32, 400, 500),
            F("Mukim_Bandar_jkr_sit", 32, 400, 500),
            F("Kategori_Premis_jkr_sit", 32, 400, 500),
            F("Sub_Kategori_Premis_jkr_sit", 32, 400, 500),
            F("Jumlah_Keluasan_Premis_jkr_sia", 33, 400, 500),
            F("Status_Premis_jkr_sit", 33, 500, 500),
            F("Aset_Warisan_jkr_siy", 33, 400, 500),
            F("Kos_Bina_Asal_jkr_sic", 33, 400, 500),
            F("Kos_Tambahan_jkr_sic", 33, 500, 500),
            F("Kos_Keseluruhan_jkr_sic", 33, 500, 500),
            F("Tahun_Siap_Bina_Asal_jkr_sit", 33, 400, 500),
            F("Tarikh_Siap_Bina_Asal_jkr_sit", 33, 400, 500),
            F("Sumber_Pembiayaan_jkr_sit", 33, 400, 500),
            F("Kod_PTJ_jkr_sit", 33, 400, 500),
            F("Nilai_Semasa_jkr_sic", 33, 500, 500),
            F("Tarikh_Penilaian_jkr_sit", 33, 500, 500),
            F("Bilangan_Blok_Bangunan_jkr_sii", 33, 400, 500),
            F("Bilangan_Binaan_Luar_jkr_sii", 33, 400, 500),
            F("No_DPA_jkr_sit", 33, 500, 500),
            F("Kod_Blok_jkr_sit", 33, 400, 500),
            F("Kod_Binaan_Luar_jkr_sit", 33, 400, 500),
            F("Nama_Blok_jkr_sit", 33, 400, 500),
            F("Nama_Binaan_Luar_jkr_sit", 33, 400, 500),
            F("Fungsi_Binaan_jkr_sit", 33, 400, 500),
            F("Jenis_Binaan_Luar_jkr_sit", 33, 400, 500),
            F("Kontraktor_Utama_jkr_sit", 33, 400, 500),
            F("Kontraktor_Tambahan_jkr_six", 33, 400, 500),
            F("Bidang_Kerja_Kontraktor_Utama_jkr_sit", 33, 400, 500),
            F("Bidang_Kerja_Kontraktor_Tambahan_jkr_six", 33, 400, 500),
            F("Juru_Perunding_Utama_jkr_sit", 33, 400, 500),
            F("Juru_Perunding_Tambahan_jkr_sit", 34, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Utama_jkr_sit", 34, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Tambahan_jkr_sit", 34, 400, 500),
            F("Fungsi_Asal_jkr_sit", 34, 400, 500),
            F("Jenis_Struktur_jkr_sit", 34, 400, 500),
            F("No_Siri_Pendaftaran_jkr_sit", 34, 500, 500),
            F("Jangka_Hayat_Tahun_jkr_sii", 34, 400, 500),
            F("Kapasiti_Penghuni_Asal_jkr_sit", 34, 400, 500),
            F("Penggunaan_Tenaga_jkr_sit", 34, 500, 500),
            F("Penggunaan_Air_jkr_sit", 34, 500, 500),
            F("Jenis_Milikan_jkr_sit", 34, 400, 500),
            F("Status_Blok_jkr_sit", 34, 500, 500),
            F("Status_Binaan_Luar_jkr_sit", 34, 500, 500),
            F("Bilangan_Aras_Atas_Tanah_jkr_sii", 34, 400, 500),
            F("Bilangan_Aras_Bawah_Tanah_jkr_sii", 34, 400, 500),
            F("Binaan_Luar_jkr_siy", 34, 400, 500),
        };

        /// <summary>ST, Category: Project Information — PB p.120–123 ("SENARAI PARAMETER STRUKTUR").
        /// The ST table starts at "Other": it has no Identity Data rows of its own.</summary>
        public static readonly IReadOnlyList<PbField> ProjectInfoSt = new[]
        {
            F("Alamat_Pelanggan_jkr_six", 120, 100, 500),
            F("Client Name", 120, 100, 500),
            F("Project Address", 120, 100, 500),
            F("Project Name", 120, 100, 500),
            F("Kos_Projek_jkr_sic", 120, 100, 500),
            F("Harga_Kontrak_jkr_sic", 120, 300, 500),
            F("Kedudukan_Project_Base_Point_jkr_sit", 120, 100, 500),
            F("Tarikh_Milik_Tapak_jkr_sit", 120, 300, 500),
            F("Tarikh_Kelulusan_Bomba_jkr_sit", 120, 300, 500),
            F("Tarikh_Kelulusan_PBT_jkr_sit", 120, 300, 500),
            F("Tarikh_Kelulusan_Pelanggan_jkr_sit", 120, 300, 500),
            F("Tarikh_CMGD_jkr_sit", 120, 400, 500),
            F("Tarikh_CPC_jkr_sit", 120, 400, 500),
            F("Template_Asal_jkr_sit", 120, 100, 500),
            F("URL_Projek_jkr_siu", 120, 300, 500),
            F("Nama_Premis_jkr_sit", 121, 400, 500),
            F("Alamat_Premis_jkr_sit", 121, 400, 500),
            F("Poskod_jkr_sit", 121, 400, 500),
            F("Koordinat_GPS_X_jkr_sit", 121, 400, 500),
            F("Koordinat_GPS_Y_jkr_sit", 121, 400, 500),
            F("Kumpulan_Agensi_jkr_sit", 121, 400, 500),
            F("Kementerian_jkr_sit", 121, 400, 500),
            F("Jabatan_jkr_sit", 121, 400, 500),
            F("Negara_jkr_sit", 121, 400, 500),
            F("Negeri_jkr_sit", 121, 400, 500),
            F("Daerah_jkr_sit", 121, 400, 500),
            F("Mukim_Bandar_jkr_sit", 121, 400, 500),
            F("Kategori_Premis_jkr_sit", 121, 400, 500),
            F("Sub_Kategori_Premis_jkr_sit", 121, 400, 500),
            F("Jumlah_Keluasan_Premis_jkr_sia", 121, 400, 500),
            F("Status_Premis_jkr_sit", 121, 500, 500),
            F("Aset_Warisan_jkr_siy", 121, 400, 500),
            F("Kos_Bina_Asal_jkr_sic", 121, 400, 500),
            F("Kos_Tambahan_jkr_sic", 121, 500, 500),
            F("Kos_Keseluruhan_jkr_sic", 121, 500, 500),
            F("Tahun_Siap_Bina_Asal_jkr_sit", 121, 400, 500),
            F("Tarikh_Siap_Bina_Asal_jkr_sit", 121, 400, 500),
            F("Sumber_Pembiayaan_jkr_sit", 121, 400, 500),
            F("Kod_PTJ_jkr_sit", 121, 400, 500),
            F("Nilai_Semasa_jkr_sic", 121, 500, 500),
            F("Tarikh_Penilaian_jkr_sit", 121, 500, 500),
            F("Bilangan_Blok_Bangunan_jkr_sii", 122, 400, 500),
            F("Bilangan_Binaan_Luar_jkr_sii", 122, 400, 500),
            F("No_DPA_jkr_sit", 122, 500, 500),
            F("Kod_Blok_jkr_sit", 122, 400, 500),
            F("Kod_Binaan_Luar_jkr_sit", 122, 400, 500),
            F("Nama_Blok_jkr_sit", 122, 400, 500),
            F("Nama_Binaan_Luar_jkr_sit", 122, 400, 500),
            F("Fungsi_Binaan_jkr_sit", 122, 400, 500),
            F("Jenis_Binaan_Luar_jkr_sit", 122, 400, 500),
            F("Kontraktor_Utama_jkr_sit", 122, 400, 500),
            F("Kontraktor_Tambahan_jkr_six", 122, 400, 500),
            F("Bidang_Kerja_Kontraktor_Utama_jkr_sit", 122, 400, 500),
            F("Bidang_Kerja_Kontraktor_Tambahan_jkr_six", 122, 400, 500),
            F("Juru_Perunding_Utama_jkr_sit", 122, 400, 500),
            F("Juru_Perunding_Tambahan_jkr_sit", 122, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Utama_jkr_sit", 122, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Tambahan_jkr_sit", 122, 400, 500),
            F("Fungsi_Asal_jkr_sit", 122, 400, 500),
            F("Jenis_Struktur_jkr_sit", 122, 400, 500),
            F("No_Siri_Pendaftaran_jkr_sit", 122, 500, 500),
            F("Jangka_Hayat_Tahun_jkr_sii", 122, 400, 500),
            F("Kapasiti_Penghuni_Asal_jkr_sit", 122, 400, 500),
            F("Penggunaan_Tenaga_jkr_sit", 122, 500, 500),
            F("Penggunaan_Air_jkr_sit", 122, 500, 500),
            F("Jenis_Milikan_jkr_sit", 123, 400, 500),
            F("Status_Blok_jkr_sit", 123, 500, 500),
            F("Status_Binaan_Luar_jkr_sit", 123, 500, 500),
            F("Bilangan_Aras_Atas_Tanah_jkr_sii", 123, 400, 500),
            F("Bilangan_Aras_Bawah_Tanah_jkr_sii", 123, 400, 500),
            F("Binaan_Luar_jkr_siy", 123, 400, 500),
        };

        /// <summary>ME, Category: Project Information — PB p.161–164 ("SENARAI PARAMETER MEKANIKAL").</summary>
        public static readonly IReadOnlyList<PbField> ProjectInfoMe = new[]
        {
            // p.161: "Pengarah_Kanan_Mekanikal" is printed without a _jkr_sit suffix (kept as printed).
            // p.163: the ticks of Bilangan_Blok_Bangunan_jkr_sii sit one line above its name in the
            //        text layer; read as LOi 400–500, under the same columns as in AR/ST/EL.
            F("Jurutera_Mekanikal_Kanan_jkr_sit", 161, 300, 500),
            F("Jurutera_Mekanikal_Penguasa_jkr_sit", 161, 300, 500),
            F("Jurutera_Mekanikal_Penguasa_Kanan_jkr_sit", 161, 300, 500),
            F("No_Fail_ME_jkr_sit", 161, 300, 500),
            F("Pegawai_Penguasa_Mekanikal_jkr_sit", 161, 400, 500),
            F("Pengarah_Kanan_Mekanikal", 161, 300, 500),
            F("Pengarah_Rekabentuk_Mekanikal_jkr_sit", 161, 300, 500),
            F("Penolong_Pegawai_Penguasa_Mekanikal_jkr_sit", 161, 400, 500),
            F("Sistem_Mekanikal_jkr_sit", 161, 300, 500),
            F("Alamat_Pelanggan_jkr_six", 161, 100, 500),
            F("Client Name", 161, 100, 500),
            F("Project Address", 161, 100, 500),
            F("Project Name", 161, 100, 500),
            F("Kos_Projek_jkr_sic", 161, 100, 500),
            F("Harga_Kontrak_jkr_sic", 162, 300, 500),
            F("Kedudukan_Project_Base_Point_jkr_sit", 162, 100, 500),
            F("Tarikh_Milik_Tapak_jkr_sit", 162, 300, 500),
            F("Tarikh_Kelulusan_Bomba_jkr_sit", 162, 300, 500),
            F("Tarikh_Kelulusan_PBT_jkr_sit", 162, 300, 500),
            F("Tarikh_Kelulusan_Pelanggan_jkr_sit", 162, 300, 500),
            F("Tarikh_CMGD_jkr_sit", 162, 400, 500),
            F("Tarikh_CPC_jkr_sit", 162, 400, 500),
            F("Template_Asal_jkr_sit", 162, 100, 500),
            F("URL_Projek_jkr_siu", 162, 300, 500),
            F("Nama_Premis_jkr_sit", 162, 400, 500),
            F("Alamat_Premis_jkr_sit", 162, 400, 500),
            F("Poskod_jkr_sit", 162, 400, 500),
            F("Koordinat_GPS_X_jkr_sit", 162, 400, 500),
            F("Koordinat_GPS_Y_jkr_sit", 162, 400, 500),
            F("Kumpulan_Agensi_jkr_sit", 162, 400, 500),
            F("Kementerian_jkr_sit", 162, 400, 500),
            F("Jabatan_jkr_sit", 162, 400, 500),
            F("Negara_jkr_sit", 162, 400, 500),
            F("Negeri_jkr_sit", 162, 400, 500),
            F("Daerah_jkr_sit", 162, 400, 500),
            F("Mukim_Bandar_jkr_sit", 162, 400, 500),
            F("Kategori_Premis_jkr_sit", 162, 400, 500),
            F("Sub_Kategori_Premis_jkr_sit", 163, 400, 500),
            F("Jumlah_Keluasan_Premis_jkr_sia", 163, 400, 500),
            F("Status_Premis_jkr_sit", 163, 500, 500),
            F("Aset_Warisan_jkr_siy", 163, 400, 500),
            F("Kos_Bina_Asal_jkr_sic", 163, 400, 500),
            F("Kos_Tambahan_jkr_sic", 163, 500, 500),
            F("Kos_Keseluruhan_jkr_sic", 163, 500, 500),
            F("Tahun_Siap_Bina_Asal_jkr_sit", 163, 400, 500),
            F("Tarikh_Siap_Bina_Asal_jkr_sit", 163, 400, 500),
            F("Sumber_Pembiayaan_jkr_sit", 163, 400, 500),
            F("Kod_PTJ_jkr_sit", 163, 400, 500),
            F("Nilai_Semasa_jkr_sic", 163, 500, 500),
            F("Tarikh_Penilaian_jkr_sit", 163, 500, 500),
            F("Bilangan_Blok_Bangunan_jkr_sii", 163, 400, 500),
            F("Bilangan_Binaan_Luar_jkr_sii", 163, 400, 500),
            F("No_DPA_jkr_sit", 163, 500, 500),
            F("Kod_Blok_jkr_sit", 163, 400, 500),
            F("Kod_Binaan_Luar_jkr_sit", 163, 400, 500),
            F("Nama_Blok_jkr_sit", 163, 400, 500),
            F("Nama_Binaan_Luar_jkr_sit", 163, 400, 500),
            F("Fungsi_Binaan_jkr_sit", 163, 400, 500),
            F("Jenis_Binaan_Luar_jkr_sit", 163, 400, 500),
            F("Kontraktor_Utama_jkr_sit", 163, 400, 500),
            F("Kontraktor_Tambahan_jkr_six", 163, 400, 500),
            F("Bidang_Kerja_Kontraktor_Utama_jkr_sit", 163, 400, 500),
            F("Bidang_Kerja_Kontraktor_Tambahan_jkr_six", 163, 400, 500),
            F("Juru_Perunding_Utama_jkr_sit", 164, 400, 500),
            F("Juru_Perunding_Tambahan_jkr_six", 164, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Utama_jkr_sit", 164, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Tambahan_jkr_six", 164, 400, 500),
            F("Fungsi_Asal_jkr_sit", 164, 400, 500),
            F("Jenis_Struktur_jkr_sit", 164, 400, 500),
            F("No_Siri_Pendaftaran_jkr_sit", 164, 500, 500),
            F("Jangka_Hayat_Tahun_jkr_sii", 164, 400, 500),
            F("Kapasiti_Penghuni_Asal_jkr_sit", 164, 400, 500),
            F("Penggunaan_Tenaga_jkr_sit", 164, 500, 500),
            F("Penggunaan_Air_jkr_sit", 164, 500, 500),
            F("Jenis_Milikan_jkr_sit", 164, 400, 500),
            F("Status_Blok_jkr_sit", 164, 500, 500),
            F("Status_Binaan_Luar_jkr_sit", 164, 500, 500),
            F("Bilangan_Aras_Atas_Tanah_jkr_sii", 164, 400, 500),
            F("Bilangan_Aras_Bawah_Tanah_jkr_sii", 164, 400, 500),
            F("Binaan_Luar_jkr_siy", 164, 400, 500),
        };

        /// <summary>EL, Category: Project Information — PB p.209–212 ("SENARAI PARAMETER ELEKTRIK").</summary>
        public static readonly IReadOnlyList<PbField> ProjectInfoEl = new[]
        {
            // p.209: five Identity Data names wrap onto a second line in the book; joined as printed
            //        (two keep the book's space: "Perunding Reka Bentuk", "Perkhidmatan Pakar").
            // p.209: No_Model_BIM_jkr_sit has an example value but no LOi ticked (0, 0).
            F("Pengarah_Kanan_Elektrik_jkr_sit", 209, 300, 500),
            F("Pengarah_Perunding Reka Bentuk_Elektrik_jkr_sit", 209, 300, 500),
            F("Pengarah_Perkhidmatan Pakar_Elektrik_jkr_sit", 209, 300, 500),
            F("Jurutera_Elektrik_Penguasa_Kanan_jkr_sit", 209, 300, 500),
            F("Jurutera_Elektrik_Penguasa_jkr_sit", 209, 300, 500),
            F("Jurutera_Elektrik_Kanan_jkr_sit", 209, 300, 500),
            F("Jurutera_Elektrik_jkr_sit", 209, 300, 500),
            F("No_Model_BIM_jkr_sit", 209, 0, 0),
            F("Sistem_Elektrik_jkr_sit", 209, 300, 500),
            F("Alamat_Pelanggan_jkr_six", 209, 100, 500),
            F("Client Name", 209, 100, 500),
            F("Project Address", 209, 100, 500),
            F("Project Name", 209, 100, 500),
            F("Kos_Projek_jkr_sic", 209, 100, 500),
            F("Harga_Kontrak_jkr_sic", 210, 300, 500),
            F("Kedudukan_Project_Base_Point_jkr_sit", 210, 100, 500),
            F("Tarikh_Milik_Tapak_jkr_sit", 210, 300, 500),
            F("Tarikh_Kelulusan_Bomba_jkr_sit", 210, 300, 500),
            F("Tarikh_Kelulusan_PBT_jkr_sit", 210, 300, 500),
            F("Tarikh_Kelulusan_Pelanggan_jkr_sit", 210, 300, 500),
            F("Tarikh_CMGD_jkr_sit", 210, 400, 500),
            F("Tarikh_CPC_jkr_sit", 210, 400, 500),
            F("Template_Asal_jkr_sit", 210, 100, 500),
            F("URL_Projek_jkr_siu", 210, 300, 500),
            F("Nama_Premis_jkr_sit", 210, 400, 500),
            F("Alamat_Premis_jkr_sit", 210, 400, 500),
            F("Poskod_jkr_sit", 210, 400, 500),
            F("Koordinat_GPS_X_jkr_sit", 210, 400, 500),
            F("Koordinat_GPS_Y_jkr_sit", 210, 400, 500),
            F("Kumpulan_Agensi_jkr_sit", 210, 400, 500),
            F("Kementerian_jkr_sit", 210, 400, 500),
            F("Jabatan_jkr_sit", 210, 400, 500),
            F("Negara_jkr_sit", 210, 400, 500),
            F("Negeri_jkr_sit", 210, 400, 500),
            F("Daerah_jkr_sit", 210, 400, 500),
            F("Mukim_Bandar_jkr_sit", 210, 400, 500),
            F("Kategori_Premis_jkr_sit", 210, 400, 500),
            F("Sub_Kategori_Premis_jkr_sit", 211, 400, 500),
            F("Jumlah_Keluasan_Premis_jkr_sia", 211, 400, 500),
            F("Status_Premis_jkr_sit", 211, 500, 500),
            F("Aset_Warisan_jkr_siy", 211, 400, 500),
            F("Kos_Bina_Asal_jkr_sic", 211, 400, 500),
            F("Kos_Tambahan_jkr_sic", 211, 500, 500),
            F("Kos_Keseluruhan_jkr_sic", 211, 500, 500),
            F("Tahun_Siap_Bina_Asal_jkr_sit", 211, 400, 500),
            F("Tarikh_Siap_Bina_Asal_jkr_sit", 211, 400, 500),
            F("Sumber_Pembiayaan_jkr_sit", 211, 400, 500),
            F("Kod_PTJ_jkr_sit", 211, 400, 500),
            F("Nilai_Semasa_jkr_sic", 211, 500, 500),
            F("Tarikh_Penilaian_jkr_sit", 211, 500, 500),
            F("Bilangan_Blok_Bangunan_jkr_sii", 211, 400, 500),
            F("Bilangan_Binaan_Luar_jkr_sii", 211, 400, 500),
            F("No_DPA_jkr_sit", 211, 500, 500),
            F("Kod_Blok_jkr_sit", 211, 400, 500),
            F("Kod_Binaan_Luar_jkr_sit", 211, 400, 500),
            F("Nama_Blok_jkr_sit", 211, 400, 500),
            F("Nama_Binaan_Luar_jkr_sit", 211, 400, 500),
            F("Fungsi_Binaan_jkr_sit", 211, 400, 500),
            F("Jenis_Binaan_Luar_jkr_sit", 211, 400, 500),
            F("Kontraktor_Utama_jkr_sit", 211, 400, 500),
            F("Kontraktor_Tambahan_jkr_six", 211, 400, 500),
            F("Bidang_Kerja_Kontraktor_Utama_jkr_sit", 211, 400, 500),
            F("Bidang_Kerja_Kontraktor_Tambahan_jkr_six", 211, 400, 500),
            F("Juru_Perunding_Utama_jkr_sit", 212, 400, 500),
            F("Juru_Perunding_Tambahan_jkr_sit", 212, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Utama_jkr_sit", 212, 400, 500),
            F("Bidang_Kerja_Juru_Perunding_Tambahan_jkr_sit", 212, 400, 500),
            F("Fungsi_Asal_jkr_sit", 212, 400, 500),
            F("Jenis_Struktur_jkr_sit", 212, 400, 500),
            F("No_Siri_Pendaftaran_jkr_sit", 212, 500, 500),
            F("Jangka_Hayat_Tahun_jkr_sii", 212, 400, 500),
            F("Kapasiti_Penghuni_Asal_jkr_sit", 212, 400, 500),
            F("Penggunaan_Tenaga_jkr_sit", 212, 500, 500),
            F("Penggunaan_Air_jkr_sit", 212, 500, 500),
            F("Jenis_Milikan_jkr_sit", 212, 400, 500),
            F("Status_Blok_jkr_sit", 212, 500, 500),
            F("Status_Binaan_Luar_jkr_sit", 212, 500, 500),
            F("Bilangan_Aras_Atas_Tanah_jkr_sii", 212, 400, 500),
            F("Bilangan_Aras_Bawah_Tanah_jkr_sii", 212, 400, 500),
            F("Binaan_Luar_jkr_siy", 212, 400, 500),
        };

        /// <summary>AR, Category: Area — PB p.36 (columns of an area schedule).</summary>
        public static readonly IReadOnlyList<PbField> AreaFields = new[]
        {
            F("Luas_Brif_jkr_sia", 36, 100, 300),
            F("Name", 36, 100, 200),
            F("Kod_Department_jkr_sit", 36, 100, 200),
        };

        /// <summary>AR, Category: Room — PB p.37 (the room schedule's mandatory columns, C1.6.3).</summary>
        public static readonly IReadOnlyList<PbField> RoomFields = new[]
        {
            // Diterima_jkr_siy: the ✓ under "Contoh" is its example value (Yes), not an LOi tick.
            F("Diterima_jkr_siy", 37, 200, 300),
            F("Luas_Brif_jkr_sia", 37, 200, 300),
            F("Kod_Ruang_AR_jkr_stt", 37, 200, 500),
            F("Number", 37, 200, 500),
            F("Name", 37, 200, 500),
            F("Occupancy", 37, 200, 500),
            F("Department", 37, 200, 500),
            F("Ceiling Finish", 37, 200, 500),
            F("Wall Finish", 37, 200, 500),
            F("Floor Finish", 37, 200, 500),
            F("Tinggi_Siling_jkr_sil", 37, 200, 500),
            F("Kod_Kambi_jkr_sit", 37, 200, 500),
            F("Kod_Sistem_Pengudaraan_jkr_sit", 37, 200, 500),
            F("Kod_Department_jkr_sit", 37, 200, 500),
            F("Kod_DAK_Lokasi_jkr_sit", 37, 400, 500),
        };

        /// <summary>AR, Category: Sheets — PB p.103. The book prints no LOi 100 / 200 ticks here.</summary>
        public static readonly IReadOnlyList<PbField> SheetFields = new[]
        {
            F("No_Lukisan_jkr_sit", 103, 300, 500),
            F("No_Lukisan_Elektronik_jkr_sit", 103, 300, 500),
            F("Checked By", 103, 300, 500),
            F("Drawn By", 103, 300, 500),
            F("Sheet Number", 103, 300, 500),
            F("Sheet Name", 103, 300, 500),
            F("Sheet_jkr_pit", 103, 300, 500),
            F("Jenis_Lukisan_jkr_pit", 103, 300, 500),
        };

        /// <summary>
        /// The AR model categories of the Buku Parameter, PB p.36–99 (table of contents p.9;
        /// Tests/data/jkr_spec_pb_categories.tsv). Project Information (p.30), Material Identity
        /// (p.35) and Sheets (p.103) are not model categories and are left out: C3.1.1 asks
        /// for a schedule per modelled category.
        /// </summary>
        public static readonly IReadOnlyList<PbCategory> ArCategories = new[]
        {
            new PbCategory("Area", 36, "Areas"),
            new PbCategory("Room", 37, "Rooms"),
            new PbCategory("Casework", 38, "Casework"),
            new PbCategory("Ceiling", 42, "Ceilings"),
            new PbCategory("Column", 45, "Columns"),
            new PbCategory("Curtain System", 48, "Curtain Systems", "Curtain Panels"),
            new PbCategory("Door", 51, "Doors"),
            new PbCategory("Floor", 55, "Floors"),
            new PbCategory("Furniture", 59, "Furniture"),
            new PbCategory("Furniture System", 63, "Furniture Systems"),
            new PbCategory("Generic Model", 67, "Generic Models"),
            new PbCategory("Planting", 70, "Planting"),
            new PbCategory("Plumbing Fixture", 72, "Plumbing Fixtures"),
            new PbCategory("Ramp", 76, "Ramps"),
            new PbCategory("Railing", 80, "Railings"),
            new PbCategory("Roof", 83, "Roofs"),
            new PbCategory("Specialty Equipment", 87, "Specialty Equipment"),
            new PbCategory("Stair", 91, "Stairs"),
            new PbCategory("Wall", 95, "Walls"),
            new PbCategory("Window", 99, "Windows"),
        };

        /// <summary>What the transcription could not read reliably (reported, never guessed).</summary>
        public static readonly IReadOnlyList<string> Gaps = new[]
        {
            "PB p.161 (ME): 'Pengarah_Kanan_Mekanikal' is printed without its _jkr_sit suffix; matched by prefix.",
            "PB p.163 (ME): Bilangan_Blok_Bangunan_jkr_sii ticks are displaced in the text layer; read as LOi 400–500.",
            "PB p.209 (EL): No_Model_BIM_jkr_sit has no LOi ticked; never counted as mandatory.",
            "PB p.209 (EL): wrapped names joined as printed ('Pengarah_Perunding Reka Bentuk_Elektrik_jkr_sit', 'Pengarah_Perkhidmatan Pakar_Elektrik_jkr_sit'); matched ignoring spaces and underscores.",
            "PB p.120 (ST): the ST Project Information table has no Identity Data rows (no ST engineer / modeller fields), as printed.",
        };

        /// <summary>The Project Information list for a discipline (AR / ST / ME / EL); null for any
        /// other (CD: the book has no Project Information table for it).</summary>
        public static IReadOnlyList<PbField> ProjectInfoFor(string discipline)
        {
            switch ((discipline ?? "").Trim().ToUpperInvariant())
            {
                case "AR": return ProjectInfoAr;
                case "ST": return ProjectInfoSt;
                case "ME": return ProjectInfoMe;
                case "EL": return ProjectInfoEl;
                default: return null;
            }
        }

        /// <summary>"PB p.30–34" for the discipline's Project Information table; "" when none.</summary>
        public static string ProjectInfoPages(string discipline)
        {
            switch ((discipline ?? "").Trim().ToUpperInvariant())
            {
                case "AR": return "PB p.30–34";
                case "ST": return "PB p.120–123";
                case "ME": return "PB p.161–164";
                case "EL": return "PB p.209–212";
                default: return "";
            }
        }

        /// <summary>The LOi column a project LOD falls under: 100, 200 … 500 (LOD 350 → 300,
        /// above 500 → 500); 0 when the LOD is unknown (below 100).</summary>
        public static int LoiOf(int lod)
        {
            if (lod < 100) return 0;
            if (lod >= 500) return 500;
            return lod / 100 * 100;
        }

        /// <summary>The fields the book marks at <paramref name="loi"/>, in book order.</summary>
        public static List<PbField> RequiredAt(IEnumerable<PbField> fields, int loi) =>
            (fields ?? Enumerable.Empty<PbField>()).Where(f => f != null && f.RequiredAt(loi)).ToList();

        /// <summary>The fields marked at every LOi (100–500): mandatory whatever the project LOD.</summary>
        public static List<PbField> RequiredAtEveryLoi(IEnumerable<PbField> fields) =>
            (fields ?? Enumerable.Empty<PbField>()).Where(f => f != null && f.FromLoi == 100 && f.ToLoi == 500).ToList();

        /// <summary>The field has an LOi gap in the transcription (none ticked).</summary>
        public static bool HasGaps(IEnumerable<PbField> fields) =>
            (fields ?? Enumerable.Empty<PbField>()).Any(f => f != null && !f.HasLoi);

        /// <summary>Name key for matching a book name with a Revit parameter name: lower case,
        /// letters and digits only ("Pengarah_Perunding Reka Bentuk" = "pengarahperundingrekabentuk").</summary>
        public static string Key(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in name ?? "")
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>
        /// Find the field among Revit parameters (name → value): exact name (any case) first,
        /// then the same letters and digits, then — for a name the book prints without its
        /// suffix — a parameter that is the name followed by "jkr…". False when the model has
        /// no such parameter.
        /// </summary>
        public static bool TryFind(IDictionary<string, string> parameters, string fieldName, out string paramName, out string value)
        {
            paramName = ""; value = "";
            if (parameters == null || string.IsNullOrWhiteSpace(fieldName)) return false;
            var want = fieldName.Trim();
            foreach (var kv in parameters)
                if (kv.Key != null && string.Equals(kv.Key.Trim(), want, StringComparison.OrdinalIgnoreCase))
                { paramName = kv.Key; value = kv.Value ?? ""; return true; }
            var key = Key(want);
            if (key.Length == 0) return false;
            foreach (var kv in parameters)
                if (kv.Key != null && Key(kv.Key) == key)
                { paramName = kv.Key; value = kv.Value ?? ""; return true; }
            if (key.Contains("jkr")) return false;   // only a name printed without its suffix
            foreach (var kv in parameters)
            {
                var k = Key(kv.Key);
                if (k.Length > key.Length && k.StartsWith(key, StringComparison.Ordinal) && k.Substring(key.Length).StartsWith("jkr", StringComparison.Ordinal))
                { paramName = kv.Key; value = kv.Value ?? ""; return true; }
            }
            return false;
        }
    }
}
